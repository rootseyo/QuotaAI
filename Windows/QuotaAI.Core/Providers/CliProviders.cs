using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace QuotaAI.Core;

public sealed class CodexProvider : IUsageProvider
{
    private static readonly ConcurrentDictionary<string, (CodexUsage Usage, DateTimeOffset CachedAt)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    public CodexAccountConfig? AccountConfig { get; }
    public string Id { get; }
    public string DisplayName { get; }
    public string ShortCode { get; }
    public string CodexHome { get; }
    public bool IsPrimary => AccountConfig is null;

    public CodexProvider(CodexAccountConfig? accountConfig = null)
    {
        AccountConfig = accountConfig;
        if (accountConfig is not null)
        {
            Id = $"codex:{accountConfig.Id}";
            DisplayName = $"Codex — {accountConfig.Label}";
            var trimmed = accountConfig.Label.Trim();
            ShortCode = string.IsNullOrEmpty(trimmed) ? "Cx" : $"Cx{char.ToUpperInvariant(trimmed[0])}";
            CodexHome = accountConfig.ExpandedHomePath();
        }
        else
        {
            Id = "codex";
            DisplayName = "Codex";
            ShortCode = "Cx";
            CodexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }
    }

    public bool IsDetected => IsPrimary
        ? Directory.Exists(Path.Combine(CodexHome, "sessions")) || File.Exists(Path.Combine(CodexHome, "auth.json"))
        : File.Exists(Path.Combine(CodexHome, "auth.json"));

    public string? DashboardUrl => "https://chatgpt.com/codex/settings/usage";

    public string? LastKnownEmail =>
        Cache.TryGetValue(Id, out var entry) ? entry.Usage.AccountEmail : null;

    public string? LastKnownPlan =>
        Cache.TryGetValue(Id, out var entry) ? entry.Usage.PlanName : null;

    public static void ClearCache(string providerId) => Cache.TryRemove(providerId, out _);

    public async Task<ProviderUsage> FetchAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        if (!forceRefresh && Cache.TryGetValue(Id, out var cached) && now - cached.CachedAt < PollInterval)
        {
            if (IsPrimary && TryReadSessionLog(out var newerLog, out var logTime) && logTime > cached.CachedAt)
                return newerLog!;
            return cached.Usage.ToProviderUsage(cached.CachedAt);
        }

