using System.Reflection;
using QuotaAI.Core;

namespace QuotaAI.Tray;

internal sealed class TrayApplication : ApplicationContext
{
    private readonly Icon icon;
    private readonly AppSettings settings;
    private readonly UsageCoordinator coordinator;
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip menu = new();
    private readonly WidgetForm widget;
    private readonly ToolStripMenuItem visibility;
    private readonly ToolStripMenuItem pin;
    private readonly ToolStripMenuItem refresh;
    private SettingsForm? settingsForm;
    private bool exiting;

    public TrayApplication()
    {
        using var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("QuotaAI.ico")!;
        icon = new Icon(iconStream, new Size(32, 32));
        settings = AppSettings.Load();
        coordinator = new UsageCoordinator(settings);

        visibility = new ToolStripMenuItem("Hide widget", null, (_, _) => ToggleWidget());
        pin = new ToolStripMenuItem("Always on top", null, (_, _) => widget!.SetPinned(!widget.TopMost))
        {
            Checked = settings.WidgetAlwaysOnTop
        };
        refresh = new ToolStripMenuItem("Refresh now", null, async (_, _) => await coordinator.RefreshAsync(forceRefresh: true));
        var settingsItem = new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings());
        menu.Items.AddRange(
        [
            visibility,
            pin,
            refresh,
            new ToolStripSeparator(),
            settingsItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit QuotaAI", null, async (_, _) => await ExitAsync())
        ]);

        widget = new WidgetForm(icon, menu, coordinator);

        tray = new NotifyIcon
        {
            Icon = icon,
            Text = coordinator.BuildTrayTooltip(),
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ToggleWidget();
        };
        tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenSettings();
        };

        widget.VisibleChanged += (_, _) => visibility.Text = widget.Visible ? "Hide widget" : "Show widget";
        widget.PinnedChanged += (_, _) =>
        {
            pin.Checked = widget.TopMost;
            SaveWidgetState();
            if (settingsForm is { IsDisposed: false })
                settingsForm.SyncWidgetPin(widget.TopMost);
        };
        widget.PositionSaved += (_, _) => SaveWidgetState();
        widget.RefreshRequested += async (_, _) => await coordinator.RefreshAsync(forceRefresh: true);
        widget.SettingsRequested += (_, _) => OpenSettings();

        coordinator.StateChanged += (_, _) => OnStateOrSettingsUpdated();
        coordinator.NotificationRequested += (title, body) =>
        {
            if (!exiting)
                tray.ShowBalloonTip(5000, title, body, ToolTipIcon.Info);
        };
        settings.Changed += OnStateOrSettingsUpdated;

        widget.Show();
        _ = coordinator.RefreshAsync(forceRefresh: false);
    }

    private void OnStateOrSettingsUpdated()
    {
        if (exiting)
            return;

        refresh.Enabled = !coordinator.IsRefreshing;
        tray.Text = coordinator.BuildTrayTooltip();
        widget.RefreshView();
    }

    private void OpenSettings()
    {
        if (settingsForm is { IsDisposed: false })
        {
            if (!settingsForm.Visible)
                settingsForm.Show();
            if (settingsForm.WindowState == FormWindowState.Minimized)
                settingsForm.WindowState = FormWindowState.Normal;
            settingsForm.Activate();
            return;
        }

        settingsForm = new SettingsForm(icon, coordinator, widget.SetPinned);
        settingsForm.Show();
        settingsForm.Activate();
    }

    private void SaveWidgetState()
    {
        settings.WidgetX = widget.Location.X;
        settings.WidgetY = widget.Location.Y;
        settings.WidgetAlwaysOnTop = widget.TopMost;
        settings.Save();
    }

    private void ToggleWidget()
    {
        if (widget.Visible)
        {
            widget.Hide();
            return;
        }

        widget.Show();
        widget.Activate();
    }

    private async Task ExitAsync()
    {
        if (exiting)
            return;

        exiting = true;
        SaveWidgetState();
        tray.Visible = false;
        widget.Hide();
        if (settingsForm is { IsDisposed: false })
            settingsForm.Close();

        await coordinator.ShutdownAsync();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            settings.Changed -= OnStateOrSettingsUpdated;
            if (settingsForm is { IsDisposed: false })
                settingsForm.Dispose();
            tray.Dispose();
            menu.Dispose();
            widget.Dispose();
            coordinator.Dispose();
            icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
