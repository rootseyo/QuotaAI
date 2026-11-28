using System.Diagnostics;
using QuotaAI.Core;

namespace QuotaAI.Tray;

// Settings window: Display tab.
internal sealed partial class SettingsForm
{
    private TabPage BuildDisplayTab(Action<bool> setWidgetPinned)
    {
        var page = new TabPage("Display") { Padding = new Padding(S(16)), AutoScroll = true, BackColor = SystemColors.Window };
        var stack = CreateVerticalStack(page);

        var widgetCard = CreateSectionCard(stack, "Widget");
        widgetPinToggle = new CheckBox
        {
            Text = "Keep widget always on top",
            AutoSize = true,
            Checked = settings.WidgetAlwaysOnTop,
            Margin = new Padding(0, S(4), 0, S(4))
        };
        widgetPinToggle.CheckedChanged += (_, _) => setWidgetPinned(widgetPinToggle.Checked);
        widgetCard.Controls.Add(widgetPinToggle);

        // 1. Units & Reset Times
        var formatCard = CreateSectionCard(stack, "Quota & Reset Formatting");

        var dirRow = CreateHorizontalRow();
        dirRow.Controls.Add(new Label { Text = "Count direction:", AutoSize = true, Margin = new Padding(0, S(5), S(12), 0) });
        var rbRemaining = new RadioButton { Text = "% left (remaining)", AutoSize = true, Checked = settings.CountDirection == CountDirection.Remaining };
        var rbUsed = new RadioButton { Text = "% used", AutoSize = true, Checked = settings.CountDirection == CountDirection.Used, Margin = new Padding(S(12), S(3), 0, 0) };
        rbRemaining.CheckedChanged += (_, _) =>
        {
            if (!rbRemaining.Checked) return;
            settings.CountDirection = CountDirection.Remaining;
            UpdateAlertsLabels();
            SaveAndNotify();
        };
        rbUsed.CheckedChanged += (_, _) =>
        {
            if (!rbUsed.Checked) return;
            settings.CountDirection = CountDirection.Used;
            UpdateAlertsLabels();
            SaveAndNotify();
        };
        dirRow.Controls.Add(rbRemaining);
        dirRow.Controls.Add(rbUsed);
        formatCard.Controls.Add(dirRow);

        var resetRow = CreateHorizontalRow();
        resetRow.Controls.Add(new Label { Text = "Reset times:", AutoSize = true, Margin = new Padding(0, S(5), S(12), 0) });
        var rbRelative = new RadioButton { Text = "Relative (in 3h 20m)", AutoSize = true, Checked = settings.ResetTimeStyle == ResetTimeStyle.Relative };
        var rbExact = new RadioButton { Text = "Exact (date + duration)", AutoSize = true, Checked = settings.ResetTimeStyle == ResetTimeStyle.Absolute, Margin = new Padding(S(12), S(3), 0, 0) };
        rbRelative.CheckedChanged += (_, _) => { if (rbRelative.Checked) { settings.ResetTimeStyle = ResetTimeStyle.Relative; SaveAndNotify(); } };
        rbExact.CheckedChanged += (_, _) => { if (rbExact.Checked) { settings.ResetTimeStyle = ResetTimeStyle.Absolute; SaveAndNotify(); } };
        resetRow.Controls.Add(rbRelative);
        resetRow.Controls.Add(rbExact);
        formatCard.Controls.Add(resetRow);

        // 2. Tray Hover Style
        var trayCard = CreateSectionCard(stack, "Tray Icon Hover Tooltip");
        var styleRow = CreateHorizontalRow();
        styleRow.Controls.Add(new Label { Text = "Hover display:", AutoSize = true, Margin = new Padding(0, S(5), S(12), 0) });
        var rbTwoLine = new RadioButton { Text = "5h & 7d Remaining (per service)", AutoSize = true, Checked = settings.TrayDisplayStyle == TrayDisplayStyle.TwoLineRemaining };
        var rbOriginal = new RadioButton { Text = "Single summary", AutoSize = true, Checked = settings.TrayDisplayStyle == TrayDisplayStyle.Original, Margin = new Padding(S(12), S(3), 0, 0) };

        var barCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(170), Enabled = settings.TrayDisplayStyle == TrayDisplayStyle.Original };
        barCombo.Items.AddRange(["Full (all services)", "Compact (most constrained)", "Title only"]);
        barCombo.SelectedIndex = settings.TrayBarStyle switch { TrayBarStyle.Compact => 1, TrayBarStyle.Icon => 2, _ => 0 };
        barCombo.SelectedIndexChanged += (_, _) =>
        {
            settings.TrayBarStyle = barCombo.SelectedIndex switch { 1 => TrayBarStyle.Compact, 2 => TrayBarStyle.Icon, _ => TrayBarStyle.Full };
            SaveAndNotify();
        };

