using System.Globalization;
using System.Text.Json;

namespace QuotaAI.Core;

public sealed record CodexUsage(
    UsageWindow? FiveHour,
    UsageWindow? Weekly,
    string? PlanName = null,
    string? AccountEmail = null,
    BalanceInfo? CreditsBalance = null)
{
    internal static CodexUsage Parse(JsonElement result, string? accountEmail = null, string? accountPlanType = null)
    {
        if (result.ValueKind != JsonValueKind.Object)
            throw UnreadableReply();

        var hasLimits = result.TryGetProperty("rateLimits", out var limits)
            && limits.ValueKind == JsonValueKind.Object;

        JsonElement chosenLimits = default;
        CodexUsage? bucketUsage = null;

        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets)
            && buckets.ValueKind == JsonValueKind.Object
            && buckets.TryGetProperty("codex", out var codex)
            && codex.ValueKind == JsonValueKind.Object)
        {
            var candidate = ReadWindows(codex);
            if (candidate.FiveHour is not null || candidate.Weekly is not null)
            {
                bucketUsage = candidate;
                chosenLimits = codex;
            }
        }

        if (bucketUsage is null)
        {
            if (!hasLimits)
                throw UnreadableReply();
            bucketUsage = ReadWindows(limits);
            chosenLimits = limits;
        }

        var rawPlan = accountPlanType;
        if (string.IsNullOrWhiteSpace(rawPlan)
            && chosenLimits.ValueKind == JsonValueKind.Object
            && chosenLimits.TryGetProperty("planType", out var pt)
            && pt.ValueKind == JsonValueKind.String)
        {
            rawPlan = pt.GetString();
        }

        BalanceInfo? balance = null;
        if (chosenLimits.ValueKind == JsonValueKind.Object
            && chosenLimits.TryGetProperty("credits", out var credits)
            && credits.ValueKind == JsonValueKind.Object
            && credits.TryGetProperty("hasCredits", out var hasCreds)
            && hasCreds.ValueKind == JsonValueKind.True)
        {
            var amount = 0.0;
            if (credits.TryGetProperty("balance", out var balProp))
            {
                if (balProp.ValueKind == JsonValueKind.String)
                    _ = double.TryParse(balProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out amount);
                else if (balProp.ValueKind == JsonValueKind.Number)
                    _ = balProp.TryGetDouble(out amount);
            }
            balance = new BalanceInfo(Math.Max(0, amount), null, "$", BalanceKind.Remaining);
        }

        return bucketUsage with
        {
            PlanName = FormatPlanName(rawPlan),
            AccountEmail = accountEmail,
            CreditsBalance = balance
        };
    }

    public ProviderUsage ToProviderUsage(DateTimeOffset now)
    {
        var windows = new List<UsageWindow>(2);
        if (FiveHour is not null)
            windows.Add(FiveHour);
        if (Weekly is not null)
            windows.Add(Weekly);
        return new ProviderUsage(PlanName, windows, now, CreditsBalance, AccountEmail: AccountEmail);
    }

    internal static string? FormatPlanName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return raw.Trim().ToLowerInvariant() switch
        {
            "pro" => "Pro",
            "plus" => "Plus",
            "prolite" => "Pro Lite",
            "team" => "Team",
            "business" => "Business",
            "enterprise" => "Enterprise",
            "free" => "Free",
            var other => char.ToUpperInvariant(other[0]) + other[1..]
        };
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

            var clampedUsed = Math.Clamp(used, 0, 100);
            if (minutes == 300)
                fiveHour ??= new UsageWindow("5h limit", clampedUsed, resetsAt);
            if (minutes == 10_080)
                weekly ??= new UsageWindow("Weekly limit", clampedUsed, resetsAt);
        }

        return new CodexUsage(fiveHour, weekly);
    }

    private static CodexUnavailableException UnreadableReply() =>
        new("Codex returned an unreadable usage response.");
}
