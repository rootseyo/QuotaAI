using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuotaAI.Core;

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
