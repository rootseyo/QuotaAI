using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuotaAI.Core;

public sealed class ClaudeApiProvider : IUsageProvider
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private byte[]? cachedKeyHash;
    private DateTimeOffset cachedMonthStart;
    private ProviderUsage? cachedUsage;
    private DateTimeOffset? cachedAt;
    private string? lastFailureMessage;
    private DateTimeOffset? failedAt;

    public string Id => "claude-api";
    public string DisplayName => "Claude API";
    public string ShortCode => "ClA";
    public ProviderAuthKind AuthKind => ProviderAuthKind.ApiKey;
    public string KeychainAccount => "apikey.claude-api";
    public string ApiKeyPlaceholder => "Claude Admin / reporting key";
    public string? KeyPortalUrl => "https://platform.claude.com/settings/admin-keys";
    public string? DashboardUrl => "https://platform.claude.com/settings/usage";
    public string? CredentialHelpText =>
        "Organization-wide API spend and tokens require a Claude Console Admin reporting key or an eligible unscoped key. Prepaid credit balance is unavailable via this API.";

    public bool IsDetected => WindowsCredentialStore.Exists(KeychainAccount);

    public async Task<ProviderUsage> FetchAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var key = WindowsCredentialStore.GetApiKey(KeychainAccount)?.Trim();
        if (string.IsNullOrEmpty(key))
            throw new ProviderUnavailableException("Add your Claude API reporting key in Settings.");

        var now = DateTimeOffset.UtcNow;
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var queryEnd = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero).AddDays(1);
        var keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cachedKeyHash is null || !cachedKeyHash.SequenceEqual(keyHash) || cachedMonthStart != monthStart)
            {
                cachedKeyHash = keyHash;
                cachedMonthStart = monthStart;
                cachedUsage = null;
                cachedAt = null;
                lastFailureMessage = null;
                failedAt = null;
            }

            if (lastFailureMessage is not null && failedAt is { } fa && (now - fa).TotalSeconds < 60 && now >= fa)
                throw new ProviderUnavailableException(lastFailureMessage);

            if (!forceRefresh && cachedUsage is not null && cachedAt is { } ca && (now - ca).TotalSeconds < 300 && now >= ca)
                return cachedUsage;

            try
            {
                var usage = await LoadReportAsync(key, monthStart, queryEnd, now, cancellationToken).ConfigureAwait(false);
                cachedUsage = usage;
                cachedAt = now;
                lastFailureMessage = null;
                failedAt = null;
                return usage;
            }
            catch (ProviderUnavailableException ex)
            {
                cachedUsage = null;
                cachedAt = null;
                lastFailureMessage = ex.Message;
                failedAt = now;
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CredentialAssessment?> AssessCredentialAsync(CancellationToken cancellationToken)
    {
        if (!IsDetected)
            return null;
        try
        {
            _ = await FetchAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
            return new CredentialAssessment(
                KeyTypeLabel: "Reporting key",
                Summary: "Reads organization-wide monthly API spend and tokens.",
                Detail: "QuotaAI sends read-only report requests to Anthropic. Prepaid credits are not exposed via this API — check Console billing directly.",
                UpgradeHint: null,
                ManageUrl: KeyPortalUrl);
        }
        catch (ProviderUnavailableException ex)
        {
            return new CredentialAssessment(
                KeyTypeLabel: "Key check failed",
                Summary: ex.Message,
                Detail: "Ensure the key is an organization Admin reporting key or an unscoped key eligible for Usage & Cost APIs.",
                UpgradeHint: null,
                ManageUrl: KeyPortalUrl);
        }
    }

    private static async Task<ProviderUsage> LoadReportAsync(
        string key, DateTimeOffset start, DateTimeOffset queryEnd, DateTimeOffset observedAt, CancellationToken ct)
    {
        var costDocs = await FetchPagesAsync("/v1/organizations/cost_report", key, start, queryEnd, ct).ConfigureAwait(false);
        var usageDocs = await FetchPagesAsync("/v1/organizations/usage_report/messages", key, start, queryEnd, ct).ConfigureAwait(false);

        decimal cents = 0m;
        DateTimeOffset? latestCostEnd = null;
        foreach (var doc in costDocs)
        {
            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    throw new ProviderUnavailableException("Unexpected Claude API report.");
                foreach (var bucket in data.EnumerateArray())
                {
                    var end = SharedHttp.ParseIsoTimestamp(bucket.TryGetProperty("ending_at", out var ea) ? ea.GetString() : null)
                        ?? throw new ProviderUnavailableException("Unexpected Claude API report.");
                    latestCostEnd = latestCostEnd is null || end > latestCostEnd ? end : latestCostEnd;
                    if (!bucket.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var r in results.EnumerateArray())
                    {
                        var currency = r.TryGetProperty("currency", out var c) ? c.GetString() : null;
                        var amountStr = r.TryGetProperty("amount", out var a) ? a.GetString() : null;
                        if (currency != "USD" || string.IsNullOrWhiteSpace(amountStr)
                            || !decimal.TryParse(amountStr, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
                            || parsed < 0)
                        {
                            throw new ProviderUnavailableException("Unexpected Claude API report.");
                        }
                        cents += parsed;
                    }
                }
            }
        }

        long input = 0, output = 0, cacheRead = 0, cacheCreate = 0;
        DateTimeOffset? latestUsageEnd = null;
        foreach (var doc in usageDocs)
        {
            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    throw new ProviderUnavailableException("Unexpected Claude API report.");
                foreach (var bucket in data.EnumerateArray())
                {
                    var end = SharedHttp.ParseIsoTimestamp(bucket.TryGetProperty("ending_at", out var ea) ? ea.GetString() : null)
                        ?? throw new ProviderUnavailableException("Unexpected Claude API report.");
                    latestUsageEnd = latestUsageEnd is null || end > latestUsageEnd ? end : latestUsageEnd;
                    if (!bucket.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var r in results.EnumerateArray())
                    {
                        input = checked(input + Math.Max(0, GetLong(r, "uncached_input_tokens")));
                        output = checked(output + Math.Max(0, GetLong(r, "output_tokens")));
                        cacheRead = checked(cacheRead + Math.Max(0, GetLong(r, "cache_read_input_tokens")));
                        if (r.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object)
                        {
                            cacheCreate = checked(cacheCreate + Math.Max(0, GetLong(cc, "ephemeral_1h_input_tokens")));
                            cacheCreate = checked(cacheCreate + Math.Max(0, GetLong(cc, "ephemeral_5m_input_tokens")));
                        }
                    }
                }
            }
        }

        var costUsd = (double)(cents / 100m);
        var minCoverage = new[] { latestCostEnd ?? start, latestUsageEnd ?? start, observedAt }.Min();
        var periodEnd = minCoverage > start ? minCoverage : start;
        var summary = new ApiUsageSummary(costUsd, input, output, cacheRead, cacheCreate, start, periodEnd);

        return new ProviderUsage(
            PlanName: "Organization",
            Windows: [],
            AsOf: observedAt,
            Balance: new BalanceInfo(costUsd, null, "$", BalanceKind.Spent),
            ApiUsage: summary);
    }

    private static long GetLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var prop) && prop.TryGetInt64(out var val) ? val : 0;

    private static async Task<List<JsonDocument>> FetchPagesAsync(
        string path, string key, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var pages = new List<JsonDocument>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? nextPage = null;

        do
        {
            if (pages.Count >= 100)
                throw new ProviderUnavailableException("Unexpected Claude API report.");

            var sb = new StringBuilder($"https://api.anthropic.com{path}?");
            sb.Append("starting_at=").Append(Uri.EscapeDataString(start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
            sb.Append("&ending_at=").Append(Uri.EscapeDataString(end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
            sb.Append("&bucket_width=1d&limit=31");
            if (!string.IsNullOrEmpty(nextPage))
                sb.Append("&page=").Append(Uri.EscapeDataString(nextPage));

            using var req = new HttpRequestMessage(HttpMethod.Get, sb.ToString());
            req.Headers.TryAddWithoutValidation("x-api-key", key);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.TryAddWithoutValidation("User-Agent", "QuotaAI/1.0");

            HttpResponseMessage resp;
            try
            {
                resp = await SharedHttp.NoRedirectClient.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                throw new ProviderUnavailableException("Could not connect to Claude API reporting.");
            }

            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    throw new ProviderUnavailableException("Claude API reporting key rejected (HTTP 401).");
                if (resp.StatusCode == HttpStatusCode.Forbidden)
                    throw new ProviderUnavailableException("Claude API reporting access denied (HTTP 403); check key scope.");
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                    throw new ProviderUnavailableException("Claude API reporting rate limited (HTTP 429).");
                if (!resp.IsSuccessStatusCode)
                    throw new ProviderUnavailableException($"Claude API reporting returned HTTP {(int)resp.StatusCode}.");

                var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                pages.Add(doc);
                var hasMore = doc.RootElement.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
                if (hasMore)
                {
                    var next = doc.RootElement.TryGetProperty("next_page", out var np) ? np.GetString() : null;
                    if (string.IsNullOrEmpty(next) || !seen.Add(next))
                        throw new ProviderUnavailableException("Unexpected Claude API report.");
                    nextPage = next;
                }
                else
                {
                    nextPage = null;
                }
            }
        } while (nextPage is not null);

        return pages;
    }
}

public sealed class OpenRouterProvider : IUsageProvider
{
    private const string CreditsUrl = "https://openrouter.ai/api/v1/credits";
    private const string KeyUrl = "https://openrouter.ai/api/v1/key";

    public string Id => "openrouter";
    public string DisplayName => "OpenRouter";
    public string ShortCode => "OR";
    public ProviderAuthKind AuthKind => ProviderAuthKind.ApiKey;
    public string KeychainAccount => "apikey.openrouter";
    public string ApiKeyPlaceholder => "OpenRouter API key";
    public string? KeyPortalUrl => "https://openrouter.ai/settings/keys";
    public string? DashboardUrl => "https://openrouter.ai/settings/credits";
    public string? CredentialHelpText => "Paste a dedicated OpenRouter API key to view remaining credits or monthly spend.";

    public bool IsDetected => WindowsCredentialStore.Exists(KeychainAccount);

    public async Task<ProviderUsage> FetchAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var key = RequireKey(this);
        var keyDoc = await GetJsonAsync(KeyUrl, key, "OpenRouter", cancellationToken).ConfigureAwait(false);
        using (keyDoc)
        {
            try
            {
                using var creditsDoc = await GetJsonAsync(CreditsUrl, key, "OpenRouter", cancellationToken).ConfigureAwait(false);
                if (creditsDoc.RootElement.TryGetProperty("data", out var cData)
                    && cData.TryGetProperty("total_credits", out var tc) && tc.TryGetDouble(out var totalCredits)
                    && cData.TryGetProperty("total_usage", out var tu) && tu.TryGetDouble(out var totalUsage))
                {
                    return new ProviderUsage(
                        "Credits",
                        [],
                        DateTimeOffset.Now,
                        new BalanceInfo(Math.Max(0, totalCredits - totalUsage), totalUsage, "$"));
                }
            }
            catch (ProviderUnavailableException)
            {
                // Standard keys may not access /credits; fall back to /key payload.
            }

            if (!keyDoc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                throw new ProviderUnavailableException("Unexpected response from OpenRouter.");

            var used = data.TryGetProperty("usage", out var u) && u.TryGetDouble(out var uv) ? uv : 0;
            if (data.TryGetProperty("limit", out var limProp) && limProp.TryGetDouble(out var limit) && limit > 0)
            {
                var rem = data.TryGetProperty("limit_remaining", out var lr) && lr.TryGetDouble(out var lrv)
                    ? Math.Max(0, lrv)
                    : Math.Max(0, limit - used);
                var pct = Math.Clamp(used / limit * 100.0, 0, 100);
                return new ProviderUsage(
                    "API key",
                    [new UsageWindow("Key limit", pct, null)],
                    DateTimeOffset.Now,
                    new BalanceInfo(rem, used, "$"));
            }

            var spend = TryDouble(data, "usage_monthly") ?? TryDouble(data, "usage_weekly") ?? TryDouble(data, "usage_daily") ?? used;
            return new ProviderUsage("API key", [], DateTimeOffset.Now, new BalanceInfo(spend, null, "$", BalanceKind.Spent));
        }
    }

    public async Task<CredentialAssessment?> AssessCredentialAsync(CancellationToken cancellationToken)
    {
        var key = WindowsCredentialStore.GetApiKey(KeychainAccount)?.Trim();
        if (string.IsNullOrEmpty(key))
            return null;

        try
        {
            using var doc = await GetJsonAsync(KeyUrl, key, "OpenRouter", cancellationToken).ConfigureAwait(false);
            var data = doc.RootElement.GetProperty("data");
            var isMgmt = (data.TryGetProperty("is_management_key", out var m) && m.ValueKind == JsonValueKind.True)
                || (data.TryGetProperty("is_provisioning_key", out var p) && p.ValueKind == JsonValueKind.True);
            if (isMgmt)
                return new CredentialAssessment("Management key", "Full account access — more powerful than needed.",
                    "A regular API key is safer and works for usage monitoring.", null, KeyPortalUrl);
            if (TryDouble(data, "limit") is > 0)
                return new CredentialAssessment("Limited key", "Spending capped — reads usage within this limit.",
                    "QuotaAI only reads usage; it never spends credits.", null, KeyPortalUrl);
            return new CredentialAssessment("Standard key", "Works for usage monitoring.",
                "QuotaAI only reads usage; it never spends credits.", null, KeyPortalUrl);
        }
        catch
        {
            return CredentialAssessment.ProbeFailed(KeyPortalUrl);
        }
    }

    private static double? TryDouble(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var p) && p.TryGetDouble(out var v) && double.IsFinite(v) ? v : null;

    internal static string RequireKey(IUsageProvider provider)
    {
        var key = WindowsCredentialStore.GetApiKey(provider.KeychainAccount)?.Trim();
        if (string.IsNullOrEmpty(key))
            throw new ProviderUnavailableException($"Add your {provider.DisplayName} API key in Settings.");
        return key;
    }

    internal static async Task<JsonDocument> GetJsonAsync(
        string url, string bearerKey, string providerName, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await SharedHttp.Client.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ProviderUnavailableException($"{providerName} key rejected — check the key in Settings.");
        if (!resp.IsSuccessStatusCode)
            throw new ProviderUnavailableException($"{providerName} returned HTTP {(int)resp.StatusCode}.");

        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }
}

public sealed class DeepSeekProvider : IUsageProvider
{
    private const string BalanceUrl = "https://api.deepseek.com/user/balance";

    public string Id => "deepseek";
    public string DisplayName => "DeepSeek";
    public string ShortCode => "DS";
    public ProviderAuthKind AuthKind => ProviderAuthKind.ApiKey;
    public string KeychainAccount => "apikey.deepseek";
    public string? KeyPortalUrl => "https://platform.deepseek.com/api_keys";
    public string? DashboardUrl => "https://platform.deepseek.com/usage";
    public bool IsDetected => WindowsCredentialStore.Exists(KeychainAccount);

    public async Task<ProviderUsage> FetchAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var key = OpenRouterProvider.RequireKey(this);
        using var doc = await OpenRouterProvider.GetJsonAsync(BalanceUrl, key, DisplayName, cancellationToken).ConfigureAwait(false);
        double remaining = 0;
        var currency = "USD";
        if (doc.RootElement.TryGetProperty("balance_infos", out var infos) && infos.ValueKind == JsonValueKind.Array)
        {
            JsonElement? chosen = null;
            foreach (var item in infos.EnumerateArray())
            {
                chosen ??= item;
                if (item.TryGetProperty("currency", out var c) && c.GetString() == "USD")
                {
                    chosen = item;
                    break;
                }
            }
            if (chosen is { } entry)
            {
                currency = entry.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "USD" : "USD";
                if (entry.TryGetProperty("total_balance", out var tb) && tb.ValueKind == JsonValueKind.String)
                    _ = double.TryParse(tb.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out remaining);
            }
        }
        var sym = currency switch { "USD" => "$", "CNY" => "¥", _ => $"{currency} " };
        return new ProviderUsage(null, [], DateTimeOffset.Now, new BalanceInfo(Math.Max(0, remaining), null, sym));
    }

    public async Task<CredentialAssessment?> AssessCredentialAsync(CancellationToken cancellationToken)
    {
        if (!IsDetected) return null;
        try
        {
            _ = await FetchAsync(false, cancellationToken).ConfigureAwait(false);
            return new CredentialAssessment("Valid key", "Reads prepaid balance only.",
                "QuotaAI only reads your DeepSeek balance and never calls models.", null, KeyPortalUrl);
        }
        catch { return CredentialAssessment.ProbeFailed(KeyPortalUrl); }
    }
}

public sealed class MoonshotProvider : IUsageProvider
{
    private const string BalanceUrl = "https://api.moonshot.ai/v1/users/me/balance";

    public string Id => "moonshot";
    public string DisplayName => "Kimi";
    public string ShortCode => "Ki";
    public ProviderAuthKind AuthKind => ProviderAuthKind.ApiKey;
    public string KeychainAccount => "apikey.moonshot";
    public string? KeyPortalUrl => "https://platform.moonshot.ai/console/api-keys";
    public string? DashboardUrl => "https://platform.moonshot.ai/console";
    public bool IsDetected => WindowsCredentialStore.Exists(KeychainAccount);

    public async Task<ProviderUsage> FetchAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var key = OpenRouterProvider.RequireKey(this);
        using var doc = await OpenRouterProvider.GetJsonAsync(BalanceUrl, key, DisplayName, cancellationToken).ConfigureAwait(false);
        var avail = 0.0;
        if (doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
            && d.TryGetProperty("available_balance", out var ab) && ab.TryGetDouble(out var v))
        {
            avail = Math.Max(0, v);
        }
        return new ProviderUsage(null, [], DateTimeOffset.Now, new BalanceInfo(avail, null, "$"));
    }

    public async Task<CredentialAssessment?> AssessCredentialAsync(CancellationToken cancellationToken)
    {
        if (!IsDetected) return null;
        try
        {
            _ = await FetchAsync(false, cancellationToken).ConfigureAwait(false);
            return new CredentialAssessment("Valid key", "Reads prepaid balance only.",
                "QuotaAI only reads your Kimi balance and never calls models.", null, KeyPortalUrl);
        }
        catch { return CredentialAssessment.ProbeFailed(KeyPortalUrl); }
    }
}

