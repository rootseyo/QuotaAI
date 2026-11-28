using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuotaAI.Core;

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
