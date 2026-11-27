using System.Globalization;
using System.Text.Json.Serialization;

namespace QuotaAI.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CountDirection
{
    Used,
    Remaining
}

public static class CountDirectionExtensions
{
    public static double DisplayPercent(this CountDirection direction, double usedPercent) =>
        direction == CountDirection.Used
            ? Math.Clamp(usedPercent, 0, 100)
            : Math.Clamp(100 - usedPercent, 0, 100);

    public static int DisplayInt(this CountDirection direction, double usedPercent) =>
        (int)Math.Round(direction.DisplayPercent(usedPercent), MidpointRounding.AwayFromZero);

    public static string PercentLabel(this CountDirection direction, double usedPercent, bool compact = false)
    {
        var val = direction.DisplayInt(usedPercent);
        if (compact)
            return $"{val}%";
        return direction == CountDirection.Used ? $"{val}% used" : $"{val}% left";
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResetTimeStyle
{
    Relative,
    Absolute
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrayDisplayStyle
{
    TwoLineRemaining,
    Original
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrayBarStyle
{
    Full,
    Compact,
    Icon
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderMode
{
    Auto,
    On,
    Off
}

public static class ProviderModeExtensions
{
    public static bool IsVisible(this ProviderMode mode, bool isDetected) => mode switch
    {
        ProviderMode.Auto => isDetected,
        ProviderMode.On => true,
        ProviderMode.Off => false,
        _ => isDetected
    };

    public static string Label(this ProviderMode mode) => mode switch
    {
        ProviderMode.Auto => "Auto",
        ProviderMode.On => "On",
        ProviderMode.Off => "Off",
        _ => "Auto"
    };
}

public enum ProviderAuthKind
{
    LocalCredentials,
    ApiKey,
    OAuth
}

public sealed record UsageWindow(string Label, double UsedPercent, DateTimeOffset? ResetsAt)
{
    public UsageWindow(int remainingPercent, DateTimeOffset? resetsAt)
        : this("Window", Math.Clamp(100 - remainingPercent, 0, 100), resetsAt)
    {
    }

    public int RemainingPercent =>
        (int)Math.Round(100 - Math.Clamp(UsedPercent, 0, 100), MidpointRounding.AwayFromZero);

    public int UsedInt =>
        (int)Math.Round(Math.Clamp(UsedPercent, 0, 100), MidpointRounding.AwayFromZero);

    public string? FormatReset(ResetTimeStyle style, DateTimeOffset? nowOverride = null)
    {
        if (ResetsAt is not { } target)
            return null;

        var now = nowOverride ?? DateTimeOffset.Now;
        var interval = target - now;
        if (interval <= TimeSpan.Zero)
            return "Resets soon";

        if (style == ResetTimeStyle.Relative)
        {
            var totalSeconds = (int)interval.TotalSeconds;
            var days = totalSeconds / 86_400;
            var hours = (totalSeconds % 86_400) / 3_600;
            var minutes = (totalSeconds % 3_600) / 60;
            if (days > 0)
                return $"Resets in {days}d {hours}h";
            if (hours > 0)
                return $"Resets in {hours}h {minutes}m";
            return $"Resets in {Math.Max(1, minutes)}m";
        }

        var local = target.ToLocalTime();
        var nowLocal = now.ToLocalTime();
        var dateFormat = local.Year != nowLocal.Year ? "MMM d, yyyy HH:mm" : "MMM d, HH:mm";
        var dateText = local.ToString(dateFormat, CultureInfo.CurrentCulture);

        string duration;
        if (interval.TotalSeconds < 60)
        {
            duration = "<1m";
        }
        else if (interval.TotalSeconds >= 86_400)
        {
            var days = (int)(interval.TotalSeconds / 86_400);
            var hours = (int)Math.Round((interval.TotalSeconds - days * 86_400) / 3_600.0);
            if (hours == 24) { days++; hours = 0; }
            duration = $"{days}d {hours}h";
        }
        else if (interval.TotalSeconds >= 3_600)
        {
            var hours = (int)(interval.TotalSeconds / 3_600);
            var mins = (int)Math.Round((interval.TotalSeconds - hours * 3_600) / 60.0);
            if (mins == 60) { hours++; mins = 0; }
            duration = hours == 24 ? "1d 0h" : $"{hours}h {mins}m";
        }
        else
        {
            var mins = Math.Max(1, (int)Math.Round(interval.TotalSeconds / 60.0));
            duration = mins == 60 ? "1h 0m" : $"{mins}m";
        }

        return $"Resets {dateText} ({duration})";
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BalanceKind
{
    Remaining,
    Spent
}

public sealed record BalanceInfo(
    double Remaining,
    double? Used,
    string CurrencySymbol,
    BalanceKind Kind = BalanceKind.Remaining)
{
    public string Display => Kind == BalanceKind.Remaining
        ? $"{CurrencySymbol}{FormatAmount(Remaining)} left"
        : $"{CurrencySymbol}{FormatAmount(Remaining)} used";

    public string ShortDisplay => $"{CurrencySymbol}{FormatAmount(Remaining, compact: true)}";

    public static string FormatAmount(double value, bool compact = false)
    {
        if (!double.IsFinite(value))
            value = 0;
        if (compact && value >= 10)
            return value.ToString("0", CultureInfo.InvariantCulture);
        return Math.Abs(value - Math.Round(value)) < 1e-9 && value < 1000
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.00", CultureInfo.InvariantCulture);
    }
}

public sealed record ApiUsageSummary(
    double CostUsd,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd);

public sealed record ProviderUsage(
    string? PlanName,
    IReadOnlyList<UsageWindow> Windows,
    DateTimeOffset? AsOf,
    BalanceInfo? Balance = null,
    ApiUsageSummary? ApiUsage = null,
    string? AccountEmail = null)
{
    public UsageWindow? WorstWindow =>
        Windows.Count == 0 ? null : Windows.MaxBy(w => w.UsedPercent);

    public string? MenuSummary(CountDirection direction)
    {
        if (WorstWindow is { } worst)
            return direction.PercentLabel(worst.UsedPercent, compact: true);
        return Balance?.ShortDisplay;
    }
}

public enum ProviderStatusKind
{
    Loading,
    Ready,
    Stale,
    Error
}

public sealed record ProviderState(
    ProviderStatusKind Kind,
    ProviderUsage? Usage = null,
    string? ErrorMessage = null,
    DateTimeOffset? StaleSince = null)
{
    public static readonly ProviderState Loading = new(ProviderStatusKind.Loading);

    public static ProviderState Ready(ProviderUsage usage) =>
        new(ProviderStatusKind.Ready, Usage: usage);

    public static ProviderState Error(string message) =>
        new(ProviderStatusKind.Error, ErrorMessage: message);

    public static ProviderState NextAfterFailure(ProviderState previous, string message, DateTimeOffset now) =>
        previous.Kind switch
        {
            ProviderStatusKind.Ready when previous.Usage is not null =>
                new ProviderState(ProviderStatusKind.Stale, previous.Usage, message, now),
            ProviderStatusKind.Stale when previous.Usage is not null =>
                new ProviderState(ProviderStatusKind.Stale, previous.Usage, message, previous.StaleSince ?? now),
            _ => Error(message)
        };
}

public sealed record CredentialAssessment(
    string KeyTypeLabel,
    string Summary,
    string Detail,
    string? UpgradeHint = null,
    string? ManageUrl = null)
{
    public static CredentialAssessment ProbeFailed(string? manageUrl) => new(
        KeyTypeLabel: "Could not verify key",
        Summary: "Saved, but verification request did not succeed.",
        Detail: "QuotaAI could not confirm this key right now. Check the key value or your network connection and try refreshing.",
        UpgradeHint: null,
        ManageUrl: manageUrl);
}

public sealed record CodexAccountConfig(Guid Id, string Label, string CodexHomePath)
{
    public static CodexAccountConfig Create(string label, string codexHomePath) =>
        new(Guid.NewGuid(), label.Trim(), codexHomePath.Trim());

    public string ExpandedHomePath()
    {
        var raw = CodexHomePath.Trim();
        if (raw.StartsWith("~/", StringComparison.Ordinal) || raw.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.GetFullPath(Path.Combine(home, raw[2..]));
        }
        if (raw == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw));
    }

    public static string SuggestedHomePath(string label)
    {
        var chars = label.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrEmpty(slug) ? @"~\.codex-account" : $@"~\.codex-{slug}";
    }

    public static string LoginCommand(string homePath)
    {
        var trimmed = string.IsNullOrWhiteSpace(homePath) ? @"~\.codex-account" : homePath.Trim();
        var resolved = trimmed.StartsWith('~')
            ? "$HOME" + trimmed[1..].Replace('/', '\\')
            : trimmed;
        return $"New-Item -ItemType Directory -Force \"{resolved}\" | Out-Null; $env:CODEX_HOME=\"{resolved}\"; codex login";
    }
}

public static class CodexAccountDiscovery
{
    public sealed record Candidate(string SuggestedLabel, string HomePath);

    public static IReadOnlyList<Candidate> Discover(IEnumerable<CodexAccountConfig> configured, string? userHome = null)
    {
        var home = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home))
            return [];

        var configuredFullPaths = configured
            .Select(c => SafeFullPath(c.ExpandedHomePath()))
            .Where(p => p is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var results = new List<Candidate>();
        try
        {
            foreach (var dir in Directory.GetDirectories(home, ".codex-*"))
            {
                var name = Path.GetFileName(dir);
                if (name.Length <= ".codex-".Length)
                    continue;
                var full = SafeFullPath(dir);
                if (full is null || configuredFullPaths.Contains(full))
                    continue;
                if (!File.Exists(Path.Combine(dir, "auth.json")))
                    continue;

                var label = name[".codex-".Length..];
                results.Add(new Candidate(label, $@"~\{name}"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return results.OrderBy(c => c.SuggestedLabel, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return null; }
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RenewalPlatform
{
    ChatGPT,
    Apple,
    GooglePlay
}

public static class RenewalPlatformExtensions
{
    public static string DisplayName(this RenewalPlatform platform) => platform switch
    {
        RenewalPlatform.ChatGPT => "ChatGPT (Web)",
        RenewalPlatform.Apple => "Apple App Store",
        RenewalPlatform.GooglePlay => "Google Play",
        _ => "ChatGPT (Web)"
    };
}

public sealed record SubscriptionRenewal(
    DateTimeOffset AnchorDate,
    RenewalPlatform Platform,
    DateTimeOffset? ConfirmedAt = null,
    int RemindDaysBefore = 3)
{
    public DateTime NextRenewalDate(DateTime todayLocal)
    {
        var anchor = AnchorDate.ToLocalTime().Date;
        var candidate = anchor;
        var monthsAdded = 0;
        while (candidate < todayLocal.Date && monthsAdded < 1200)
        {
            monthsAdded++;
            var targetMonth = anchor.AddMonths(monthsAdded);
            var day = Math.Min(anchor.Day, DateTime.DaysInMonth(targetMonth.Year, targetMonth.Month));
            candidate = new DateTime(targetMonth.Year, targetMonth.Month, day);
        }
        return candidate;
    }

    public int DaysUntilRenewal(DateTime todayLocal) =>
        (NextRenewalDate(todayLocal) - todayLocal.Date).Days;
}

public enum QuotaColorBand
{
    High,
    Good,
    Medium,
    Low,
    Empty
}

public enum ThemeAppearance
{
    Light,
    Dark
}

public static class RemainingQuotaPalette
{
    public static readonly QuotaColorBand[] AllBands =
    [
        QuotaColorBand.High,
        QuotaColorBand.Good,
        QuotaColorBand.Medium,
        QuotaColorBand.Low,
        QuotaColorBand.Empty
    ];

    public static QuotaColorBand BandFor(int remainingPercent) => remainingPercent switch
    {
        >= 76 => QuotaColorBand.High,
        >= 51 => QuotaColorBand.Good,
        >= 26 => QuotaColorBand.Medium,
        >= 1 => QuotaColorBand.Low,
        _ => QuotaColorBand.Empty
    };

    public static string Label(this QuotaColorBand band) => band switch
    {
        QuotaColorBand.High => "76–100%",
        QuotaColorBand.Good => "51–75%",
        QuotaColorBand.Medium => "26–50%",
        QuotaColorBand.Low => "1–25%",
        _ => "0%"
    };

    public static int DefaultRgb(QuotaColorBand band, ThemeAppearance appearance) => (band, appearance) switch
    {
        (QuotaColorBand.High, ThemeAppearance.Light) => 0x075735,
        (QuotaColorBand.High, ThemeAppearance.Dark) => 0x82F3B2,
        (QuotaColorBand.Good, ThemeAppearance.Light) => 0x00378D,
        (QuotaColorBand.Good, ThemeAppearance.Dark) => 0xA5D3FF,
        (QuotaColorBand.Medium, ThemeAppearance.Light) => 0x604600,
        (QuotaColorBand.Medium, ThemeAppearance.Dark) => 0xFFE083,
        (QuotaColorBand.Low, ThemeAppearance.Light) => 0x8A2C00,
        (QuotaColorBand.Low, ThemeAppearance.Dark) => 0xFFAD78,
        (QuotaColorBand.Empty, ThemeAppearance.Light) => 0xA0102D,
        (QuotaColorBand.Empty, ThemeAppearance.Dark) => 0xFF92A9,
        _ => 0x82F3B2
    };

    public static string Key(QuotaColorBand band, ThemeAppearance appearance) =>
        $"{appearance.ToString().ToLowerInvariant()}.{band.ToString().ToLowerInvariant()}";

    public static int GetRgb(
        IReadOnlyDictionary<string, int>? overrides,
        int remainingPercent,
        bool darkAppearance)
    {
        var band = BandFor(remainingPercent);
        var appearance = darkAppearance ? ThemeAppearance.Dark : ThemeAppearance.Light;
        return GetRgb(overrides, band, appearance);
    }

    public static int GetRgb(
        IReadOnlyDictionary<string, int>? overrides,
        QuotaColorBand band,
        ThemeAppearance appearance)
    {
        var key = Key(band, appearance);
        if (overrides is not null && overrides.TryGetValue(key, out var custom) && custom is >= 0 and <= 0xFFFFFF)
            return custom;
        return DefaultRgb(band, appearance);
    }
}
