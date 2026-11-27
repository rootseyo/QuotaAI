using System.Text.Json;

namespace QuotaAI.Core;

public sealed class AppSettings
{
    public static readonly int[] RefreshIntervalOptions = [30, 60, 300];
    public static readonly double[] NotificationThresholdOptions = [70, 80, 90];
    public static readonly double[] BalanceThresholdOptions = [1, 5, 10];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public int RefreshIntervalSeconds { get; set; } = 60;
    public bool NotificationsEnabled { get; set; }
    public double NotificationThreshold { get; set; } = 80;
    public double BalanceNotificationThreshold { get; set; } = 5;
    public CountDirection CountDirection { get; set; } = CountDirection.Remaining;
    public ResetTimeStyle ResetTimeStyle { get; set; } = ResetTimeStyle.Relative;
    public TrayDisplayStyle TrayDisplayStyle { get; set; } = TrayDisplayStyle.TwoLineRemaining;
    public TrayBarStyle TrayBarStyle { get; set; } = TrayBarStyle.Full;
    public Dictionary<string, int> RemainingColorOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AgentAccessEnabled { get; set; }
    public Dictionary<string, ProviderMode> ProviderModes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> ProviderShowInTray { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CodexAccountConfig> CodexExtraAccounts { get; set; } = [];
    public Dictionary<string, SubscriptionRenewal> SubscriptionRenewals { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int? WidgetX { get; set; }
    public int? WidgetY { get; set; }
    public bool WidgetAlwaysOnTop { get; set; } = true;

    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    public ProviderMode GetMode(string providerId) =>
        ProviderModes.TryGetValue(providerId, out var mode) ? mode : ProviderMode.Auto;

    public void SetMode(string providerId, ProviderMode mode) =>
        ProviderModes[providerId] = mode;

    public bool ShowsInTray(string providerId) =>
        !ProviderShowInTray.TryGetValue(providerId, out var show) || show;

    public void SetShowsInTray(string providerId, bool show) =>
        ProviderShowInTray[providerId] = show;

    public void SetRemainingColor(QuotaColorBand band, ThemeAppearance appearance, int rgb)
    {
        rgb &= 0xFFFFFF;
        var key = RemainingQuotaPalette.Key(band, appearance);
        if (rgb == RemainingQuotaPalette.DefaultRgb(band, appearance))
            RemainingColorOverrides.Remove(key);
        else
            RemainingColorOverrides[key] = rgb;
    }

    public void ResetRemainingColors() => RemainingColorOverrides.Clear();

    public void AddCodexAccount(CodexAccountConfig account) =>
        CodexExtraAccounts.Add(account);

    public void RemoveCodexAccount(Guid id)
    {
        SubscriptionRenewals.Remove($"codex:{id}");
        CodexExtraAccounts.RemoveAll(a => a.Id == id);
    }

    public SubscriptionRenewal? GetRenewal(string providerId) =>
        SubscriptionRenewals.TryGetValue(providerId, out var renewal) ? renewal : null;

    public void SetRenewal(string providerId, SubscriptionRenewal renewal) =>
        SubscriptionRenewals[providerId] = renewal;

    public void RemoveRenewal(string providerId) =>
        SubscriptionRenewals.Remove(providerId);

    public static string DefaultDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.IsPathFullyQualified(localAppData) ? localAppData : Path.GetTempPath();
        return Path.Combine(root, "QuotaAI");
    }

    public static string DefaultPath() => Path.Combine(DefaultDirectory(), "settings.json");

    public static AppSettings Load(string? path = null)
    {
        var target = path ?? DefaultPath();
        try
        {
            if (File.Exists(target))
            {
                using var stream = File.OpenRead(target);
                var loaded = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
            else if (path is null)
            {
                // Migrate legacy widget.json if present
                var legacyWidget = WidgetSettings.Load();
                if (legacyWidget.X is not null || legacyWidget.Y is not null || !legacyWidget.AlwaysOnTop)
                {
                    return new AppSettings
                    {
                        WidgetX = legacyWidget.X,
                        WidgetY = legacyWidget.Y,
                        WidgetAlwaysOnTop = legacyWidget.AlwaysOnTop
                    };
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        Normalize();
        var target = path ?? DefaultPath();
        try
        {
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var temp = $"{target}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Normalize()
    {
        if (!RefreshIntervalOptions.Contains(RefreshIntervalSeconds))
            RefreshIntervalSeconds = 60;
        if (!NotificationThresholdOptions.Contains(NotificationThreshold))
            NotificationThreshold = 80;
        if (!BalanceThresholdOptions.Contains(BalanceNotificationThreshold))
            BalanceNotificationThreshold = 5;

        ProviderModes ??= new(StringComparer.OrdinalIgnoreCase);
        ProviderShowInTray ??= new(StringComparer.OrdinalIgnoreCase);
        RemainingColorOverrides ??= new(StringComparer.OrdinalIgnoreCase);
        CodexExtraAccounts ??= [];
        SubscriptionRenewals ??= new(StringComparer.OrdinalIgnoreCase);

        var invalidKeys = RemainingColorOverrides
            .Where(kv => kv.Value is < 0 or > 0xFFFFFF)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var k in invalidKeys)
            RemainingColorOverrides.Remove(k);
    }
}
