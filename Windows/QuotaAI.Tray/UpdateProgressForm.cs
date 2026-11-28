namespace QuotaAI.Tray;

/// <summary>Small modeless dialog showing download progress with a Cancel button.</summary>
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
