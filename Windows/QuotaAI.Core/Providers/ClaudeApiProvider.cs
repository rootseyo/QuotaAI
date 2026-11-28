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
