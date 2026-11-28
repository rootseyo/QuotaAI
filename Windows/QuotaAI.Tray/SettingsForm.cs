using System.Diagnostics;
using QuotaAI.Core;

namespace QuotaAI.Tray;

internal sealed partial class SettingsForm : Form
{
    private readonly UsageCoordinator coordinator;
    private readonly AppSettings settings;
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private Panel providersScrollHost = null!;
    private bool providersLoaded;
    private ThemeAppearance editingColorAppearance = ThemeAppearance.Dark;
    private readonly Dictionary<QuotaColorBand, Button> colorSwatchButtons = new();
    private Button resetColorsButton = null!;
    private RadioButton[] alertThresholdRadios = [];
    private Label alertThresholdTitle = null!;
    private Label alertFooterLabel = null!;
    private CheckBox widgetPinToggle = null!;

    private readonly UpdateController updates;

    public SettingsForm(Icon icon, UsageCoordinator usageCoordinator, Action<bool> setWidgetPinned, UpdateController updateController)
    {
        updates = updateController;
        coordinator = usageCoordinator;
        settings = coordinator.Settings;

        Text = "QuotaAI Settings";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        Font = SystemFonts.MessageBoxFont ?? DefaultFont;
        MinimumSize = new Size(S(560), S(520));
        ClientSize = new Size(S(620), S(600));

        tabs.TabPages.Add(BuildGeneralTab());
        var providersPage = BuildProvidersTab();
        tabs.TabPages.Add(providersPage);
        tabs.TabPages.Add(BuildDisplayTab(setWidgetPinned));
        tabs.TabPages.Add(BuildAlertsTab());
        DisableMnemonicsRecursive(tabs);
        Controls.Add(tabs);
        tabs.Selected += (_, e) =>
        {
            if (e.TabPage == providersPage && !providersLoaded)
            {
                providersLoaded = true;
                RebuildProvidersList();
            }
        };
    }

    private static void DisableMnemonicsRecursive(Control root)
    {
        switch (root)
        {
            case Label lbl: lbl.UseMnemonic = false; break;
            case ButtonBase btn: btn.UseMnemonic = false; break;
        }
        foreach (Control child in root.Controls)
            DisableMnemonicsRecursive(child);
    }

    public void SelectProvidersTab() => tabs.SelectedIndex = 1;
    public void SyncWidgetPin(bool pinned) => widgetPinToggle.Checked = pinned;

    private void ShowAboutDialog()
    {
        MessageBox.Show(
            this,
            "QuotaAI for Windows\n\n" +
            "AI coding usage & quota tracker for Codex, Claude, Antigravity, Cursor, and API providers.\n\n" +
            "• Credentials never leave your machine except to each provider's own API.\n" +
            "• No analytics, no telemetry.\n" +
            "• Open source under the MIT License (https://github.com/rootseyo/QuotaAI).",
            "About QuotaAI",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void SaveAndNotify()
    {
        settings.Save();
        settings.NotifyChanged();
    }

    private FlowLayoutPanel CreateVerticalStack(Control parent)
    {
        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true
        };
        parent.Controls.Add(stack);
        return stack;
    }

    private FlowLayoutPanel CreateSectionCard(
        FlowLayoutPanel parent,
        string heading,
        string? providerId = null,
        string? shortCode = null)
    {
        var box = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            MinimumSize = new Size(S(540), 0),
            MaximumSize = new Size(S(560), 0),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = SystemColors.ControlLightLight,
            Padding = new Padding(S(12), S(10), S(12), S(10)),
            Margin = new Padding(0, 0, 0, S(12))
        };

        if (providerId is not null && shortCode is not null)
        {
            var headerRow = CreateHorizontalRow();
            headerRow.Margin = new Padding(0, 0, 0, S(6));
            var iconPx = S(22);
            using var bFont = new Font(Font.FontFamily, S(9), FontStyle.Bold, GraphicsUnit.Pixel);
            var pic = new PictureBox
            {
                Size = new Size(iconPx, iconPx),
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = ProviderIcons.CreateBadgeImage(providerId, shortCode, iconPx, bFont),
                Margin = new Padding(0, 0, S(7), 0)
            };
            var lbl = new Label
            {
                Text = heading,
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, S(2), 0, 0)
            };
            headerRow.Controls.Add(pic);
            headerRow.Controls.Add(lbl);
            box.Controls.Add(headerRow);
        }
        else
        {
            box.Controls.Add(new Label
            {
                Text = heading,
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, S(6))
            });
        }

        parent.Controls.Add(box);
        return box;
    }

    private static FlowLayoutPanel CreateHorizontalRow() => new()
    {
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = true,
        AutoSize = true,
        Margin = Padding.Empty
    };

    private Label CreateCaptionLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        MaximumSize = new Size(S(520), 0),
        Margin = new Padding(0, S(2), 0, S(2))
    };

    private static LinkLabel CreateWebLink(string text, string url)
    {
        var link = new LinkLabel { Text = text, AutoSize = true };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        };
        return link;
    }

    private int S(int logicalPixels) => LogicalToDeviceUnits(logicalPixels);
}
