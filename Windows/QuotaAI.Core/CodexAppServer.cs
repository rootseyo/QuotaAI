using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace QuotaAI.Core;

public sealed class CodexUnavailableException(string message) : Exception(message);

public static class CodexAppServer
{
    private const string Init = """{"id":1,"method":"initialize","params":{"clientInfo":{"name":"QuotaAI","version":"1.0"}}}""";
    private const string Initialized = """{"method":"initialized"}""";
    private const string Limits = """{"id":2,"method":"account/rateLimits/read","params":{}}""";
    private const string Account = """{"id":3,"method":"account/read","params":{}}""";

    public static Task<CodexUsage> ReadAsync(CancellationToken cancellationToken) =>
        ReadAsync(codexHome: null, includeAccountRead: false, cancellationToken);

    public static Task<CodexUsage> ReadAsync(string? codexHome, bool includeAccountRead, CancellationToken cancellationToken)
    {
        var executable = FindOnPath("codex.exe");
        if (executable is not null)
            return ReadAsync(executable, ["app-server"], codexHome, includeAccountRead, cancellationToken);

        var node = FindOnPath("node.exe");
        var npm = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (node is not null)
        {
            var roots = PathDirectories().Append(Path.GetDirectoryName(node)!);
            if (Path.IsPathFullyQualified(npm))
                roots = roots.Append(Path.Combine(npm, "npm"));
            if (Path.IsPathFullyQualified(localAppData))
                roots = roots.Append(Path.Combine(localAppData, "pnpm", "global", "5"));

            foreach (var root in roots)
            {
                var script = Path.Combine(root, "node_modules", "@openai", "codex", "bin", "codex.js");
                if (File.Exists(script))
                    return ReadAsync(node, [script, "app-server"], codexHome, includeAccountRead, cancellationToken);
            }
        }

        throw new CodexUnavailableException("Install the Codex CLI and add it to PATH.");
    }

    internal static Task<CodexUsage> ReadAsync(
        string executable, string[] arguments, CancellationToken cancellationToken) =>
        ReadAsync(executable, arguments, codexHome: null, includeAccountRead: false, cancellationToken);

    internal static async Task<CodexUsage> ReadAsync(
        string executable,
        string[] arguments,
        string? codexHome,
        bool includeAccountRead,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var start = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8
        };
        if (!string.IsNullOrWhiteSpace(codexHome))
            start.Environment["CODEX_HOME"] = codexHome;

        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            token.ThrowIfCancellationRequested();
            if (!process.Start())
                throw new CodexUnavailableException("Could not start the Codex CLI.");
        }
        catch (Win32Exception)
        {
            throw new CodexUnavailableException("Could not start the Codex CLI.");
        }

        try
        {
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginErrorReadLine();
            await WriteAsync(process.StandardInput, Init, token).ConfigureAwait(false);
            await ReadResultAsync(process.StandardOutput, 1, token).ConfigureAwait(false);
            await WriteAsync(process.StandardInput, Initialized, token).ConfigureAwait(false);

            string? email = null;
            string? planType = null;
            if (includeAccountRead)
            {
                await WriteAsync(process.StandardInput, Account, token).ConfigureAwait(false);
                try
                {
                    var accountResult = await ReadResultAsync(process.StandardOutput, 3, token).ConfigureAwait(false);
                    if (accountResult.ValueKind == JsonValueKind.Object
                        && accountResult.TryGetProperty("account", out var acct)
                        && acct.ValueKind == JsonValueKind.Object)
                    {
                        if (acct.TryGetProperty("email", out var em) && em.ValueKind == JsonValueKind.String)
                            email = em.GetString();
                        if (acct.TryGetProperty("planType", out var pt) && pt.ValueKind == JsonValueKind.String)
                            planType = pt.GetString();
                    }
                }
                catch (CodexUnavailableException)
                {
                    // Optional enrichment; rateLimits/read remains authoritative.
                }
            }

            await WriteAsync(process.StandardInput, Limits, token).ConfigureAwait(false);
            var result = await ReadResultAsync(process.StandardOutput, 2, token).ConfigureAwait(false);
            return CodexUsage.Parse(result, email, planType);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CodexUnavailableException("Codex did not respond in time.");
        }
        catch (IOException)
        {
            throw new CodexUnavailableException("Lost the connection to the Codex CLI.");
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
    }

    private static async Task WriteAsync(StreamWriter writer, string message, CancellationToken token)
    {
        await writer.WriteLineAsync(message.AsMemory(), token).ConfigureAwait(false);
        await writer.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadResultAsync(StreamReader reader, int expectedId, CancellationToken token)
    {
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var reply = json.RootElement;
                if (reply.ValueKind != JsonValueKind.Object || !reply.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var responseId)
                    || responseId != expectedId)
                {
                    continue;
                }

                if (reply.TryGetProperty("result", out var result))
                    return result.Clone();

                if (reply.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                    && message.GetString() is { } reason
                    && (reason.Contains("auth", StringComparison.OrdinalIgnoreCase)
                        || reason.Contains("login", StringComparison.OrdinalIgnoreCase)
                        || reason.Contains("sign in", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new CodexUnavailableException("Sign in to the Codex CLI with ChatGPT, then refresh.");
                }

                throw new CodexUnavailableException("Codex could not report usage. Check your CLI and account.");
            }
            catch (JsonException)
            {
            }
        }

        throw new CodexUnavailableException("The Codex CLI exited before returning usage.");
    }

    internal static string? FindOnPath(string name) => PathDirectories()
        .Select(directory => Path.Combine(directory, name))
        .FirstOrDefault(File.Exists);

    private static IEnumerable<string> PathDirectories() => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(directory => directory.Trim('"'))
        .Where(Path.IsPathFullyQualified);
}
