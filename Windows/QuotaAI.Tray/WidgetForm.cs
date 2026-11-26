using QuotaAI.Core;

namespace QuotaAI.Tray;

internal sealed class WidgetForm : Form
{
    private readonly Label fiveHour = new();
    private readonly Label weekly = new();
    private readonly Label fiveHourReset = new();
    private readonly Label weeklyReset = new();
    private readonly Label status = new();
    private readonly Button refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly CheckBox pin = new() { Text = "Always on top", AutoSize = true, Checked = true };

    public event EventHandler? PinnedChanged;
    public event EventHandler? RefreshRequested;

    public WidgetForm(Icon icon)
    {
        Text = "QuotaAI";
        Icon = icon;
        TopMost = true;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(314, 218);
        Font = SystemFonts.MessageBoxFont ?? Control.DefaultFont;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 10, 14, 8),
            ColumnCount = 2,
            RowCount = 5
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        var header = new Label { Text = "Codex · Remaining", AutoSize = true, Anchor = AnchorStyles.Left };
        layout.Controls.Add(header, 0, 0);
        layout.SetColumnSpan(header, 2);

        AddUsageRow(layout, 1, "5-hour window", fiveHour, fiveHourReset);
        AddUsageRow(layout, 2, "Weekly window", weekly, weeklyReset);

        status.Dock = DockStyle.Fill;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.AutoEllipsis = true;
        layout.Controls.Add(status, 0, 3);
        layout.SetColumnSpan(status, 2);

        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        controls.Controls.Add(pin);
        controls.Controls.Add(refresh);
        layout.Controls.Add(controls, 0, 4);
        layout.SetColumnSpan(controls, 2);
        Controls.Add(layout);

        pin.CheckedChanged += (_, _) =>
        {
            TopMost = pin.Checked;
            PinnedChanged?.Invoke(this, EventArgs.Empty);
        };
        refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
        ShowUsage(null, "Checking Codex…");
    }

    protected override void OnShown(EventArgs e)
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(Math.Max(area.Left, area.Right - Width - 16),
            Math.Max(area.Top, area.Bottom - Height - 16));
        base.OnShown(e);
    }

    public void SetPinned(bool pinned) => pin.Checked = pinned;

    public void SetRefreshing(bool isRefreshing)
    {
        refresh.Enabled = !isRefreshing;
        if (isRefreshing)
            status.Text = "Checking Codex…";
    }

    public void ShowUsage(CodexUsage? usage, string message)
    {
        SetWindow(fiveHour, fiveHourReset, "5-hour window", usage?.FiveHour);
        SetWindow(weekly, weeklyReset, "Weekly window", usage?.Weekly);
        status.Text = message;
    }

    internal static string Percent(UsageWindow? window) =>
        window is null ? "—" : $"{window.RemainingPercent}%";

    private void AddUsageRow(TableLayoutPanel layout, int row, string name, Label percent, Label reset)
    {
        percent.Font = new Font(Font.FontFamily, 21, FontStyle.Bold);
        percent.Anchor = AnchorStyles.Left;
        percent.AutoSize = true;

        var description = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0, 5, 0, 0)
        };
        description.Controls.Add(new Label { Text = name, AutoSize = true });
        reset.AutoSize = true;
        description.Controls.Add(reset);
        layout.Controls.Add(percent, 0, row);
        layout.Controls.Add(description, 1, row);
    }

    private void SetWindow(Label percent, Label reset, string name, UsageWindow? window)
    {
        percent.Text = Percent(window);
        percent.ForeColor = window is null || SystemInformation.HighContrast
            ? ForeColor
            : QuotaColor(window.RemainingPercent, BackColor.GetBrightness() < 0.5f);
        percent.AccessibleName = window is null
            ? $"{name} remaining quota unavailable"
            : $"{name}, {window.RemainingPercent} percent remaining";
        reset.Text = window?.ResetsAt is { } timestamp
            ? $"Resets {timestamp.ToLocalTime():ddd HH:mm}"
            : "Reset time unavailable";
    }

    private static Color QuotaColor(int remaining, bool darkBackground)
    {
        var rgb = remaining switch
        {
            > 75 => darkBackground ? 0x70DEA8 : 0x136543, // Green
            > 50 => darkBackground ? 0x82B8FF : 0x145ABB, // Blue
            > 25 => darkBackground ? 0xF6D36A : 0x705400, // Gold
            > 0 => darkBackground ? 0xFF9D59 : 0x9A3B00,  // Orange
            _ => darkBackground ? 0xFF8496 : 0xAF2039     // Red
        };
        return Color.FromArgb(unchecked((int)0xFF000000) | rgb);
    }
}
