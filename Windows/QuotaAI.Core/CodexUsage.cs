using System.Text.Json;

namespace QuotaAI.Core;

public sealed record UsageWindow(int RemainingPercent, DateTimeOffset? ResetsAt);

public sealed record CodexUsage(UsageWindow? FiveHour, UsageWindow? Weekly)
{
    internal static CodexUsage Parse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
            throw UnreadableReply();

        var hasLimits = result.TryGetProperty("rateLimits", out var limits)
            && limits.ValueKind == JsonValueKind.Object;

        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets)
            && buckets.ValueKind == JsonValueKind.Object
            && buckets.TryGetProperty("codex", out var codex)
            && codex.ValueKind == JsonValueKind.Object)
        {
            var usage = ReadWindows(codex);
            if (usage.FiveHour is not null || usage.Weekly is not null)
                return usage;
        }

        if (hasLimits)
            return ReadWindows(limits);

        throw UnreadableReply();
    }

    private static CodexUsage ReadWindows(JsonElement limits)
    {
        UsageWindow? fiveHour = null;
        UsageWindow? weekly = null;
        foreach (var key in new[] { "primary", "secondary" })
        {
            if (!limits.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object
                || !window.TryGetProperty("windowDurationMins", out var duration)
                || duration.ValueKind != JsonValueKind.Number || !duration.TryGetInt32(out var minutes)
                || !window.TryGetProperty("usedPercent", out var percent)
                || percent.ValueKind != JsonValueKind.Number || !percent.TryGetDouble(out var used)
                || !double.IsFinite(used))
            {
                continue;
            }

            DateTimeOffset? resetsAt = null;
            if (window.TryGetProperty("resetsAt", out var reset) && reset.ValueKind == JsonValueKind.Number
                && reset.TryGetInt64(out var seconds) && seconds is >= 0 and <= 253402300799)
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }

            var value = new UsageWindow((int)Math.Round(100 - Math.Clamp(used, 0, 100), MidpointRounding.AwayFromZero), resetsAt);
            if (minutes == 300)
                fiveHour ??= value;
            if (minutes == 10_080)
                weekly ??= value;
        }

        return new CodexUsage(fiveHour, weekly);
    }

    private static CodexUnavailableException UnreadableReply() =>
        new("Codex returned an unreadable usage response.");
}
