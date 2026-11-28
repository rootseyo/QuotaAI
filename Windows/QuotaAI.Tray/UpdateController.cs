using System.Diagnostics;
using QuotaAI.Core;

namespace QuotaAI.Tray;

/// <summary>Swaps the running executable for a downloaded one and restarts.</summary>
internal static class SelfUpdater
{
    public static Version CurrentVersion =>
        UpdateChecker.Normalize(typeof(SelfUpdater).Assembly.GetName().Version ?? new Version(0, 0, 0, 0));

    /// <summary>Development builds (Version 0.0.0) never replace themselves.</summary>
    public static bool IsDevBuild => CurrentVersion == new Version(0, 0, 0, 0);

    public static string? ExePath
    {
        get
        {
            var path = Environment.ProcessPath;
            return path is not null && string.Equals(Path.GetFileName(path), UpdateChecker.AssetName, StringComparison.OrdinalIgnoreCase)
                ? path
                : null;
        }
    }

    public static string StagingPath(string exe) => exe + ".new";

    /// <summary>Removes the previous version left behind by the last update.</summary>
    public static void CleanupLeftovers()
    {
        if (ExePath is not { } exe)
            return;
        foreach (var leftover in new[] { exe + ".old", StagingPath(exe) })
        {
            try { File.Delete(leftover); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Windows lets a running exe be renamed but not overwritten: move it aside, put the
    /// new file in its place (rolling back on failure), then start it. The new process
    /// waits for this one to exit before taking the single-instance lock.
    /// </summary>
    public static void ApplyAndRestart(string staged)
    {
        var exe = ExePath ?? throw new InvalidOperationException("Not running from QuotaAI.exe.");
        var old = exe + ".old";
        if (File.Exists(old))
            File.Delete(old);

        File.Move(exe, old);
        try
        {
            File.Move(staged, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }

        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.ArgumentList.Add("--restart-after");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(start);
    }
}

/// <summary>Owns the update check / download / install flow and its dialogs.</summary>
internal sealed class UpdateController
{
    private readonly AppSettings settings;
    private readonly Icon icon;
    private readonly Action<string, string> showBalloon;
    private readonly Func<Task> exitAsync;
    private UpdateInfo? pending;
    private bool busy;

    public UpdateController(AppSettings settings, Icon icon, Action<string, string> showBalloon, Func<Task> exitAsync)
    {
        this.settings = settings;
        this.icon = icon;
        this.showBalloon = showBalloon;
        this.exitAsync = exitAsync;
    }

    public string VersionText => SelfUpdater.IsDevBuild ? "development build" : SelfUpdater.CurrentVersion.ToString();

    /// <summary>Quiet startup check, at most once a day. Announces with a tray balloon.</summary>
    public async Task AutoCheckAsync()
    {
        if (!settings.AutoCheckForUpdates || SelfUpdater.IsDevBuild || busy)
            return;
        if (settings.LastUpdateCheckUtc is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(24))
            return;

        try
        {
            var info = await UpdateChecker.CheckAsync(SelfUpdater.CurrentVersion);
            settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            settings.Save();
            if (info is not null)
            {
                pending = info;
                showBalloon("QuotaAI update available", $"Version {info.Version} is ready. Click to install.");
            }
        }
        catch (UpdateException)
        {
            // Silent: a failed background check is retried next launch.
        }
    }

    public void OnUpdateBalloonClicked()
    {
        if (pending is { } info)
            _ = PromptAndInstallAsync(info);
    }

    public async Task CheckInteractiveAsync()
    {
        if (busy)
            return;
        if (SelfUpdater.IsDevBuild)
        {
            Info("This is a development build, so update checks are disabled.");
            return;
        }

        busy = true;
        UpdateInfo? info;
        try
        {
            info = await UpdateChecker.CheckAsync(SelfUpdater.CurrentVersion);
            settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            settings.Save();
        }
        catch (UpdateException ex)
        {
            busy = false;
            Info(ex.Message, MessageBoxIcon.Warning);
            return;
        }
        busy = false;

        if (info is null)
            Info($"QuotaAI is up to date (version {VersionText}).");
        else
            await PromptAndInstallAsync(info);
    }

    private async Task PromptAndInstallAsync(UpdateInfo info)
    {
        if (busy)
            return;

        var answer = MessageBox.Show(
            $"QuotaAI {info.Version} is available (you have {VersionText}).\n\n" +
            "Download and install it now? QuotaAI will restart.",
            "QuotaAI update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
            return;

        busy = true;
        try
        {
            await InstallAsync(info);
        }
        finally
        {
            busy = false;
        }
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
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            dialog.Close();
            OfferReleasePage($"The update could not be installed: {ex.Message}", info);
            return;
        }

        await exitAsync();
    }

    private static void OfferReleasePage(string message, UpdateInfo info)
    {
        var answer = MessageBox.Show($"{message}\n\nOpen the release page to download it manually?",
            "QuotaAI update", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer == DialogResult.Yes)
            Process.Start(new ProcessStartInfo(info.ReleaseUrl) { UseShellExecute = true });
    }

    private static void Info(string message, MessageBoxIcon icon = MessageBoxIcon.Information) =>
        MessageBox.Show(message, "QuotaAI update", MessageBoxButtons.OK, icon);
}

internal sealed class UpdateProgressForm : Form
{
    private readonly ProgressBar bar = new() { Dock = DockStyle.Top, Height = 22, Style = ProgressBarStyle.Marquee };

    public UpdateProgressForm(Icon icon, Version version, CancellationTokenSource cts)
    {
        Text = "Updating QuotaAI";
        Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(380, 110);
        Padding = new Padding(16);

        var label = new Label { Text = $"Downloading QuotaAI {version}…", Dock = DockStyle.Top, Height = 28 };
        var cancel = new Button { Text = "Cancel", Dock = DockStyle.Bottom, Height = 30 };
        cancel.Click += (_, _) => cts.Cancel();
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
                cts.Cancel();
        };
        Controls.Add(cancel);
        Controls.Add(bar);
        Controls.Add(label);
    }

    public void SetProgress(double fraction)
    {
        if (bar.Style != ProgressBarStyle.Continuous)
            bar.Style = ProgressBarStyle.Continuous;
        bar.Value = Math.Clamp((int)(fraction * 100), 0, 100);
    }
}
