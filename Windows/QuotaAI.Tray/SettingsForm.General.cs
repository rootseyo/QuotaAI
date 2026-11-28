using System.Diagnostics;
using QuotaAI.Core;

namespace QuotaAI.Tray;

// Settings window: General tab.
internal sealed partial class SettingsForm
{
    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("General") { Padding = new Padding(S(16)), AutoScroll = true, BackColor = SystemColors.Window };
        var stack = CreateVerticalStack(page);

        // 1. Startup
        var startupCard = CreateSectionCard(stack, "Startup");
        var launchToggle = new CheckBox
        {
            Text = "Launch QuotaAI at Windows sign-in",
            AutoSize = true,
            Checked = StartupRegistration.IsEnabled(),
            Margin = new Padding(0, S(4), 0, S(4))
        };
        launchToggle.CheckedChanged += (_, _) =>
        {
            if (!StartupRegistration.SetEnabled(launchToggle.Checked))
                launchToggle.Checked = StartupRegistration.IsEnabled();
        };
        startupCard.Controls.Add(launchToggle);

        var updatesCard = CreateSectionCard(stack, "Updates");
        var autoUpdateToggle = new CheckBox
        {
            Text = "Check for updates automatically (once a day, via GitHub)",
            AutoSize = true,
            Checked = settings.AutoCheckForUpdates,
            Margin = new Padding(0, S(4), 0, S(4))
        };
        autoUpdateToggle.CheckedChanged += (_, _) =>
        {
            settings.AutoCheckForUpdates = autoUpdateToggle.Checked;
            SaveAndNotify();
        };
        var updateRow = CreateHorizontalRow();
        var checkUpdatesButton = new Button
        {
            Text = "Check for updates…",
            AutoSize = true,
            Margin = new Padding(0, S(2), S(10), S(2))
        };
        checkUpdatesButton.Click += async (_, _) =>
        {
            checkUpdatesButton.Enabled = false;
            try { await updates.CheckInteractiveAsync(); }
            finally { checkUpdatesButton.Enabled = true; }
        };
        updateRow.Controls.Add(checkUpdatesButton);
        updateRow.Controls.Add(new Label
        {
            Text = $"Current version: {updates.VersionText}",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, S(8), 0, 0)
        });
        updatesCard.Controls.Add(autoUpdateToggle);
        updatesCard.Controls.Add(updateRow);
        updatesCard.Controls.Add(CreateCaptionLabel(
            "Downloads QuotaAI.exe from this project's GitHub Releases, verifies its SHA-256, then restarts. Nothing about you is sent."));

        // 2. Agent & CLI Snapshot
        var agentCard = CreateSectionCard(stack, "Agent & CLI Access");
        var snapshotPathLabel = new Label
        {
            Text = $"Snapshot path: {StatusSnapshotWriter.SnapshotPath()}",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(S(520), 0),
            Margin = new Padding(0, S(4), 0, S(4))
        };
        var agentToggle = new CheckBox
        {
            Text = "Enable agent & CLI access (write status.json)",
            AutoSize = true,
            Checked = settings.AgentAccessEnabled,
            Margin = new Padding(0, S(4), 0, S(4))
        };
        agentToggle.CheckedChanged += (_, _) =>
        {
            settings.AgentAccessEnabled = agentToggle.Checked;
            SaveAndNotify();
            StatusSnapshotWriter.WriteOrDelete(settings, coordinator.Providers, coordinator.States);
        };
        var agentHint = CreateCaptionLabel(
            "Writes a machine-readable usage snapshot (never credentials or tokens) to LocalAppData after each refresh.");
        agentCard.Controls.Add(agentToggle);
        agentCard.Controls.Add(snapshotPathLabel);
        agentCard.Controls.Add(agentHint);

        // 3. Refresh Interval
        var refreshCard = CreateSectionCard(stack, "Refresh Cadence");
        var refreshRow = CreateHorizontalRow();
        refreshRow.Controls.Add(new Label
        {
            Text = "Refresh interval:",
            AutoSize = true,
            Margin = new Padding(0, S(6), S(10), 0)
        });
        var refreshCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = S(160)
        };
        refreshCombo.Items.AddRange(["30 seconds", "1 minute", "5 minutes"]);
        refreshCombo.SelectedIndex = settings.RefreshIntervalSeconds switch
        {
            30 => 0,
            300 => 2,
            _ => 1
        };
        refreshCombo.SelectedIndexChanged += (_, _) =>
        {
            settings.RefreshIntervalSeconds = refreshCombo.SelectedIndex switch
            {
                0 => 30,
                2 => 300,
                _ => 60
            };
            SaveAndNotify();
            coordinator.RescheduleTimer();
        };
        refreshRow.Controls.Add(refreshCombo);
        refreshCard.Controls.Add(refreshRow);

        // 4. Diagnostics & About
        var diagCard = CreateSectionCard(stack, "Troubleshooting & About");
        var diagRow = CreateHorizontalRow();
        var copyDiagBtn = new Button { Text = "Copy Diagnostics", AutoSize = true, Padding = new Padding(S(8), S(2), S(8), S(2)) };
        copyDiagBtn.Click += async (_, _) =>
        {
            Clipboard.SetText(DiagnosticsReport.Build(settings, coordinator.Providers, coordinator.States));
            copyDiagBtn.Text = "Copied ✓";
            await Task.Delay(1800);
            if (!copyDiagBtn.IsDisposed)
                copyDiagBtn.Text = "Copy Diagnostics";
        };
        var aboutBtn = new Button
        {
            Text = "About QuotaAI…",
            AutoSize = true,
            Padding = new Padding(S(8), S(2), S(8), S(2)),
            Margin = new Padding(S(8), S(3), 0, S(3))
        };
        aboutBtn.Click += (_, _) => ShowAboutDialog();
        diagRow.Controls.Add(copyDiagBtn);
        diagRow.Controls.Add(aboutBtn);
        var donateLink = CreateWebLink("Donate ↗", "https://buymeacoffee.com/seyo");
        donateLink.Margin = new Padding(S(12), S(8), 0, 0);
        diagRow.Controls.Add(donateLink);
        diagCard.Controls.Add(diagRow);
        diagCard.Controls.Add(CreateCaptionLabel("Copies redacted troubleshooting info to the clipboard — never includes keys or tokens."));

        return page;
    }
}
