using System.Net;

namespace QuotaAI.Core;

public sealed class ProviderUnavailableException(string message) : Exception(message);

public interface IUsageProvider
{
    string Id { get; }
    string DisplayName { get; }
    string ShortCode { get; }
    bool IsDetected { get; }
    ProviderAuthKind AuthKind => ProviderAuthKind.LocalCredentials;
    string KeychainAccount => $"apikey.{Id}";
    string ApiKeyPlaceholder => $"{DisplayName} API key";
    string? CredentialHelpText => null;
    string? KeyPortalUrl => null;
    string? DashboardUrl => null;

    Task<ProviderUsage> FetchAsync(bool forceRefresh, CancellationToken cancellationToken);

    Task<CredentialAssessment?> AssessCredentialAsync(CancellationToken cancellationToken) =>
        Task.FromResult<CredentialAssessment?>(null);
}

internal static class SharedHttp
{
    public static readonly HttpClient Client = CreateClient(allowAutoRedirect: true);
    public static readonly HttpClient NoRedirectClient = CreateClient(allowAutoRedirect: false);

    private static HttpClient CreateClient(bool allowAutoRedirect)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    public static DateTimeOffset? ParseIsoTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }
}
