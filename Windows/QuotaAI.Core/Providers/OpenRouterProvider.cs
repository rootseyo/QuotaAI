using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuotaAI.Core;

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