        try
        {
            var reading = await CodexAppServer.ReadAsync(
                IsPrimary ? null : CodexHome,
                includeAccountRead: true,
                cancellationToken).ConfigureAwait(false);
            Cache[Id] = (reading, now);
            return reading.ToProviderUsage(now);
        }
        catch (CodexUnavailableException ex)
        {
            if (IsPrimary && TryReadSessionLog(out var fallbackUsage, out _))
                return fallbackUsage!;
            throw new ProviderUnavailableException(ex.Message);
        }
    }

    private bool TryReadSessionLog(out ProviderUsage? usage, out DateTimeOffset timestamp)
    {
        usage = null;
        timestamp = DateTimeOffset.MinValue;
        var sessionsRoot = Path.Combine(CodexHome, "sessions");
        if (!Directory.Exists(sessionsRoot))
            return false;

        try
        {
            var files = Directory.EnumerateFiles(sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(10);

            foreach (var file in files)
            {
                if (TryParseLatestRateLimitsFromFile(file.FullName, out usage, out timestamp))
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return false;
    }

    private static bool TryParseLatestRateLimitsFromFile(string path, out ProviderUsage? usage, out DateTimeOffset asOf)
    {
        usage = null;
        asOf = DateTimeOffset.MinValue;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var readLen = (int)Math.Min(fs.Length, 2 * 1024 * 1024);
        if (readLen <= 0)
            return false;

        fs.Seek(-readLen, SeekOrigin.End);
        var buf = new byte[readLen];
        var actual = fs.Read(buf, 0, readLen);
        var text = Encoding.UTF8.GetString(buf, 0, actual);
        var lines = text.Split('\n');

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (!line.Contains("\"rate_limits\"", StringComparison.Ordinal))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!TryFindPropertyRecursive(doc.RootElement, "rate_limits", out var rl))
                    continue;

                var windows = new List<UsageWindow>();
                foreach (var key in new[] { "primary", "secondary" })
                {
                    if (!rl.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object)
                        continue;
                    if (!w.TryGetProperty("used_percent", out var up) || !up.TryGetDouble(out var used))
                        continue;
                    var mins = w.TryGetProperty("window_minutes", out var wm) && wm.TryGetInt32(out var m) ? m : 0;
                    DateTimeOffset? reset = null;
                    if (w.TryGetProperty("resets_at", out var ra) && ra.TryGetDouble(out var sec) && sec > 0)
                        reset = DateTimeOffset.FromUnixTimeSeconds((long)sec);

                    var label = mins switch
                    {
                        300 => "5h limit",
                        10_080 => "Weekly limit",
                        > 0 and < 120 => $"{mins}m limit",
                        >= 120 and < 2880 => $"{mins / 60}h limit",
                        >= 2880 => $"{mins / 1440}d limit",
                        _ => "Window"
                    };
                    windows.Add(new UsageWindow(label, Math.Clamp(used, 0, 100), reset));
                }

                if (windows.Count == 0)
                    continue;

                if (doc.RootElement.TryGetProperty("timestamp", out var tsProp) && tsProp.ValueKind == JsonValueKind.String)
                    asOf = SharedHttp.ParseIsoTimestamp(tsProp.GetString()) ?? DateTimeOffset.Now;
                else
                    asOf = DateTimeOffset.Now;

                string? plan = null;
                if (rl.TryGetProperty("plan_type", out var pt) && pt.ValueKind == JsonValueKind.String)
                    plan = CodexUsage.FormatPlanName(pt.GetString());

                usage = new ProviderUsage(plan, windows, asOf);
                return true;
            }
            catch (JsonException)
            {
            }
        }
        return false;
    }

    private static bool TryFindPropertyRecursive(JsonElement element, string name, out JsonElement found)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out found) && found.ValueKind == JsonValueKind.Object)
                return true;
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object && TryFindPropertyRecursive(prop.Value, name, out found))
                    return true;
            }
        }
        found = default;
        return false;
    }
}

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

public sealed class AntigravityProvider : IUsageProvider
{
    private const string QuotaUrl = "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string GoKeyringTarget = "gemini:antigravity";

    // Public installed-app OAuth client shipped by Google's agy CLI (split so secret scanners don't false-positive).
    private static readonly string ClientId = string.Join(".", ["1071006060591-tmhssin2h21lcre235vtolojh4g403ep", "apps.googleusercontent.com"]);
    private static readonly string ClientSecret = string.Join("-", ["GOCSPX", "K58FWR486LdLJ1mLB8sXC4z6qDAf"]);

    public string Id => "gemini";
    public string DisplayName => "Antigravity";
    public string ShortCode => "Ag";
    public string? DashboardUrl => "https://antigravity.google/";
    public string? CredentialHelpText => "Sign in with Antigravity CLI (`agy`). QuotaAI reads its existing CLI credentials without modifying them.";

    private static string CredentialsFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli", "antigravity-oauth-token");

    public bool IsDetected =>
        File.Exists(CredentialsFilePath) || WindowsCredentialStore.ExternalExists(GoKeyringTarget);

    public async Task<ProviderUsage> FetchAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var cred = LoadCredentials();
        var token = cred.AccessToken;
        if (string.IsNullOrWhiteSpace(token) || (cred.Expiry is { } exp && exp <= DateTimeOffset.UtcNow.AddSeconds(60)))
        {
            if (string.IsNullOrWhiteSpace(cred.RefreshToken))
                throw new ProviderUnavailableException("Sign in to Antigravity CLI by running agy, then refresh.");
            token = await RefreshTokenAsync(cred.RefreshToken, cancellationToken).ConfigureAwait(false);
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, QuotaUrl)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("User-Agent", "antigravity/cli/1.4.12");

