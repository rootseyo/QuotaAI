using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace QuotaAI.Core;

public sealed class CodexProvider : IUsageProvider
{
    private static readonly ConcurrentDictionary<string, (CodexUsage Usage, DateTimeOffset CachedAt)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    public CodexAccountConfig? AccountConfig { get; }
    public string Id { get; }
    public string DisplayName { get; }
    public string ShortCode { get; }
    public string CodexHome { get; }
    public bool IsPrimary => AccountConfig is null;

    public CodexProvider(CodexAccountConfig? accountConfig = null)
    {
        AccountConfig = accountConfig;
        if (accountConfig is not null)
        {
            Id = $"codex:{accountConfig.Id}";
            DisplayName = $"Codex — {accountConfig.Label}";
            var trimmed = accountConfig.Label.Trim();
            ShortCode = string.IsNullOrEmpty(trimmed) ? "Cx" : $"Cx{char.ToUpperInvariant(trimmed[0])}";
            CodexHome = accountConfig.ExpandedHomePath();
        }
        else
        {
            Id = "codex";
            DisplayName = "Codex";
            ShortCode = "Cx";
            CodexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }
    }

    public bool IsDetected => IsPrimary
        ? Directory.Exists(Path.Combine(CodexHome, "sessions")) || File.Exists(Path.Combine(CodexHome, "auth.json"))
        : File.Exists(Path.Combine(CodexHome, "auth.json"));

    public string? DashboardUrl => "https://chatgpt.com/codex/settings/usage";

    public string? LastKnownEmail =>
        Cache.TryGetValue(Id, out var entry) ? entry.Usage.AccountEmail : null;

    public string? LastKnownPlan =>
        Cache.TryGetValue(Id, out var entry) ? entry.Usage.PlanName : null;

    public static void ClearCache(string providerId) => Cache.TryRemove(providerId, out _);

    public async Task<ProviderUsage> FetchAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        if (!forceRefresh && Cache.TryGetValue(Id, out var cached) && now - cached.CachedAt < PollInterval)
        {
            if (IsPrimary && TryReadSessionLog(out var newerLog, out var logTime) && logTime > cached.CachedAt)
                return newerLog!;
            return cached.Usage.ToProviderUsage(cached.CachedAt);
        }

        try
        {
            var reading = await CodexAppServer.ReadAsync(
                IsPrimary ? null : CodexHome,
                includeAccountRead: true,
                cancellationToken).ConfigureAwait(false);
            Cache[Id] = (reading, now);
            return reading.ToProviderUsage(now);
        }
        catch (CodexUnavailableException ex)
        {
            if (IsPrimary && TryReadSessionLog(out var fallbackUsage, out _))
                return fallbackUsage!;
            throw new ProviderUnavailableException(ex.Message);
        }
    }

    private bool TryReadSessionLog(out ProviderUsage? usage, out DateTimeOffset timestamp)
    {
        usage = null;
        timestamp = DateTimeOffset.MinValue;
        var sessionsRoot = Path.Combine(CodexHome, "sessions");
        if (!Directory.Exists(sessionsRoot))
            return false;

        try
        {
            var files = Directory.EnumerateFiles(sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(10);

            foreach (var file in files)
            {
                if (TryParseLatestRateLimitsFromFile(file.FullName, out usage, out timestamp))
                    return true;
            }
        }
        catch (Exception ex) when (FileErrors.Is(ex))
        {
        }
        return false;
    }

    private static bool TryParseLatestRateLimitsFromFile(string path, out ProviderUsage? usage, out DateTimeOffset asOf)
    {
        usage = null;
        asOf = DateTimeOffset.MinValue;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var readLen = (int)Math.Min(fs.Length, 2 * 1024 * 1024);
        if (readLen <= 0)
            return false;

        fs.Seek(-readLen, SeekOrigin.End);
        var buf = new byte[readLen];
        var actual = fs.Read(buf, 0, readLen);
        var text = Encoding.UTF8.GetString(buf, 0, actual);
        var lines = text.Split('\n');

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (!line.Contains("\"rate_limits\"", StringComparison.Ordinal))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!TryFindPropertyRecursive(doc.RootElement, "rate_limits", out var rl))
                    continue;

                var windows = new List<UsageWindow>();
                foreach (var key in new[] { "primary", "secondary" })
                {
                    if (!rl.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object)
                        continue;
                    if (!w.TryGetProperty("used_percent", out var up) || !up.TryGetDouble(out var used))
                        continue;
                    var mins = w.TryGetProperty("window_minutes", out var wm) && wm.TryGetInt32(out var m) ? m : 0;
                    DateTimeOffset? reset = null;
                    if (w.TryGetProperty("resets_at", out var ra) && ra.TryGetDouble(out var sec) && sec > 0)
                        reset = DateTimeOffset.FromUnixTimeSeconds((long)sec);

                    var label = mins switch
                    {
                        300 => "5h limit",
                        10_080 => "Weekly limit",
                        > 0 and < 120 => $"{mins}m limit",
                        >= 120 and < 2880 => $"{mins / 60}h limit",
                        >= 2880 => $"{mins / 1440}d limit",
                        _ => "Window"
                    };
                    windows.Add(new UsageWindow(label, Math.Clamp(used, 0, 100), reset));
                }

                if (windows.Count == 0)
                    continue;

                if (doc.RootElement.TryGetProperty("timestamp", out var tsProp) && tsProp.ValueKind == JsonValueKind.String)
                    asOf = SharedHttp.ParseIsoTimestamp(tsProp.GetString()) ?? DateTimeOffset.Now;
                else
                    asOf = DateTimeOffset.Now;

                string? plan = null;
                if (rl.TryGetProperty("plan_type", out var pt) && pt.ValueKind == JsonValueKind.String)
                    plan = CodexUsage.FormatPlanName(pt.GetString());

                usage = new ProviderUsage(plan, windows, asOf);
                return true;
            }
            catch (JsonException)
            {
            }
        }
        return false;
    }

    private static bool TryFindPropertyRecursive(JsonElement element, string name, out JsonElement found)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out found) && found.ValueKind == JsonValueKind.Object)
                return true;
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object && TryFindPropertyRecursive(prop.Value, name, out found))
                    return true;
            }
        }
        found = default;
        return false;
    }
}