public sealed class ZaiProvider : IUsageProvider
{
    private const string QuotaUrl = "https://api.z.ai/api/monitor/usage/quota/limit";

    public string Id => "zai";
    public string DisplayName => "Z.ai";
    public string ShortCode => "Zg";
    public ProviderAuthKind AuthKind => ProviderAuthKind.ApiKey;
    public string KeychainAccount => "apikey.zai";
    public string ApiKeyPlaceholder => "GLM Coding Plan API key";
    public string? KeyPortalUrl => "https://z.ai/manage-apikey/apikey-list";
    public string? DashboardUrl => "https://z.ai/model-api";
    public string? CredentialHelpText =>
        "Z.ai monitoring supports GLM Coding Plans only. Standard pay-as-you-go API balance is not exposed via an API.";
    public bool IsDetected => WindowsCredentialStore.Exists(KeychainAccount);

    public async Task<ProviderUsage> FetchAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var key = OpenRouterProvider.RequireKey(this);
        using var doc = await OpenRouterProvider.GetJsonAsync(QuotaUrl, key, DisplayName, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.TryGetProperty("code", out var codeProp) && codeProp.TryGetInt32(out var code) && code != 200)
        {
            var msg = root.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
            if (msg.Contains("coding plan", StringComparison.OrdinalIgnoreCase))
                throw new ProviderUnavailableException("Z.ai key is valid, but this account has no GLM Coding Plan.");
            throw new ProviderUnavailableException(string.IsNullOrEmpty(msg) ? $"Z.ai API returned code {code}." : $"Z.ai API: {msg}");
        }