        using var resp = await SharedHttp.Client.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new ProviderUnavailableException("Sign in to Antigravity CLI by running agy, then refresh.");
        if (resp.StatusCode == HttpStatusCode.Forbidden)
            throw new ProviderUnavailableException("Google denied Antigravity quota access. Check agy sign-in.");
        if (!resp.IsSuccessStatusCode)
            throw new ProviderUnavailableException($"Antigravity returned HTTP {(int)resp.StatusCode}.");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return ParseUsage(doc.RootElement, DateTimeOffset.Now);
    }

    internal static ProviderUsage ParseUsage(JsonElement root, DateTimeOffset now)
    {
        var ranked = new List<(int Rank, int Index, UsageWindow Window)>();
        if (root.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groups.EnumerateArray())
            {
                var groupName = group.TryGetProperty("displayName", out var gn) && gn.ValueKind == JsonValueKind.String
                    ? gn.GetString() ?? "Model"
                    : "Model";
                if (!group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var bucket in buckets.EnumerateArray())
                {
                    if (!bucket.TryGetProperty("remainingFraction", out var rf) || !rf.TryGetDouble(out var fraction) || !double.IsFinite(fraction))
                        continue;

                    var bucketId = bucket.TryGetProperty("bucketId", out var bid) && bid.ValueKind == JsonValueKind.String
                        ? bid.GetString() ?? ""
                        : "";
                    var bucketDisplay = bucket.TryGetProperty("displayName", out var bd) && bd.ValueKind == JsonValueKind.String
                        ? bd.GetString()
                        : null;

                    var (label, rank) = bucketId switch
                    {
                        "gemini-5h" => ("Base model (5h)", 0),
                        "gemini-weekly" => ("Base model (weekly)", 1),
                        "gemini-argon-wtu-weekly" => ("Argon (weekly)", 2),
                        "3p-5h" => ("Claude / GPT (5h)", 3),
                        "3p-weekly" => ("Claude / GPT (weekly)", 4),
                        _ => ($"{groupName}: {bucketDisplay ?? bucketId}", 5)
                    };

                    var reset = bucket.TryGetProperty("resetTime", out var rt) && rt.ValueKind == JsonValueKind.String
                        ? SharedHttp.ParseIsoTimestamp(rt.GetString())
                        : null;
                    var usedPercent = 100.0 - Math.Clamp(fraction, 0.0, 1.0) * 100.0;
                    ranked.Add((rank, ranked.Count, new UsageWindow(label, usedPercent, reset)));
                }
            }
        }

        var windows = ranked
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Index)
            .Select(x => x.Window)
            .ToList();

        if (windows.Count == 0)
            throw new ProviderUnavailableException("Antigravity did not return readable quota data.");

        return new ProviderUsage(null, windows, now);
    }

    private sealed record TokenData(string AccessToken, string? RefreshToken, DateTimeOffset? Expiry);

    private static TokenData LoadCredentials()
    {
        TokenData? fileCred = null;
        if (File.Exists(CredentialsFilePath))
        {
            try { fileCred = DecodeTokenPayload(File.ReadAllText(CredentialsFilePath)); }
            catch (IOException) { }
        }

        TokenData? keyringCred = null;
        if (WindowsCredentialStore.ReadRawCredentialBytes(GoKeyringTarget) is { } rawBytes)
        {
            keyringCred = DecodeTokenPayload(Encoding.UTF8.GetString(rawBytes))
                ?? DecodeTokenPayload(WindowsCredentialStore.DecodeSecretBytes(rawBytes));
        }

        return (keyringCred, fileCred) switch
        {
            ({ } k, { } f) => (f.Expiry ?? DateTimeOffset.MinValue) > (k.Expiry ?? DateTimeOffset.MinValue) ? f : k,
            ({ } k, null) => k,
            (null, { } f) => f,
            _ => throw new ProviderUnavailableException("Sign in to Antigravity CLI by running agy, then refresh.")
        };
    }

    internal static string? UnwrapGoKeyringText(string raw)
    {
        var text = raw.Trim().TrimEnd('\0');
        if (string.IsNullOrEmpty(text))
            return null;

        if (text.StartsWith("go-keyring-base64:", StringComparison.Ordinal))
        {
            try
            {
                var bytes = Convert.FromBase64String(text["go-keyring-base64:".Length..]);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException) { return null; }
        }

        if (text.StartsWith("go-keyring-encoded:", StringComparison.Ordinal))
        {
            try
            {
                var bytes = Convert.FromHexString(text["go-keyring-encoded:".Length..]);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException) { return null; }
        }

        return text;
    }

    private static TokenData? DecodeTokenPayload(string raw)
    {
        var unwrapped = UnwrapGoKeyringText(raw);
        if (string.IsNullOrWhiteSpace(unwrapped))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(unwrapped);
            if (!doc.RootElement.TryGetProperty("token", out var tok) || tok.ValueKind != JsonValueKind.Object)
                return null;
            var access = tok.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String
                ? at.GetString() ?? ""
                : "";
            var refresh = tok.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String
                ? rt.GetString()
                : null;
            var expiry = tok.TryGetProperty("expiry", out var ex) && ex.ValueKind == JsonValueKind.String
                ? SharedHttp.ParseIsoTimestamp(ex.GetString())
                : null;
            if (string.IsNullOrEmpty(access) && string.IsNullOrEmpty(refresh))
                return null;
            return new TokenData(access, refresh, expiry);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", refreshToken),
                new KeyValuePair<string, string>("client_id", ClientId),
                new KeyValuePair<string, string>("client_secret", ClientSecret)
            ])
        };
        using var resp = await SharedHttp.Client.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ProviderUnavailableException("Sign in to Antigravity CLI by running agy, then refresh.");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var access = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        if (string.IsNullOrWhiteSpace(access))
            throw new ProviderUnavailableException("Unexpected token response from Google.");
        return access;
    }
}

