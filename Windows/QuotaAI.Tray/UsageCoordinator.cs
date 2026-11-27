using Microsoft.Win32;
using QuotaAI.Core;

namespace QuotaAI.Tray;

internal sealed class UsageCoordinator : IDisposable
{
    private readonly SynchronizationContext uiContext;
    private readonly CancellationTokenSource shutdown = new();
    private readonly System.Windows.Forms.Timer timer = new();
    private readonly Dictionary<string, ProviderState> states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> credentialGenerations = new(StringComparer.OrdinalIgnoreCase);
    private readonly AlertTracker alertTracker = new();
    private List<IUsageProvider> providers;
    private Task? activeRefresh;
    private bool disposed;

    public AppSettings Settings { get; }
    public IReadOnlyList<IUsageProvider> Providers => providers;
    public IReadOnlyDictionary<string, ProviderState> States => states;
    public DateTimeOffset? LastRefreshed { get; private set; }
    public bool IsRefreshing => activeRefresh is { IsCompleted: false };

    public event EventHandler? StateChanged;
    public event Action<string, string>? NotificationRequested;

    public UsageCoordinator(AppSettings settings)
    {
        Settings = settings;
        uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        providers = BuildDefaultProviders(settings.CodexExtraAccounts);
        foreach (var p in providers)
            states[p.Id] = ProviderState.Loading;

        timer.Interval = Math.Max(10, settings.RefreshIntervalSeconds) * 1000;
        timer.Tick += async (_, _) => await RefreshAsync(forceRefresh: false);
        timer.Start();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public static List<IUsageProvider> BuildDefaultProviders(IEnumerable<CodexAccountConfig> extraAccounts)
    {
        var list = new List<IUsageProvider> { new CodexProvider() };
        foreach (var account in extraAccounts)
            list.Add(new CodexProvider(account));

        list.AddRange(
        [
            new CursorProvider(),
            new ClaudeProvider(),
            new ClaudeApiProvider(),
            new AntigravityProvider(),
            new OpenRouterProvider(),
            new DeepSeekProvider(),
            new MoonshotProvider(),
            new ZaiProvider(),
            new VeniceProvider()
        ]);
        return list;
    }

    public IReadOnlyList<IUsageProvider> VisibleProviders =>
        providers.Where(p => Settings.GetMode(p.Id).IsVisible(p.IsDetected)).ToList();

    public IReadOnlyList<IUsageProvider> TrayProviders =>
        VisibleProviders.Where(p => Settings.ShowsInTray(p.Id)).ToList();

    public ProviderState GetState(string providerId) =>
        states.TryGetValue(providerId, out var st) ? st : ProviderState.Loading;

    public void RescheduleTimer()
    {
        timer.Stop();
        timer.Interval = Math.Max(10, Settings.RefreshIntervalSeconds) * 1000;
        timer.Start();
    }

    public void RebuildProviders()
    {
        var next = BuildDefaultProviders(Settings.CodexExtraAccounts);
        var oldIds = providers.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextIds = next.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        providers = next;
        foreach (var removed in oldIds.Except(nextIds, StringComparer.OrdinalIgnoreCase))
        {
            states.Remove(removed);
            CodexProvider.ClearCache(removed);
        }
        foreach (var added in nextIds.Except(oldIds, StringComparer.OrdinalIgnoreCase))
            states[added] = ProviderState.Loading;

        RaiseStateChanged();
        _ = RefreshAsync(forceRefresh: false);
    }

    public void CredentialDidChange(string providerId)
    {
        credentialGenerations[providerId] = credentialGenerations.GetValueOrDefault(providerId) + 1;
        states[providerId] = ProviderState.Loading;
        RaiseStateChanged();
        _ = RefreshAsync(forceRefresh: true);
    }

    public Task RefreshAsync(bool forceRefresh = false)
    {
        if (disposed)
            return Task.CompletedTask;
        if (!forceRefresh && activeRefresh is { IsCompleted: false })
            return activeRefresh;

        var task = RunRefreshAsync(forceRefresh);
        activeRefresh = task;
        RaiseStateChanged();
        return task;
    }

    private async Task RunRefreshAsync(bool forceRefresh)
    {
        var targets = VisibleProviders;
        LastRefreshed = DateTimeOffset.Now;

        try
        {
            var tasks = targets.Select(p => RefreshSingleAsync(p, forceRefresh)).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            PostToUi(() =>
            {
                if (disposed)
                    return;
                StatusSnapshotWriter.WriteOrDelete(Settings, providers, states);
                RaiseStateChanged();
            });
        }
    }

    private async Task RefreshSingleAsync(IUsageProvider provider, bool forceRefresh)
    {
        var startGen = 0;
        SendOrPost(sync: true, () => startGen = credentialGenerations.GetValueOrDefault(provider.Id));

        try
        {
            var usage = await provider.FetchAsync(forceRefresh, shutdown.Token).ConfigureAwait(false);
            PostToUi(() =>
            {
                if (disposed || credentialGenerations.GetValueOrDefault(provider.Id) != startGen)
                    return;

                states[provider.Id] = ProviderState.Ready(usage);
                foreach (var (title, body) in alertTracker.Evaluate(provider, usage, Settings, DateTimeOffset.Now))
                    NotificationRequested?.Invoke(title, body);
                RaiseStateChanged();
            });
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var message = ex is ProviderUnavailableException or CodexUnavailableException
                ? ex.Message
                : $"Could not refresh {provider.DisplayName}.";

            PostToUi(() =>
            {
                if (disposed || credentialGenerations.GetValueOrDefault(provider.Id) != startGen)
                    return;

                var prev = GetState(provider.Id);
                states[provider.Id] = ProviderState.NextAfterFailure(prev, message, DateTimeOffset.Now);
                RaiseStateChanged();
            });
        }
    }

    public string BuildTrayTooltip()
    {
        var entries = TrayProviders
            .Select(p => new TrayTooltipFormatter.EntryInput(p.Id, p.ShortCode, p.DisplayName, GetState(p.Id)))
            .ToList();
        return TrayTooltipFormatter.Format(
            entries,
            Settings.CountDirection,
            Settings.TrayDisplayStyle,
            Settings.TrayBarStyle);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume && !disposed)
            PostToUi(() => _ = RefreshAsync(forceRefresh: false));
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void PostToUi(Action action) => uiContext.Post(_ => action(), null);

    private void SendOrPost(bool sync, Action action)
    {
        if (SynchronizationContext.Current == uiContext)
            action();
        else if (sync)
            uiContext.Send(_ => action(), null);
        else
            uiContext.Post(_ => action(), null);
    }

    public async Task ShutdownAsync()
    {
        if (disposed)
            return;
        timer.Stop();
        shutdown.Cancel();
        try
        {
            if (activeRefresh is not null)
                await activeRefresh;
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        timer.Dispose();
        shutdown.Cancel();
        shutdown.Dispose();
    }
}

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QuotaAI";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
                return false;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe))
                    return false;
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal static class ProviderBadgePalette
{
    public static Color ColorFor(string providerId)
    {
        if (providerId.StartsWith("codex:", StringComparison.OrdinalIgnoreCase))
            return Color.FromArgb(15, 166, 140);

        return providerId.ToLowerInvariant() switch
        {
            "codex" => Color.FromArgb(15, 166, 140),
            "cursor" => Color.FromArgb(115, 92, 230),
            "claude" or "claude-api" => Color.FromArgb(217, 120, 64),
            "gemini" => Color.FromArgb(66, 133, 245),
            "openrouter" => Color.FromArgb(89, 107, 217),
            "deepseek" => Color.FromArgb(51, 153, 219),
            "moonshot" => Color.FromArgb(41, 56, 122),
            "zai" => Color.FromArgb(46, 158, 105),
            "venice" => Color.FromArgb(199, 74, 87),
            _ => Color.FromArgb(90, 102, 122)
        };
    }
}
