using System.Text;
using System.Text.Json;

namespace QuotaAI.Core;

public static class TrayTooltipFormatter
{
    public const int MaxWin32TooltipLength = 127;

    public sealed record EntryInput(
        string Id,
        string ShortCode,
        string DisplayName,
        ProviderState State);

    public static string Format(
        IReadOnlyList<EntryInput> entries,
        CountDirection direction,
        TrayDisplayStyle displayStyle,
        TrayBarStyle barStyle)
    {
        if (entries.Count == 0)
            return "QuotaAI · No active providers";

        if (displayStyle == TrayDisplayStyle.Original)
        {
            if (barStyle == TrayBarStyle.Icon)
                return "QuotaAI";

            var items = barStyle == TrayBarStyle.Compact
                ? PickMostConstrained(entries)
                : entries;

            var summary = string.Join("\n", items.Select(e => FormatSingleSummaryLine(e, direction, fullNames: true)));
            if (summary.Length <= MaxWin32TooltipLength)
                return summary;

            var shortSummary = string.Join("\n", items.Select(e => FormatSingleSummaryLine(e, direction, fullNames: false)));
            return TruncateSafe(shortSummary, MaxWin32TooltipLength);
        }

        // Default TwoLineRemaining: show each configured service's 5h & 7d %!
        var fullLines = entries.Select(e => FormatFiveHourWeeklyLine(e, direction, tier: 0)).ToList();
        var fullText = string.Join("\n", fullLines);
        if (fullText.Length <= MaxWin32TooltipLength)
            return fullText;

        var mediumLines = entries.Select(e => FormatFiveHourWeeklyLine(e, direction, tier: 1)).ToList();
        var mediumText = string.Join("\n", mediumLines);
        if (mediumText.Length <= MaxWin32TooltipLength)
            return mediumText;

        var compactLines = entries.Select(e => FormatFiveHourWeeklyLine(e, direction, tier: 2)).ToList();
        var compactText = string.Join(" · ", compactLines);
        return TruncateSafe(compactText, MaxWin32TooltipLength);
    }

    public static (string? FiveHourLabel, int? FiveHourValue, string? WeeklyLabel, int? WeeklyValue, bool IsTwoWindow)
        ExtractFiveHourAndWeekly(string providerId, ProviderUsage? usage, CountDirection direction)
    {
        var windows = usage?.Windows ?? [];
        if (providerId.Equals("codex", StringComparison.OrdinalIgnoreCase)
            || providerId.StartsWith("codex:", StringComparison.OrdinalIgnoreCase)
            || providerId.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "5h", FindPercent(windows, "5h limit", direction),
                "7d", FindPercent(windows, "Weekly limit", direction),
                true);
        }

        if (providerId.Equals("gemini", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "5h", FindPercent(windows, "Base model (5h)", direction),
                "7d", FindPercent(windows, "Base model (weekly)", direction),
                true);
        }

        if (providerId.Equals("zai", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "5h", FindPercent(windows, "Time quota", direction),
                "Tok", FindPercent(windows, "Token quota", direction),
                true);
        }

