using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using QuotaAI.Core;

namespace QuotaAI.Tray;

/// <summary>
/// Compact single-row horizontal bar widget modeled after the macOS menu bar
/// two-line renderer: each active AI service displays its icon alongside two
/// stacked quota lines (e.g. "5h 40%" above "7d 60%").
/// </summary>
internal sealed class WidgetForm : Form
{
    private const int WsExToolWindow = 0x00000080;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmExitSizeMove = 0x0232;
    private const int HtCaption = 0x0002;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    private sealed record BarLine(string? Prefix, string ValueText, int? RemainingForColor);

    private sealed record BarSegment(
        string Id,
        string ShortCode,
        string DisplayName,
        IReadOnlyList<BarLine> Lines,
        bool Stale,
        string HoverDetail,
        Rectangle Bounds,
        Rectangle IconBounds,
        int TextX);

    private readonly Icon appIcon;
    private readonly UsageCoordinator coordinator;
    private readonly ToolTip toolTip = new() { InitialDelay = 250, ReshowDelay = 150 };

    private bool isDarkTheme;
    private bool initialPlacementApplied;
    private List<BarSegment> segments = [];

    private Font lineLabelFont = null!;
    private Font lineValueFont = null!;
    private Font badgeFont = null!;
    private Font emptyFont = null!;

    private Rectangle settingsButtonBounds;
    private Rectangle closeButtonBounds;
    private int hoveredControl = -1; // 0 = settings, 1 = close, >= 10 = segment index + 10
    private string? activeTooltipText;

    public event EventHandler? PinnedChanged;
    public event EventHandler? PositionSaved;
    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;

    public WidgetForm(Icon icon, ContextMenuStrip contextMenu, UsageCoordinator usageCoordinator)
    {
        appIcon = icon;
        coordinator = usageCoordinator;
        var settings = coordinator.Settings;

        Text = "QuotaAI";
        Icon = icon;
        TopMost = settings.WidgetAlwaysOnTop;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        KeyPreview = true;
        ContextMenuStrip = contextMenu;
        AccessibleName = "QuotaAI Quota Bar";

        DetectSystemTheme();
        RebuildFonts();
        RecalculateLayout();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };

