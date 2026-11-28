using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace QuotaAI.Core;

public sealed class ClaudeProvider : IUsageProvider
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string BetaHeader = "oauth-2025-04-20";
    private const string UserAgentValue = "claude-code/2.1.0";
    private const string WinCredTarget = "Claude Code-credentials";

    private const string ExpiredMessage =
        "Claude Code sign-in expired. Run Claude Code once to refresh it.";

    public string Id => "claude";
    public string DisplayName => "Claude";
    public string ShortCode => "Cl";
    public string? DashboardUrl => "https://claude.ai/settings/usage";
    public string? CredentialHelpText => FindClaudeExecutable() is { } executable
        ? $"To sign in from PowerShell, run: & '{executable.Replace("'", "''")}' auth login"
        : "Install Claude Code CLI and sign in with `claude auth login`.";

    private static string CredentialsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    public bool IsDetected =>
        File.Exists(CredentialsPath) || WindowsCredentialStore.ExternalExists(WinCredTarget)
        || FindClaudeExecutable() is not null;

    private static string? FindClaudeExecutable()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Path.IsPathFullyQualified(home))
        {
            var executable = Path.Combine(home, ".local", "bin", "claude.exe");
            if (File.Exists(executable))
                return executable;
        }

        return CodexAppServer.FindOnPath("claude.exe");
    }

    public async Task<ProviderUsage> FetchAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        // Read-only: never use Claude Code's refresh token. Refresh tokens are
        // single-use, so refreshing here would sign Claude Code out. Claude Code
        // renews the stored token itself the next time it runs.
        var creds = LoadCredentials();
        if (creds.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow.AddSeconds(30))
            throw new ProviderUnavailableException(ExpiredMessage);

        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("anthropic-beta", BetaHeader);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgentValue);

        using var resp = await SharedHttp.Client.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new ProviderUnavailableException(ExpiredMessage);
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            throw new ProviderUnavailableException("Anthropic rate-limited the usage request.");
        if (!resp.IsSuccessStatusCode)
            throw new ProviderUnavailableException($"Anthropic returned HTTP {(int)resp.StatusCode}.");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return ParseUsage(doc.RootElement, creds.SubscriptionType, DateTimeOffset.Now);
    }

    internal static ProviderUsage ParseUsage(JsonElement root, string? subscriptionType, DateTimeOffset now)
    {
        var windows = new List<UsageWindow>();
        void AddWindow(string jsonKey, string label)
        {
            if (root.TryGetProperty(jsonKey, out var w) && w.ValueKind == JsonValueKind.Object
                && w.TryGetProperty("utilization", out var u) && u.TryGetDouble(out var util) && double.IsFinite(util))
            {
                var reset = w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
                    ? SharedHttp.ParseIsoTimestamp(r.GetString())
                    : null;
                windows.Add(new UsageWindow(label, Math.Clamp(util, 0, 100), reset));
            }
        }

        AddWindow("five_hour", "5h limit");
        AddWindow("seven_day", "Weekly limit");
        AddWindow("seven_day_opus", "Weekly (Opus)");
        AddWindow("seven_day_sonnet", "Weekly (Sonnet)");

        var plan = string.IsNullOrWhiteSpace(subscriptionType)
            ? null
            : char.ToUpperInvariant(subscriptionType[0]) + subscriptionType[1..];
        return new ProviderUsage(plan, windows, now);
    }

    private sealed record Credentials(string AccessToken, DateTimeOffset? ExpiresAt, string? SubscriptionType);

    private static Credentials LoadCredentials()
    {
        if (File.Exists(CredentialsPath))
        {
            try
            {
                var parsed = DecodeCredentials(File.ReadAllText(CredentialsPath));
                if (parsed is not null)
                    return parsed;
            }
            catch (IOException) { }
        }

        if (WindowsCredentialStore.ReadRawCredentialBytes(WinCredTarget) is { } bytes)
        {
            var parsed = DecodeCredentials(WindowsCredentialStore.DecodeSecretBytes(bytes));
            if (parsed is not null)
                return parsed;
        }

        throw new ProviderUnavailableException("Not signed in to Claude Code.");
    }

    private static Credentials? DecodeCredentials(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object)
                return null;
            var access = oauth.TryGetProperty("accessToken", out var a) ? a.GetString() : null;
            if (string.IsNullOrWhiteSpace(access))
                return null;

            DateTimeOffset? expiresAt = null;
            if (oauth.TryGetProperty("expiresAt", out var exp) && exp.TryGetDouble(out var ms) && ms > 0)
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds((long)ms);

            var sub = oauth.TryGetProperty("subscriptionType", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;
            return new Credentials(access, expiresAt, sub);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