        var windows = new List<UsageWindow>();
        string? planName = null;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("planName", out var pn) && pn.ValueKind == JsonValueKind.String)
                planName = pn.GetString();
            if (data.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in limits.EnumerateArray())
                {
                    var type = entry.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                    var label = type switch
                    {
                        "TIME_LIMIT" => "Time quota",
                        "TOKENS_LIMIT" => "Token quota",
                        _ => null
                    };
                    if (label is null)
                        continue;
                    var pct = entry.TryGetProperty("percentage", out var p) && p.TryGetDouble(out var pv) ? Math.Clamp(pv, 0, 100) : 0;
                    DateTimeOffset? reset = null;
                    if (entry.TryGetProperty("nextResetTime", out var nr) && nr.TryGetInt64(out var ms) && ms > 0)
                        reset = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                    windows.Add(new UsageWindow(label, pct, reset));
                }
            }
        }
        return new ProviderUsage(planName, windows, DateTimeOffset.Now);
    }

    public async Task<CredentialAssessment?> AssessCredentialAsync(CancellationToken cancellationToken)
    {
        if (!IsDetected) return null;
        try
        {
            _ = await FetchAsync(false, cancellationToken).ConfigureAwait(false);
            return new CredentialAssessment("GLM Coding Plan key", "Shows your time and token quotas.",
                "Tied to a GLM Coding Plan subscription; QuotaAI only reads quota percentages.", null, KeyPortalUrl);
        }
        catch (ProviderUnavailableException ex) when (ex.Message.Contains("no GLM Coding Plan", StringComparison.OrdinalIgnoreCase))
        {
            return new CredentialAssessment("Standard API key", "Valid key, but Z.ai only exposes usage for GLM Coding Plans.",
                "Pay-as-you-go balances are not published via Z.ai's API.", null, KeyPortalUrl);
        }
        catch { return CredentialAssessment.ProbeFailed(KeyPortalUrl); }
    }
}