        ApplyInitialPlacement(settings);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExToolWindow;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDwmAttributes();
    }

    protected override void OnShown(EventArgs e)
    {
        if (!initialPlacementApplied)
        {
            PlaceInDefaultCorner();
            initialPlacementApplied = true;
        }
        base.OnShown(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        RebuildFonts();
        RecalculateLayout();
        ClampCurrentLocation();
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            Hide();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.F5 && !coordinator.IsRefreshing)
        {
            RefreshRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.Control && e.KeyCode == Keys.Oemcomma)
        {
            SettingsRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.Location);
        if (hit != hoveredControl)
        {
            hoveredControl = hit;
            Cursor = hit is 0 or 1 ? Cursors.Hand : Cursors.SizeAll;

            string? tip = hit switch
            {
                0 => "Settings (Ctrl+,)",
                1 => "Hide widget (Esc)",
                >= 10 when hit - 10 < segments.Count => segments[hit - 10].HoverDetail,
                _ => null
            };

            if (tip != activeTooltipText)
            {
                activeTooltipText = tip;
                if (tip is null)
                    toolTip.Hide(this);
                else
                    toolTip.Show(tip, this, e.Location.X + S(10), e.Location.Y - S(42), 5000);
            }
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hoveredControl != -1)
        {
            hoveredControl = -1;
            activeTooltipText = null;
            toolTip.Hide(this);
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && HitTest(e.Location) is not (0 or 1))
            SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        var hit = HitTest(e.Location);
        if (hit == 0)
        {
            SettingsRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (hit == 1)
        {
            Hide();
            return;
        }

        ReleaseCapture();
        SendMessage(Handle, WmNcLButtonDown, HtCaption, 0);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WmExitSizeMove)
        {
            ClampCurrentLocation();
            PositionSaved?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetPinned(bool pinned)
    {
        if (TopMost == pinned)
            return;

        TopMost = pinned;
        PinnedChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    public void RefreshView()
    {
        var anchorRight = Right;
        var hadPlacement = initialPlacementApplied;
        RecalculateLayout();
        if (hadPlacement)
        {
            var area = Screen.FromRectangle(Bounds).WorkingArea;
            // Keep right edge stable when sitting near the right half of the screen
            if (anchorRight > area.Left + area.Width / 2)
                Location = new Point(anchorRight - Width, Location.Y);
            ClampCurrentLocation();
        }
        Invalidate();
    }

    internal static Color ColorFromRgb(int rgb) =>
        Color.FromArgb(unchecked((int)0xFF000000) | (rgb & 0xFFFFFF));

    private void ApplyInitialPlacement(AppSettings settings)
    {
        if (settings.WidgetX is { } x && settings.WidgetY is { } y)
        {
            var targetRect = new Rectangle(x, y, Width, Height);
            var area = Screen.FromRectangle(targetRect).WorkingArea;
            var (cx, cy) = WidgetSettings.ClampToWorkingArea(
                x, y, Width, Height, area.Left, area.Top, area.Right, area.Bottom);
            Location = new Point(cx, cy);
            initialPlacementApplied = true;
        }
    }

    private void PlaceInDefaultCorner()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromPoint(Cursor.Position).WorkingArea;
        var margin = S(14);
        Location = new Point(
            Math.Max(area.Left, area.Right - Width - margin),
            Math.Max(area.Top, area.Bottom - Height - margin));
    }

    private void ClampCurrentLocation()
    {
        var area = Screen.FromRectangle(Bounds).WorkingArea;
        var (cx, cy) = WidgetSettings.ClampToWorkingArea(
            Location.X, Location.Y, Width, Height, area.Left, area.Top, area.Right, area.Bottom);
        if (cx != Location.X || cy != Location.Y)
            Location = new Point(cx, cy);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (IsDisposed)
            return;

        DetectSystemTheme();
        if (IsHandleCreated)
            ApplyDwmAttributes();
        RecalculateLayout();
        Invalidate();
    }

    private void DetectSystemTheme()
    {
        if (SystemInformation.HighContrast)
        {
            isDarkTheme = SystemColors.Window.GetBrightness() < 0.5f;
            BackColor = SystemColors.Window;
            ForeColor = SystemColors.WindowText;
            return;
        }

        var dark = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
                dark = value == 0;
        }
        catch
        {
            dark = false;
        }

        isDarkTheme = dark;
        BackColor = dark ? Color.FromArgb(24, 27, 34) : Color.FromArgb(250, 251, 254);
        ForeColor = dark ? Color.FromArgb(242, 245, 250) : Color.FromArgb(21, 25, 34);
    }

    private void ApplyDwmAttributes()
    {
        try
        {
            var dark = isDarkTheme ? 1 : 0;
            _ = DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            var corner = DwmwcpRound;
            _ = DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private void RebuildFonts()
    {
        lineLabelFont?.Dispose();
        lineValueFont?.Dispose();
        badgeFont?.Dispose();
        emptyFont?.Dispose();

        var family = (SystemFonts.MessageBoxFont ?? DefaultFont).FontFamily;
        lineLabelFont = new Font(family, S(11), FontStyle.Bold, GraphicsUnit.Pixel);
        lineValueFont = new Font(family, S(12), FontStyle.Bold, GraphicsUnit.Pixel);
        badgeFont = new Font(family, S(10), FontStyle.Bold, GraphicsUnit.Pixel);
        emptyFont = new Font(family, S(12), FontStyle.Bold, GraphicsUnit.Pixel);
    }

    private void RecalculateLayout()
    {
        var padX = S(11);
        var padY = S(7);
        var iconSize = S(26);
        var iconTextGap = S(6);
        var prefixValueGap = S(4);
        var segmentGap = S(14);
        var lineGap = S(1);

        var lineH = Math.Max(Measure("5h", lineLabelFont).Height, Measure("100%", lineValueFont).Height);
        var twoLineH = lineH * 2 + lineGap;
        var contentH = Math.Max(iconSize, twoLineH);
        var barHeight = contentH + padY * 2;

        var activeProviders = coordinator.TrayProviders;
        if (activeProviders.Count == 0)
            activeProviders = coordinator.VisibleProviders;

        var built = new List<BarSegment>(activeProviders.Count);
        var x = padX;

        for (var i = 0; i < activeProviders.Count; i++)
        {
            if (i > 0)
                x += segmentGap;

            var p = activeProviders[i];
            var st = coordinator.GetState(p.Id);
            var lines = BuildBarLines(p, st);
            var hover = BuildSegmentHoverDetail(p, st);

            var maxPrefixW = 0;
            var maxValW = 0;
            foreach (var ln in lines)
            {
                if (!string.IsNullOrEmpty(ln.Prefix))
                    maxPrefixW = Math.Max(maxPrefixW, Measure(ln.Prefix!, lineLabelFont).Width);
                maxValW = Math.Max(maxValW, Measure(ln.ValueText, lineValueFont).Width);
            }

            var textBlockW = maxPrefixW > 0
                ? maxPrefixW + prefixValueGap + maxValW
                : maxValW;

            var segStartX = x;
            var iconRect = new Rectangle(x, (barHeight - iconSize) / 2, iconSize, iconSize);
            var textX = iconRect.Right + iconTextGap;
            var segW = iconSize + iconTextGap + textBlockW;
            var segBounds = new Rectangle(segStartX, 0, segW, barHeight);

            built.Add(new BarSegment(
                p.Id,
                p.ShortCode,
                p.DisplayName,
                lines,
                st.Kind == ProviderStatusKind.Stale,
                hover,
                segBounds,
                iconRect,
                textX));

            x += segW;
        }

        segments = built;

        if (segments.Count == 0)
        {
            var emptyTextW = Measure("QuotaAI · Setup", emptyFont).Width;
            x = padX + iconSize + iconTextGap + emptyTextW;
        }

        // Right-end compact action buttons (Settings gear + Hide ×)
        var btnSize = S(22);
        var btnGap = S(2);
        x += S(12);
        var btnY = (barHeight - btnSize) / 2;
        settingsButtonBounds = new Rectangle(x, btnY, btnSize, btnSize);
        closeButtonBounds = new Rectangle(settingsButtonBounds.Right + btnGap, btnY, btnSize, btnSize);

        var barWidth = closeButtonBounds.Right + S(8);
        if (ClientSize.Width != barWidth || ClientSize.Height != barHeight)
            ClientSize = new Size(barWidth, barHeight);
    }

    private List<BarLine> BuildBarLines(IUsageProvider provider, ProviderState state)
    {
        var settings = coordinator.Settings;
        var dir = settings.CountDirection;

        var (l1, _, l2, _, isTwoWindow) = TrayTooltipFormatter.ExtractFiveHourAndWeekly(provider.Id, state.Usage, dir);
        if (isTwoWindow
            || provider.Id.Equals("codex", StringComparison.OrdinalIgnoreCase)
            || provider.Id.StartsWith("codex:", StringComparison.OrdinalIgnoreCase)
            || provider.Id.Equals("claude", StringComparison.OrdinalIgnoreCase)
            || provider.Id.Equals("gemini", StringComparison.OrdinalIgnoreCase)
            || provider.Id.Equals("zai", StringComparison.OrdinalIgnoreCase))
        {
            var topLabel = l1 ?? "5h";
            var bottomLabel = l2 ?? (provider.Id.Equals("zai", StringComparison.OrdinalIgnoreCase) ? "Tk" : "7d");

            if (state.Kind == ProviderStatusKind.Loading)
                return [new BarLine(topLabel, "…", null), new BarLine(bottomLabel, "…", null)];

            if (state.Usage is null)
                return [new BarLine(topLabel, "--", null), new BarLine(bottomLabel, "--", null)];

            var (win1, win2) = FindTwoWindows(provider.Id, state.Usage.Windows);
            return
            [
                FormatWindowBarLine(topLabel, win1, dir),
                FormatWindowBarLine(bottomLabel, win2, dir)
            ];
        }

        if (state.Kind == ProviderStatusKind.Loading)
            return [new BarLine(null, "…", null)];

        if (state.Usage is null)
            return [new BarLine(null, "--", null)];

        if (state.Usage.WorstWindow is { } worst)
        {
            return [new BarLine(null, dir.PercentLabel(worst.UsedPercent, compact: true), worst.RemainingPercent)];
        }

        return [new BarLine(null, state.Usage.Balance?.ShortDisplay ?? "--", null)];
    }

    private static (UsageWindow? First, UsageWindow? Second) FindTwoWindows(
        string providerId, IReadOnlyList<UsageWindow> windows)
    {
        UsageWindow? Find(string label) =>
            windows.FirstOrDefault(w => string.Equals(w.Label, label, StringComparison.OrdinalIgnoreCase));

        if (providerId.Equals("gemini", StringComparison.OrdinalIgnoreCase))
            return (Find("Base model (5h)"), Find("Base model (weekly)"));

        if (providerId.Equals("zai", StringComparison.OrdinalIgnoreCase))
            return (Find("Time quota"), Find("Token quota"));

        return (Find("5h limit"), Find("Weekly limit"));
    }

    private static BarLine FormatWindowBarLine(string prefix, UsageWindow? window, CountDirection direction)
    {
        if (window is null)
            return new BarLine(prefix, "--", null);

        return new BarLine(
            prefix,
            direction.PercentLabel(window.UsedPercent, compact: true),
            window.RemainingPercent);
    }

    private string BuildSegmentHoverDetail(IUsageProvider provider, ProviderState state)
    {
        var settings = coordinator.Settings;
        if (state.Kind == ProviderStatusKind.Loading)
            return $"{provider.DisplayName}: Loading…";

        if (state.Usage is null)
            return $"{provider.DisplayName}: {state.ErrorMessage ?? "Unavailable"}";

        var parts = new List<string>
        {
            string.IsNullOrWhiteSpace(state.Usage.PlanName)
                ? provider.DisplayName
                : $"{provider.DisplayName} ({state.Usage.PlanName})"
        };

        foreach (var w in state.Usage.Windows)
        {
            var val = settings.CountDirection.PercentLabel(w.UsedPercent);
            var rst = w.FormatReset(settings.ResetTimeStyle);
            parts.Add(rst is null ? $"{w.Label}: {val}" : $"{w.Label}: {val} · {rst}");
        }

        if (state.Usage.Balance is { } bal)
            parts.Add(bal.Display);

        if (state.Kind == ProviderStatusKind.Stale)
            parts.Add($"Cached ({state.ErrorMessage})");

        return string.Join("\n", parts);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var highContrast = SystemInformation.HighContrast;
        var primaryText = highContrast ? SystemColors.WindowText : ForeColor;
        var secondaryText = highContrast
            ? SystemColors.GrayText
            : (isDarkTheme ? Color.FromArgb(148, 159, 178) : Color.FromArgb(96, 106, 124));
        var borderColor = highContrast
            ? SystemColors.WindowFrame
            : (isDarkTheme ? Color.FromArgb(46, 52, 66) : Color.FromArgb(214, 222, 235));
        var sepColor = highContrast
            ? SystemColors.ControlDark
            : (isDarkTheme ? Color.FromArgb(42, 48, 62) : Color.FromArgb(224, 230, 240));

        var settings = coordinator.Settings;
        var lineH = Math.Max(Measure("5h", lineLabelFont).Height, Measure("100%", lineValueFont).Height);
        var lineGap = S(1);
        var prefixValueGap = S(4);

        if (segments.Count == 0)
        {
            var iconSz = S(24);
            var iconRect = new Rectangle(S(11), (ClientSize.Height - iconSz) / 2, iconSz, iconSz);
            g.DrawIcon(appIcon, iconRect);

            var msg = "QuotaAI · Setup";
            var msgSz = Measure(msg, emptyFont);
            TextRenderer.DrawText(g, msg, emptyFont,
                new Point(iconRect.Right + S(7), (ClientSize.Height - msgSz.Height) / 2),
                secondaryText, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
        else
        {
            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                if (i > 0)
                {
                    var sepX = seg.Bounds.Left - S(7);
                    using var sepPen = new Pen(sepColor, 1f);
                    g.DrawLine(sepPen, sepX, S(9), sepX, ClientSize.Height - S(9));
                }

                ProviderIcons.DrawProviderIconOrBadge(g, seg.Id, seg.ShortCode, seg.IconBounds, badgeFont);

                var maxPrefixW = 0;
                foreach (var ln in seg.Lines)
                {
                    if (!string.IsNullOrEmpty(ln.Prefix))
                        maxPrefixW = Math.Max(maxPrefixW, Measure(ln.Prefix!, lineLabelFont).Width);
                }

                if (seg.Lines.Count == 1)
                {
                    var ln = seg.Lines[0];
                    var y = (ClientSize.Height - lineH) / 2;
                    DrawBarLine(g, seg.TextX, y, maxPrefixW, prefixValueGap, ln, seg.Stale, settings, primaryText, secondaryText);
                }
                else
                {
                    var totalH = lineH * 2 + lineGap;
                    var topY = (ClientSize.Height - totalH) / 2;
                    DrawBarLine(g, seg.TextX, topY, maxPrefixW, prefixValueGap, seg.Lines[0], seg.Stale, settings, primaryText, secondaryText);
                    DrawBarLine(g, seg.TextX, topY + lineH + lineGap, maxPrefixW, prefixValueGap, seg.Lines[1], seg.Stale, settings, primaryText, secondaryText);
                }
            }
        }

        // Right-end subtle divider + compact Settings / Close buttons
        var actionSepX = settingsButtonBounds.Left - S(6);
        using (var sepPen = new Pen(sepColor, 1f))
            g.DrawLine(sepPen, actionSepX, S(9), actionSepX, ClientSize.Height - S(9));

        DrawMiniButton(g, settingsButtonBounds, kind: 0, hovered: hoveredControl == 0, primaryText, secondaryText);
        DrawMiniButton(g, closeButtonBounds, kind: 1, hovered: hoveredControl == 1, primaryText, secondaryText);

        using var borderPen = new Pen(borderColor, 1f);
        g.DrawRectangle(borderPen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    private void DrawBarLine(
        Graphics g,
        int x,
        int y,
        int prefixColWidth,
        int gap,
        BarLine line,
        bool stale,
        AppSettings settings,
        Color primaryText,
        Color secondaryText)
    {
        var valX = x;
        if (!string.IsNullOrEmpty(line.Prefix))
        {
            var pSz = Measure(line.Prefix!, lineLabelFont);
            var vSz = Measure(line.ValueText, lineValueFont);
            var pY = y + Math.Max(0, (vSz.Height - pSz.Height) / 2);
            TextRenderer.DrawText(g, line.Prefix!, lineLabelFont, new Point(x, pY),
                secondaryText, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            valX = x + prefixColWidth + gap;
        }

        Color valColor;
        if (SystemInformation.HighContrast)
        {
            valColor = primaryText;
        }
        else if (stale || line.RemainingForColor is null)
        {
            valColor = line.ValueText is "--" or "…" ? secondaryText : primaryText;
        }
        else
        {
            valColor = ColorFromRgb(RemainingQuotaPalette.GetRgb(
                settings.RemainingColorOverrides, line.RemainingForColor.Value, isDarkTheme));
        }

        TextRenderer.DrawText(g, line.ValueText, lineValueFont, new Point(valX, y),
            valColor, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    private void DrawMiniButton(
        Graphics g,
        Rectangle rect,
        int kind,
        bool hovered,
        Color primaryText,
        Color secondaryText)
    {
        if (hovered)
        {
            var bg = SystemInformation.HighContrast
                ? SystemColors.Highlight
                : (isDarkTheme ? Color.FromArgb(44, 50, 64) : Color.FromArgb(228, 234, 245));
            using var brush = new SolidBrush(bg);
            FillRoundedRect(g, brush, rect, S(5));
        }

        var fg = hovered
            ? (SystemInformation.HighContrast ? SystemColors.HighlightText : primaryText)
            : secondaryText;

        using var pen = new Pen(fg, Math.Max(1.4f, S(2) * 0.78f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        var cx = rect.Left + rect.Width / 2f;
        var cy = rect.Top + rect.Height / 2f;
        var r = rect.Width * 0.24f;

        if (kind == 0) // Settings gear
        {
            var innerR = r * 0.54f;
            g.DrawEllipse(pen, cx - innerR, cy - innerR, innerR * 2, innerR * 2);
            for (var deg = 0; deg < 360; deg += 45)
            {
                var rad = deg * Math.PI / 180.0;
                g.DrawLine(pen,
                    cx + (float)(Math.Cos(rad) * innerR * 1.15),
                    cy + (float)(Math.Sin(rad) * innerR * 1.15),
                    cx + (float)(Math.Cos(rad) * r * 1.08),
                    cy + (float)(Math.Sin(rad) * r * 1.08));
            }
        }
        else // Close '×'
        {
            var d = r * 0.8f;
            g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d);
            g.DrawLine(pen, cx + d, cy - d, cx - d, cy + d);
        }
    }

    private int HitTest(Point pt)
    {
        if (settingsButtonBounds.Contains(pt)) return 0;
        if (closeButtonBounds.Contains(pt)) return 1;
        for (var i = 0; i < segments.Count; i++)
            if (segments[i].Bounds.Contains(pt))
                return 10 + i;
        return -1;
    }

    private static Size Measure(string text, Font font) =>
        TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

    private static void FillRoundedRect(Graphics g, Brush brush, Rectangle rect, int radius)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;
        var d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d <= 1)
        {
            g.FillRectangle(brush, rect);
            return;
        }
        using var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    private int S(int logicalPixels) => LogicalToDeviceUnits(logicalPixels);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            toolTip.Dispose();
            lineLabelFont?.Dispose();
            lineValueFont?.Dispose();
            badgeFont?.Dispose();
            emptyFont?.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
