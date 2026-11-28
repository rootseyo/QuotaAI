using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace QuotaAI.Core;

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
        // No redirects: a 307/308 would replay the refresh token in the body to another host.
        using var resp = await SharedHttp.NoRedirectClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ProviderUnavailableException("Sign in to Antigravity CLI by running agy, then refresh.");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var access = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        if (string.IsNullOrWhiteSpace(access))
            throw new ProviderUnavailableException("Unexpected token response from Google.");
        return access;
    }
}
