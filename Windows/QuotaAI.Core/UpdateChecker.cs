using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace QuotaAI.Core;

public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string DownloadUrl,
    string Sha256,
    long Size,
    string ReleaseUrl);

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Looks for newer Windows builds on the project's GitHub Releases page.
/// Windows releases are tagged <c>windows-preview-&lt;version&gt;</c> and carry a
/// single <c>QuotaAI.exe</c> asset. Only that repository's own download URLs are
/// accepted and every download is verified against the SHA-256 digest GitHub
/// computes for the asset. No credentials or identifiers are sent.
/// </summary>
public static class UpdateChecker
{
    public const string TagPrefix = "windows-preview-";
    public const string AssetName = "QuotaAI.exe";
    public const string ReleasesPageUrl = "https://github.com/rootseyo/QuotaAI/releases";

    private const string ReleasesApiUrl = "https://api.github.com/repos/rootseyo/QuotaAI/releases?per_page=30";
    private const string DownloadPrefix = "https://github.com/rootseyo/QuotaAI/releases/download/";

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

    /// <summary>Newest non-draft Windows release in a GitHub "list releases" response.</summary>
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

            foreach (var asset in assets.EnumerateArray())
            {
                if (!string.Equals(Str(asset, "name"), AssetName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var url = Str(asset, "browser_download_url");
                var digest = Str(asset, "digest");
                if (url is null || !url.StartsWith(DownloadPrefix, StringComparison.Ordinal))
                    continue;
                if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    continue; // fail closed: never install an unverifiable file

                var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                var info = new UpdateInfo(version, tag!, url, digest["sha256:".Length..].ToLowerInvariant(), size,
                    Str(release, "html_url") ?? ReleasesPageUrl);
                if (best is null || info.Version > best.Version)
                    best = info;
                break;
            }
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

    /// <summary>Downloads the release asset to <paramref name="destination"/> and verifies its SHA-256.</summary>
    public static async Task DownloadAsync(UpdateInfo info, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        if (!info.DownloadUrl.StartsWith(DownloadPrefix, StringComparison.Ordinal))
            throw new UpdateException("Refusing to download from an unexpected address.");

        try
        {
            using var response = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"Download failed (HTTP {(int)response.StatusCode}).");

            var total = response.Content.Headers.ContentLength ?? info.Size;
            using var hash = SHA256.Create();
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    hash.TransformBlock(buffer, 0, n, null, 0);
                    await target.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total > 0)
                        progress?.Report(Math.Min(1.0, (double)read / total));
                }
                hash.TransformFinalBlock([], 0, 0);
            }

            var actual = Convert.ToHexString(hash.Hash!).ToLowerInvariant();
            if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("The downloaded file failed its integrity check and was discarded.");
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
