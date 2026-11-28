using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuotaAI.Core;

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
