using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuotaAI.Core;

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