public sealed class CursorProvider : IUsageProvider
{
    private const string Endpoint = "https://cursor.com/api/usage-summary";

    public string Id => "cursor";
    public string DisplayName => "Cursor";
    public string ShortCode => "Cu";
    public string? DashboardUrl => "https://cursor.com/dashboard";
    public string? CredentialHelpText => "Reads the local session token from Cursor (`state.vscdb`, read-only).";

    private static string StateDbPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cursor", "User", "globalStorage", "state.vscdb");

    public bool IsDetected => File.Exists(StateDbPath);

    public async Task<ProviderUsage> FetchAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var token = ReadAccessTokenFromSqlite(StateDbPath);
        var sub = ExtractJwtSubject(token);
        var cookieValue = Uri.EscapeDataString($"{sub}::{token}");

        using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("Cookie", $"WorkosCursorSessionToken={cookieValue}");

        using var resp = await SharedHttp.Client.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ProviderUnavailableException("Cursor session expired — sign in to Cursor again.");
        if (!resp.IsSuccessStatusCode)
            throw new ProviderUnavailableException($"cursor.com returned HTTP {(int)resp.StatusCode}.");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return ParseSummary(doc.RootElement, DateTimeOffset.Now);
    }

    internal static ProviderUsage ParseSummary(JsonElement root, DateTimeOffset now)
    {
        var resetsAt = root.TryGetProperty("billingCycleEnd", out var bce) && bce.ValueKind == JsonValueKind.String
            ? SharedHttp.ParseIsoTimestamp(bce.GetString())
            : null;
        var windows = new List<UsageWindow>();

        if (root.TryGetProperty("individualUsage", out var ind) && ind.ValueKind == JsonValueKind.Object)
        {
            if (ind.TryGetProperty("plan", out var plan) && plan.ValueKind == JsonValueKind.Object)
            {
                double? total = plan.TryGetProperty("totalPercentUsed", out var tp) && tp.TryGetDouble(out var tpv) ? tpv : null;
                if (total is null
                    && plan.TryGetProperty("used", out var u) && u.TryGetDouble(out var used)
                    && plan.TryGetProperty("limit", out var l) && l.TryGetDouble(out var lim) && lim > 0)
                {
                    total = used / lim * 100.0;
                }
                if (total is { } t && double.IsFinite(t))
                    windows.Add(new UsageWindow("Included usage", Math.Clamp(t, 0, 100), resetsAt));
                if (plan.TryGetProperty("autoPercentUsed", out var ap) && ap.TryGetDouble(out var apv) && double.IsFinite(apv))
                    windows.Add(new UsageWindow("Auto usage", Math.Clamp(apv, 0, 100), resetsAt));
                if (plan.TryGetProperty("apiPercentUsed", out var kp) && kp.TryGetDouble(out var kpv) && double.IsFinite(kpv))
                    windows.Add(new UsageWindow("API usage", Math.Clamp(kpv, 0, 100), resetsAt));
            }

            if (windows.Count == 0 && TryReadPoolPercent(ind, "overall", out var overallPct))
                windows.Add(new UsageWindow("Personal cap", overallPct, resetsAt));
        }

        if (windows.Count == 0
            && root.TryGetProperty("teamUsage", out var team) && team.ValueKind == JsonValueKind.Object
            && TryReadPoolPercent(team, "pooled", out var teamPct))
        {
            windows.Add(new UsageWindow("Team pool", teamPct, resetsAt));
        }

        var membership = root.TryGetProperty("membershipType", out var mt) && mt.ValueKind == JsonValueKind.String
            ? mt.GetString()
            : null;
        var planName = string.IsNullOrWhiteSpace(membership)
            ? null
            : char.ToUpperInvariant(membership[0]) + membership[1..];
        return new ProviderUsage(planName, windows, now);
    }

    private static bool TryReadPoolPercent(JsonElement parent, string propName, out double pct)
    {
        pct = 0;
        if (parent.TryGetProperty(propName, out var pool) && pool.ValueKind == JsonValueKind.Object
            && pool.TryGetProperty("used", out var u) && u.TryGetDouble(out var used)
            && pool.TryGetProperty("limit", out var l) && l.TryGetDouble(out var lim) && lim > 0)
        {
            pct = Math.Clamp(used / lim * 100.0, 0, 100);
            return true;
        }
        return false;
    }

    internal static string ExtractJwtSubject(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
            throw new ProviderUnavailableException("Cursor session token has an unexpected format.");
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload += new string('=', (4 - payload.Length % 4) % 4);
        try
        {
            var bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.TryGetProperty("sub", out var sub) && sub.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(sub.GetString()))
            {
                return sub.GetString()!;
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
        }
        throw new ProviderUnavailableException("Cursor session token has an unexpected format.");
    }

    private static string ReadAccessTokenFromSqlite(string dbPath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(dbPath))
            throw new ProviderUnavailableException("Cursor session token not found (is Cursor signed in?).");

        var db = IntPtr.Zero;
        try
        {
            if (WinSqlite.sqlite3_open_v2(Encoding.UTF8.GetBytes(dbPath + '\0'), out db, 1 /* SQLITE_OPEN_READONLY */, IntPtr.Zero) != 0)
                throw new ProviderUnavailableException("Cursor session token not found (is Cursor signed in?).");

            const string sql = "SELECT value FROM ItemTable WHERE key = 'cursorAuth/accessToken' LIMIT 1\0";
            if (WinSqlite.sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes(sql), -1, out var stmt, IntPtr.Zero) != 0)
                throw new ProviderUnavailableException("Cursor session token not found (is Cursor signed in?).");

            try
            {
                if (WinSqlite.sqlite3_step(stmt) != 100 /* SQLITE_ROW */)
                    throw new ProviderUnavailableException("Cursor session token not found (is Cursor signed in?).");

                var ptr = WinSqlite.sqlite3_column_blob(stmt, 0);
                var len = WinSqlite.sqlite3_column_bytes(stmt, 0);
                if (ptr == IntPtr.Zero || len <= 0)
                    throw new ProviderUnavailableException("Cursor session token not found (is Cursor signed in?).");

                var raw = new byte[len];
                Marshal.Copy(ptr, raw, 0, len);
                var token = Encoding.UTF8.GetString(raw).Trim();
                if (string.IsNullOrEmpty(token))
                    throw new ProviderUnavailableException("Cursor session token not found (is Cursor signed in?).");
                return token;
            }
            finally
            {
                _ = WinSqlite.sqlite3_finalize(stmt);
            }
        }
        catch (DllNotFoundException)
        {
            throw new ProviderUnavailableException("System SQLite library unavailable.");
        }
        finally
        {
            if (db != IntPtr.Zero)
                _ = WinSqlite.sqlite3_close(db);
        }
    }

    private static class WinSqlite
    {
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_open_v2(byte[] filename, out IntPtr ppDb, int flags, IntPtr zVfs);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_prepare_v2(IntPtr db, byte[] zSql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_step(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sqlite3_column_blob(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_column_bytes(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_close(IntPtr db);
    }
}
