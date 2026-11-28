using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace QuotaAI.Core;

public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string DownloadUrl,
    string SignatureUrl,
    string Sha256,
    long Size,
    string ReleaseUrl);

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Looks for newer Windows builds on the project's GitHub Releases page.
/// Windows releases are tagged <c>windows-preview-&lt;version&gt;</c> and carry
/// <c>QuotaAI.exe</c> plus <c>QuotaAI.exe.sig</c>, an ECDSA P-256 / SHA-256
/// signature made in CI with a key whose public half is embedded below. A
/// download is installed only if its size, GitHub's SHA-256 digest and that
/// signature all match, so a compromised GitHub account alone cannot ship an
/// update. No credentials or identifiers are sent.
/// </summary>
public static class UpdateChecker
{
    public const string TagPrefix = "windows-preview-";
    public const string AssetName = "QuotaAI.exe";
    public const string SignatureAssetName = AssetName + ".sig";

    private const string Repository = "rootseyo/QuotaAI";
    public const string ReleasesPageUrl = "https://github.com/" + Repository + "/releases";
    private const string ReleasesApiUrl = "https://api.github.com/repos/" + Repository + "/releases?per_page=30";
    private const string DownloadPrefix = ReleasesPageUrl + "/download/";
    private const string ReleasePagePrefix = ReleasesPageUrl + "/";

    /// <summary>Hard ceiling for the executable (the self-contained build is ~115 MB).</summary>
    public const long MaxAssetBytes = 400L * 1024 * 1024;
    private const int MaxSignatureBytes = 1024;

    // Public half of the CI release-signing key (secret WINDOWS_UPDATE_SIGNING_KEY).
    public const string SigningPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE8EZ5njztL0To9B5K4HR8lXXOJP5J
        bU4rF8tA74GPasxkMwQ5IPvlpAQEyimDv69HmjCx8b2xrXLKe3QDdV3wFQ==
        -----END PUBLIC KEY-----
        """;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("QuotaAI-Windows", "1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static Version? ParseTag(string? tag)
    {
        if (tag is null || !tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        return Version.TryParse(tag[TagPrefix.Length..], out var version) ? Normalize(version) : null;
    }

    public static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>Only this repository's own release pages may be opened in a browser.</summary>
    public static string SafeReleaseUrl(string? url) =>
        url is not null && url.StartsWith(ReleasePagePrefix, StringComparison.Ordinal)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? url
            : ReleasesPageUrl;

    /// <summary>Newest non-draft, verifiable Windows release in a GitHub "list releases" response.</summary>
    public static UpdateInfo? SelectLatest(string json)
    {
        UpdateInfo? best = null;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                continue;
            var tag = Str(release, "tag_name");
            var version = ParseTag(tag);
            if (version is null || !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                continue;

            JsonElement? exe = null;
            string? signatureUrl = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = Str(asset, "name");
                if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                    exe = asset;
                else if (string.Equals(name, SignatureAssetName, StringComparison.OrdinalIgnoreCase))
                    signatureUrl = Str(asset, "browser_download_url");
            }

            // Fail closed: never offer a file that cannot be fully verified.
            if (exe is not { } e || !IsOwnDownload(signatureUrl))
                continue;
            var url = Str(e, "browser_download_url");
            var digest = Str(e, "digest");
            var size = e.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
            if (!IsOwnDownload(url) || digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (size is <= 0 or > MaxAssetBytes)
                continue;

            var info = new UpdateInfo(version, tag!, url!, signatureUrl!, digest["sha256:".Length..].ToLowerInvariant(), size,
                SafeReleaseUrl(Str(release, "html_url")));
            if (best is null || info.Version > best.Version)
                best = info;
        }

        return best;
    }

    /// <summary>Returns the newest release if it is newer than <paramref name="current"/>.</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await Http.GetAsync(ReleasesApiUrl, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"GitHub returned HTTP {(int)response.StatusCode}.");
            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            var latest = SelectLatest(json);
            return latest is not null && latest.Version > Normalize(current) ? latest : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested)
                throw;
            throw new UpdateException("Could not reach GitHub to check for updates.", ex);
        }
    }

    /// <summary>
    /// Downloads the release asset to <paramref name="destination"/> and verifies its
    /// exact size, SHA-256 digest and release signature. Deletes the file on any failure.
    /// </summary>
    public static async Task DownloadAsync(UpdateInfo info, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        if (!IsOwnDownload(info.DownloadUrl) || !IsOwnDownload(info.SignatureUrl))
            throw new UpdateException("Refusing to download from an unexpected address.");

        try
        {
            var signature = await DownloadSignatureAsync(info.SignatureUrl, ct);

            using var response = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"Download failed (HTTP {(int)response.StatusCode}).");
            if (response.Content.Headers.ContentLength is { } announced && announced != info.Size)
                throw new UpdateException("The download size does not match the release.");

            byte[] digest;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    read += n;
                    if (read > info.Size)
                        throw new UpdateException("The download is larger than the release and was discarded.");
                    hash.AppendData(buffer, 0, n);
                    await target.WriteAsync(buffer.AsMemory(0, n), ct);
                    progress?.Report((double)read / info.Size);
                }

                if (read != info.Size)
                    throw new UpdateException("The download was incomplete and was discarded.");
                digest = hash.GetHashAndReset();
            }

            if (!string.Equals(Convert.ToHexString(digest), info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("The downloaded file failed its integrity check and was discarded.");
            if (!VerifySignature(digest, signature, SigningPublicKeyPem))
                throw new UpdateException("The downloaded file is not signed by QuotaAI and was discarded.");
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    /// <summary>Checks a DER-encoded ECDSA P-256 signature (openssl dgst -sha256 -sign) over a SHA-256 digest.</summary>
    public static bool VerifySignature(byte[] sha256Digest, byte[] derSignature, string publicKeyPem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyHash(sha256Digest, derSignature, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static async Task<byte[]> DownloadSignatureAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new UpdateException($"Could not download the release signature (HTTP {(int)response.StatusCode}).");
        if (response.Content.Headers.ContentLength > MaxSignatureBytes)
            throw new UpdateException("The release signature is malformed.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[256];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > MaxSignatureBytes)
                throw new UpdateException("The release signature is malformed.");
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }

    private static bool IsOwnDownload(string? url) =>
        url is not null && url.StartsWith(DownloadPrefix, StringComparison.Ordinal);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