        rbTwoLine.CheckedChanged += (_, _) =>
        {
            if (!rbTwoLine.Checked) return;
            settings.TrayDisplayStyle = TrayDisplayStyle.TwoLineRemaining;
            barCombo.Enabled = false;
            SaveAndNotify();
        };
        rbOriginal.CheckedChanged += (_, _) =>
        {
            if (!rbOriginal.Checked) return;
            settings.TrayDisplayStyle = TrayDisplayStyle.Original;
            barCombo.Enabled = true;
            SaveAndNotify();
        };
        styleRow.Controls.Add(rbTwoLine);
        styleRow.Controls.Add(rbOriginal);
        trayCard.Controls.Add(styleRow);

        var subBarRow = CreateHorizontalRow();
        subBarRow.Controls.Add(new Label { Text = "Summary mode:", AutoSize = true, Margin = new Padding(0, S(6), S(12), 0) });
        subBarRow.Controls.Add(barCombo);
        trayCard.Controls.Add(subBarRow);

        // 3. Remaining Quota Colors (5 bands × Light/Dark, identical to macOS)
        var colorCard = CreateSectionCard(stack, "Remaining Quota Colors");
        var appRow = CreateHorizontalRow();
        appRow.Controls.Add(new Label { Text = "Appearance:", AutoSize = true, Margin = new Padding(0, S(5), S(12), 0) });
        var rbDarkApp = new RadioButton { Text = "Dark appearance", AutoSize = true, Checked = true };
        var rbLightApp = new RadioButton { Text = "Light appearance", AutoSize = true, Margin = new Padding(S(12), S(3), 0, 0) };
        rbDarkApp.CheckedChanged += (_, _) => { if (rbDarkApp.Checked) { editingColorAppearance = ThemeAppearance.Dark; RefreshSwatches(); } };
        rbLightApp.CheckedChanged += (_, _) => { if (rbLightApp.Checked) { editingColorAppearance = ThemeAppearance.Light; RefreshSwatches(); } };
        appRow.Controls.Add(rbDarkApp);
        appRow.Controls.Add(rbLightApp);
        colorCard.Controls.Add(appRow);

        var swatchRow = CreateHorizontalRow();
        swatchRow.Margin = new Padding(0, S(8), 0, S(6));
        foreach (var band in RemainingQuotaPalette.AllBands)
        {
            var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(0, 0, S(14), 0) };
            var btn = new Button
            {
                Size = new Size(S(74), S(32)),
                FlatStyle = FlatStyle.Flat,
                Text = ""
            };
            var capturedBand = band;
            btn.Click += (_, _) =>
            {
                var currentRgb = RemainingQuotaPalette.GetRgb(settings.RemainingColorOverrides, capturedBand, editingColorAppearance);
                using var dlg = new ColorDialog { Color = WidgetForm.ColorFromRgb(currentRgb), FullOpen = true };
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    var rgb = (dlg.Color.R << 16) | (dlg.Color.G << 8) | dlg.Color.B;
                    settings.SetRemainingColor(capturedBand, editingColorAppearance, rgb);
                    RefreshSwatches();
                    SaveAndNotify();
                }
            };
            colorSwatchButtons[band] = btn;
            col.Controls.Add(btn);
            col.Controls.Add(new Label { Text = band.Label(), AutoSize = true });
            swatchRow.Controls.Add(col);
        }
        colorCard.Controls.Add(swatchRow);

        resetColorsButton = new Button
        {
            Text = "Reset Colors",
            AutoSize = true,
            Enabled = settings.RemainingColorOverrides.Count > 0
        };
        resetColorsButton.Click += (_, _) =>
        {
            settings.ResetRemainingColors();
            RefreshSwatches();
            SaveAndNotify();
        };
        colorCard.Controls.Add(resetColorsButton);
        RefreshSwatches();

        return page;
    }

    private void RefreshSwatches()
    {
        foreach (var (band, btn) in colorSwatchButtons)
        {
            var rgb = RemainingQuotaPalette.GetRgb(settings.RemainingColorOverrides, band, editingColorAppearance);
            btn.BackColor = WidgetForm.ColorFromRgb(rgb);
        }
        if (resetColorsButton is not null)
            resetColorsButton.Enabled = settings.RemainingColorOverrides.Count > 0;
    }
}