        return (null, null, null, null, false);
    }

    private static int? FindPercent(IReadOnlyList<UsageWindow> windows, string label, CountDirection direction)
    {
        var match = windows.FirstOrDefault(w => string.Equals(w.Label, label, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : direction.DisplayInt(match.UsedPercent);
    }

    private static string FormatFiveHourWeeklyLine(EntryInput entry, CountDirection direction, int tier)
    {
        var name = tier == 0 ? entry.DisplayName : entry.ShortCode;
        var staleSuffix = entry.State.Kind == ProviderStatusKind.Stale && tier == 0 ? " *" : "";

        if (entry.State.Kind == ProviderStatusKind.Loading)
            return tier == 2 ? $"{entry.ShortCode} …" : $"{name}: …";

        if (entry.State.Usage is null)
            return tier == 2 ? $"{entry.ShortCode} !" : $"{name}: unavailable";

        var (l1, v1, l2, v2, isTwoWindow) = ExtractFiveHourAndWeekly(entry.Id, entry.State.Usage, direction);
        if (isTwoWindow)
        {
            var s1 = v1 is { } n1 ? $"{n1}%" : "--";
            var s2 = v2 is { } n2 ? $"{n2}%" : "--";
            return tier switch
            {
                0 => $"{name}: {l1} {s1} · {l2} {s2}{staleSuffix}",
                1 => $"{name} {l1} {s1} · {l2} {s2}",
                _ => $"{entry.ShortCode} {s1}/{s2}"
            };
        }

        var single = entry.State.Usage.MenuSummary(direction) ?? "--";
        return tier == 2 ? $"{entry.ShortCode} {single}" : $"{name}: {single}{staleSuffix}";
    }

    private static string FormatSingleSummaryLine(EntryInput entry, CountDirection direction, bool fullNames)
    {
        var name = fullNames ? entry.DisplayName : entry.ShortCode;
        return entry.State.Kind switch
        {
            ProviderStatusKind.Loading => $"{name}: …",
            ProviderStatusKind.Error => $"{name}: !",
            _ => $"{name}: {entry.State.Usage?.MenuSummary(direction) ?? "--"}"
        };
    }

    private static IReadOnlyList<EntryInput> PickMostConstrained(IReadOnlyList<EntryInput> entries)
    {
        var worst = entries
            .Where(e => e.State.Usage?.WorstWindow is not null)
            .OrderByDescending(e => e.State.Usage!.WorstWindow!.UsedPercent)
            .FirstOrDefault();
        if (worst is not null)
            return [worst];

        var firstBalance = entries.FirstOrDefault(e => e.State.Usage?.Balance is not null);
        return firstBalance is not null ? [firstBalance] : entries.Take(1).ToList();
    }

    private static string TruncateSafe(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
}

public sealed class AlertTracker
{
    private readonly Dictionary<string, DateTimeOffset?> armedResets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> firedBalanceProviders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> firedRenewalAnchors = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<(string Title, string Body)> Evaluate(
        IUsageProvider provider,
        ProviderUsage usage,
        AppSettings settings,
        DateTimeOffset now)
    {
        var notifications = new List<(string Title, string Body)>();

        if (settings.NotificationsEnabled)
        {
            foreach (var window in usage.Windows)
            {
                var key = $"{provider.Id}|{window.Label}";
                if (armedResets.TryGetValue(key, out var prevReset))
                {
                    // Re-arm when reset timestamp advances or usage drops back below threshold
                    if ((window.ResetsAt is not null && prevReset != window.ResetsAt)
                        || window.UsedPercent < settings.NotificationThreshold - 5)
                    {
                        armedResets.Remove(key);
                    }
                }

                if (!armedResets.ContainsKey(key) && window.UsedPercent >= settings.NotificationThreshold)
                {
                    armedResets[key] = window.ResetsAt;
                    var metric = settings.CountDirection.PercentLabel(window.UsedPercent);
                    notifications.Add((
                        $"{provider.DisplayName} · {window.Label}",
                        $"{metric} ({window.FormatReset(settings.ResetTimeStyle, now) ?? "active window"})"));
                }
            }

            if (usage.Balance is { Kind: BalanceKind.Remaining } bal && bal.CurrencySymbol == "$")
            {
                if (bal.Remaining < settings.BalanceNotificationThreshold)
                {
                    if (firedBalanceProviders.Add(provider.Id))
                        notifications.Add(($"{provider.DisplayName} low balance", $"{bal.Display} remaining."));
                }
                else
                {
                    firedBalanceProviders.Remove(provider.Id);
                }
            }
        }

        if (settings.GetRenewal(provider.Id) is { RemindDaysBefore: > 0 } renewal)
        {
            var today = now.ToLocalTime().Date;
            var nextDate = renewal.NextRenewalDate(today);
            var daysUntil = (nextDate - today).Days;
            if (daysUntil >= 0 && daysUntil <= renewal.RemindDaysBefore)
            {
                if (!firedRenewalAnchors.TryGetValue(provider.Id, out var lastNext) || lastNext != nextDate)
                {
                    firedRenewalAnchors[provider.Id] = nextDate;
                    var when = daysUntil == 0 ? "today" : $"in {daysUntil} day(s)";
                    notifications.Add((
                        $"{provider.DisplayName} subscription renews {when}",
                        $"Billed through {renewal.Platform.DisplayName()} on {nextDate:MMM d}."));
                }
            }
        }

        return notifications;
    }
}

public static class StatusSnapshotWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string SnapshotPath() => Path.Combine(AppSettings.DefaultDirectory(), "status.json");

    public static void WriteOrDelete(
        AppSettings settings,
        IReadOnlyList<IUsageProvider> providers,
        IReadOnlyDictionary<string, ProviderState> states)
    {
        var path = SnapshotPath();
        if (!settings.AgentAccessEnabled)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var payload = new
            {
                schemaVersion = 1,
                generatedAt = DateTimeOffset.UtcNow,
                providers = providers
                    .Where(p => settings.GetMode(p.Id).IsVisible(p.IsDetected))
                    .Select(p =>
                    {
                        var st = states.TryGetValue(p.Id, out var s) ? s : ProviderState.Loading;
                        return new
                        {
                            id = p.Id,
                            displayName = p.DisplayName,
                            shortCode = p.ShortCode,
                            status = st.Kind.ToString().ToLowerInvariant(),
                            planName = st.Usage?.PlanName,
                            asOf = st.Usage?.AsOf,
                            windows = st.Usage?.Windows.Select(w => new
                            {
                                label = w.Label,
                                usedPercent = Math.Round(w.UsedPercent, 1),
                                remainingPercent = w.RemainingPercent,
                                resetsAt = w.ResetsAt
                            }),
                            balance = st.Usage?.Balance is { } b
                                ? new { remaining = b.Remaining, used = b.Used, currencySymbol = b.CurrencySymbol, kind = b.Kind.ToString().ToLowerInvariant() }
                                : null,
                            error = st.ErrorMessage
                        };
                    })
                    .ToList()
            };

            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(payload, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public static class DiagnosticsReport
{
    public static string Build(
        AppSettings settings,
        IReadOnlyList<IUsageProvider> providers,
        IReadOnlyDictionary<string, ProviderState> states)
    {
        var sb = new StringBuilder();
        sb.AppendLine("QuotaAI Windows Diagnostics (redacted — no keys, tokens, or emails)");
        sb.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        sb.AppendLine($"OS: {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
        sb.AppendLine($".NET: {Environment.Version}");
        sb.AppendLine($"Refresh interval: {settings.RefreshIntervalSeconds}s | Count: {settings.CountDirection} | Reset: {settings.ResetTimeStyle}");
        sb.AppendLine($"Tray style: {settings.TrayDisplayStyle} ({settings.TrayBarStyle}) | Agent snapshot: {settings.AgentAccessEnabled}");
        sb.AppendLine();
        sb.AppendLine("Providers:");
        foreach (var p in providers)
        {
            var mode = settings.GetMode(p.Id);
            var st = states.TryGetValue(p.Id, out var s) ? s : ProviderState.Loading;
            var winCount = st.Usage?.Windows.Count ?? 0;
            var hasBal = st.Usage?.Balance is not null;
            sb.AppendLine($"- {p.Id} ({p.ShortCode}): mode={mode}, inTray={settings.ShowsInTray(p.Id)}, detected={p.IsDetected}, state={st.Kind}, windows={winCount}, balance={hasBal}");
        }
        return sb.ToString();
    }
}
