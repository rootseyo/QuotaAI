using System.ComponentModel;
using System.Diagnostics;
using QuotaAI.Core;

namespace QuotaAI.Tray;

/// <summary>
/// Owns the update check / download / install flow and its dialogs. Background
/// checks run at launch and then hourly, but hit GitHub at most once a day. Only one
/// check, prompt, or install runs at a time.
/// </summary>
internal sealed class UpdateController : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly AppSettings settings;
    private readonly Icon icon;
    private readonly Action<string, string> showBalloon;
    private readonly Func<Task> exitAsync;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = (int)TimeSpan.FromHours(1).TotalMilliseconds };
    private UpdateInfo? pending;
    private Version? announced;
    private bool busy;

    public UpdateController(AppSettings settings, Icon icon, Action<string, string> showBalloon, Func<Task> exitAsync)
    {
        this.settings = settings;
        this.icon = icon;
        this.showBalloon = showBalloon;
        this.exitAsync = exitAsync;
        timer.Tick += async (_, _) => await AutoCheckAsync();
    }

    public string VersionText => SelfUpdater.VersionText;

    /// <summary>Runs the first background check and keeps checking while the app is open.</summary>
    public void Start()
    {
        timer.Start();
        _ = AutoCheckAsync();
    }

    public void OnUpdateBalloonClicked() => _ = InstallPendingAsync();

    public async Task CheckInteractiveAsync()
    {
        if (SelfUpdater.IsDevBuild)
        {
            Info("This is a development build, so update checks are disabled.");
            return;
        }
        if (!TryBegin())
        {
            Info("An update check is already in progress.");
            return;
        }

        try
        {
            UpdateInfo? info;
            try
            {
                info = await CheckAndRecordAsync();
            }
            catch (UpdateException ex)
            {
                Info(ex.Message, MessageBoxIcon.Warning);
                return;
            }

            if (info is null)
                Info($"QuotaAI is up to date (version {VersionText}).");
            else
                await PromptAndInstallAsync(info);
        }
        finally
        {
            busy = false;
        }
    }

    private async Task AutoCheckAsync()
    {
        if (!settings.AutoCheckForUpdates || SelfUpdater.IsDevBuild)
            return;
        if (settings.LastUpdateCheckUtc is { } last && DateTimeOffset.UtcNow - last < CheckInterval)
            return;
        if (!TryBegin())
            return;

        try
        {
            var info = await CheckAndRecordAsync();
            if (info is not null && info.Version != announced)
            {
                // Announce each new version once per run; the menu item still works afterwards.
                announced = info.Version;
                showBalloon("QuotaAI update available", $"Version {info.Version} is ready. Click to install.");
            }
        }
        catch (UpdateException)
        {
            // Silent: a failed background check is retried on a later tick.
        }
        finally
        {
            busy = false;
        }
    }

    private async Task InstallPendingAsync()
    {
        if (pending is not { } info || !TryBegin())
            return;
        try
        {
            await PromptAndInstallAsync(info);
        }
        finally
        {
            busy = false;
        }
    }

    private bool TryBegin()
    {
        if (busy)
            return false;
        busy = true;
        return true;
    }

    private async Task<UpdateInfo?> CheckAndRecordAsync()
    {
        var info = await UpdateChecker.CheckAsync(SelfUpdater.CurrentVersion);
        settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        settings.Save();
        pending = info;
        return info;
    }

    private async Task PromptAndInstallAsync(UpdateInfo info)
    {
        var answer = MessageBox.Show(
            $"QuotaAI {info.Version} is available (you have {VersionText}).\n\n" +
            "Download and install it now? QuotaAI will restart.",
            "QuotaAI update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes)
            await InstallAsync(info);
    }

    private async Task InstallAsync(UpdateInfo info)
    {
        if (SelfUpdater.ExePath is not { } exe)
        {
            OfferReleasePage("QuotaAI can only update itself when running as QuotaAI.exe.", info);
            return;
        }

        var staged = SelfUpdater.StagingPath(exe);
        using var cts = new CancellationTokenSource();
        using var dialog = new UpdateProgressForm(icon, info.Version, cts);
        dialog.Show();
        try
        {
            var progress = new Progress<double>(dialog.SetProgress);
            await UpdateChecker.DownloadAsync(info, staged, progress, cts.Token);
            dialog.Close();
            SelfUpdater.ApplyAndRestart(staged);
        }
        catch (OperationCanceledException)
        {
            dialog.Close();
            return;
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException || FileErrors.Is(ex))
        {
            dialog.Close();
            OfferReleasePage($"The update could not be installed: {ex.Message}", info);
            return;
        }

        timer.Stop();
        await exitAsync();
    }

    private static void OfferReleasePage(string message, UpdateInfo info)
    {
        var answer = MessageBox.Show($"{message}\n\nOpen the release page to download it manually?",
            "QuotaAI update", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(UpdateChecker.SafeReleaseUrl(info.ReleaseUrl)) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No default browser; nothing else to do.
        }
    }

    private static void Info(string message, MessageBoxIcon icon = MessageBoxIcon.Information) =>
        MessageBox.Show(message, "QuotaAI update", MessageBoxButtons.OK, icon);

    public void Dispose() => timer.Dispose();
}
