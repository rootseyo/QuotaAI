using System.Reflection;
using QuotaAI.Core;

namespace QuotaAI.Tray;

internal sealed class TrayApplication : ApplicationContext
{
    private readonly CancellationTokenSource shutdown = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 5 * 60 * 1000 };
    private readonly Icon icon;
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip menu = new();
    private readonly WidgetForm widget;
    private readonly ToolStripMenuItem visibility;
    private readonly ToolStripMenuItem pin;
    private readonly ToolStripMenuItem refresh;
    private CodexUsage? lastUsage;
    private Task? activeRefresh;
    private bool exiting;

    public TrayApplication()
    {
        using var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("QuotaAI.ico")!;
        icon = new Icon(iconStream, new Size(32, 32));
        widget = new WidgetForm(icon);
        visibility = new ToolStripMenuItem("Hide widget", null, (_, _) => ToggleWidget());
        pin = new ToolStripMenuItem("Always on top", null, (_, _) => widget.SetPinned(!widget.TopMost))
        {
            Checked = widget.TopMost
        };
        refresh = new ToolStripMenuItem("Refresh now", null, async (_, _) => await RefreshAsync());
        menu.Items.AddRange([visibility, pin, refresh, new ToolStripSeparator(),
            new ToolStripMenuItem("Exit QuotaAI", null, async (_, _) => await ExitAsync())]);

        tray = new NotifyIcon
        {
            Icon = icon,
            Text = "QuotaAI · Checking Codex…",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ToggleWidget();
        };
        widget.VisibleChanged += (_, _) => visibility.Text = widget.Visible ? "Hide widget" : "Show widget";
        widget.PinnedChanged += (_, _) => pin.Checked = widget.TopMost;
        widget.RefreshRequested += async (_, _) => await RefreshAsync();
        widget.Shown += async (_, _) => await RefreshAsync();
        timer.Tick += async (_, _) => await RefreshAsync();
        timer.Start();
        widget.Show();
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

    private Task RefreshAsync()
    {
        if (exiting)
            return Task.CompletedTask;
        if (activeRefresh is { IsCompleted: false })
            return activeRefresh;

        return activeRefresh = RefreshUsageAsync();
    }

    private async Task RefreshUsageAsync()
    {
        refresh.Enabled = false;
        widget.SetRefreshing(true);
        try
        {
            var usage = await CodexAppServer.ReadAsync(shutdown.Token);
            if (exiting)
                return;

            lastUsage = usage;
            widget.ShowUsage(usage, usage.FiveHour is null && usage.Weekly is null
                ? "No 5-hour or weekly quota reported."
                : $"Updated {DateTime.Now:t}");
            UpdateTooltip(usage, stale: false);
        }
        catch (CodexUnavailableException error)
        {
            if (!exiting)
            {
                widget.ShowUsage(lastUsage, lastUsage is null ? error.Message : $"Cached data. {error.Message}");
                if (lastUsage is { } cached)
                    UpdateTooltip(cached, stale: true);
                else
                    tray.Text = "QuotaAI · Codex usage unavailable";
            }
        }
        catch (OperationCanceledException) when (exiting) { }
        finally
        {
            if (!exiting)
            {
                refresh.Enabled = true;
                widget.SetRefreshing(false);
            }
        }
    }

    private void UpdateTooltip(CodexUsage usage, bool stale)
    {
        var state = stale ? " (cached)" : "";
        tray.Text = $"QuotaAI · Codex{state}\n5h {WidgetForm.Percent(usage.FiveHour)}  •  7d {WidgetForm.Percent(usage.Weekly)}";
    }

    private async Task ExitAsync()
    {
        if (exiting)
            return;

        exiting = true;
        timer.Stop();
        tray.Visible = false;
        widget.Hide();
        shutdown.Cancel();
        try
        {
            if (activeRefresh is not null)
                await activeRefresh;
        }
        finally
        {
            ExitThread();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            tray.Dispose();
            menu.Dispose();
            widget.Dispose();
            icon.Dispose();
            shutdown.Dispose();
        }
        base.Dispose(disposing);
    }
}