public sealed class VeniceProvider : IUsageProvider
{
    private const string BalanceUrl = "https://api.venice.ai/api/v1/billing/balance";
    private const string RateLimitsUrl = "https://api.venice.ai/api/v1/api_keys/rate_limits";

    public string Id => "venice";
    public string DisplayName => "Venice";
    public string ShortCode => "Ve";
    public ProviderAuthKind AuthKind => ProviderAuthKind.ApiKey;
    public string KeychainAccount => "apikey.venice";
    public string ApiKeyPlaceholder => "Venice API key";
    public string? KeyPortalUrl => "https://venice.ai/settings/api";
    public string? DashboardUrl => "https://venice.ai/settings/api";
    public string? CredentialHelpText => "Inference and Admin Venice API keys are supported. x402 wallet balances are not supported.";
    public bool IsDetected => WindowsCredentialStore.Exists(KeychainAccount);

    public async Task<ProviderUsage> FetchAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var key = OpenRouterProvider.RequireKey(this);
        var (status, body) = await GetRawAsync(BalanceUrl, key, cancellationToken).ConfigureAwait(false);
        if (status == HttpStatusCode.OK)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var pref = root.TryGetProperty("consumptionCurrency", out var cc) ? cc.GetString() : null;
            double? usd = null, diem = null;
            if (root.TryGetProperty("balances", out var b) && b.ValueKind == JsonValueKind.Object)
            {
                usd = ReadFlexDouble(b, "usd") ?? ReadFlexDouble(b, "USD");
                diem = ReadFlexDouble(b, "diem") ?? ReadFlexDouble(b, "DIEM");
            }
            var (cur, amt) = ChooseCurrency(pref, usd, diem);
            return new ProviderUsage(null, [], DateTimeOffset.Now, new BalanceInfo(amt, null, SymbolFor(cur)));
        }

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var (fbStatus, fbBody) = await GetRawAsync(RateLimitsUrl, key, cancellationToken).ConfigureAwait(false);
            if (fbStatus == HttpStatusCode.OK)
            {
                using var doc = JsonDocument.Parse(fbBody);
                double? usd = null, diem = null;
                if (doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                    && d.TryGetProperty("balances", out var b) && b.ValueKind == JsonValueKind.Object)
                {
                    usd = ReadFlexDouble(b, "USD") ?? ReadFlexDouble(b, "usd");
                    diem = ReadFlexDouble(b, "DIEM") ?? ReadFlexDouble(b, "diem");
                }
                var (cur, amt) = ChooseCurrency(null, usd, diem);
                return new ProviderUsage("Inference key", [], DateTimeOffset.Now, new BalanceInfo(amt, null, SymbolFor(cur)));
            }
            if (fbStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProviderUnavailableException("Venice key rejected — check the key in Settings.");
            throw new ProviderUnavailableException($"Venice returned HTTP {(int)fbStatus}.");
        }

        throw new ProviderUnavailableException($"Venice returned HTTP {(int)status}.");
    }

    public async Task<CredentialAssessment?> AssessCredentialAsync(CancellationToken cancellationToken)
    {
        var key = WindowsCredentialStore.GetApiKey(KeychainAccount)?.Trim();
        if (string.IsNullOrEmpty(key)) return null;
        try
        {
            var primary = await GetRawAsync(BalanceUrl, key, cancellationToken).ConfigureAwait(false);
            if (primary.Status == HttpStatusCode.OK)
                return new CredentialAssessment("Admin key", "Full access including billing.",
                    "An Inference key is safer and also works with QuotaAI.", null, KeyPortalUrl);
            if (primary.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var fallback = await GetRawAsync(RateLimitsUrl, key, cancellationToken).ConfigureAwait(false);
                if (fallback.Status == HttpStatusCode.OK)
                    return new CredentialAssessment("Inference key", "Balance works via rate-limits endpoint.",
                        "Safer default key with limited billing scope.", null, KeyPortalUrl);
                return new CredentialAssessment("Key not working", "The key was rejected by Venice.",
                    "Paste a valid Inference or Admin key.", null, KeyPortalUrl);
            }
            return CredentialAssessment.ProbeFailed(KeyPortalUrl);
        }
        catch { return CredentialAssessment.ProbeFailed(KeyPortalUrl); }
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetRawAsync(string url, string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await SharedHttp.Client.SendAsync(req, ct).ConfigureAwait(false);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private static double? ReadFlexDouble(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var n)) return n;
        if (p.ValueKind == JsonValueKind.String && double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    private static (string Currency, double Amount) ChooseCurrency(string? preferred, double? usd, double? diem)
    {
        if (string.Equals(preferred, "DIEM", StringComparison.OrdinalIgnoreCase) && diem is { } dv)
            return ("DIEM", Math.Max(0, dv));
        if (usd is { } uv)
            return ("USD", Math.Max(0, uv));
        if (diem is { } d2)
            return ("DIEM", Math.Max(0, d2));
        return ("USD", 0);
    }

    private static string SymbolFor(string currency) => currency.ToUpperInvariant() switch
    {
        "USD" => "$",
        "DIEM" => "DIEM ",
        "VCU" => "VCU ",
        var c => $"{c} "
    };
}
