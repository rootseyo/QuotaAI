using System.Diagnostics;
using QuotaAI.Core;

namespace QuotaAI.Tray;

internal sealed class SettingsForm : Form
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

    public SettingsForm(Icon icon, UsageCoordinator usageCoordinator, Action<bool> setWidgetPinned)
    {
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
        diagCard.Controls.Add(diagRow);
        diagCard.Controls.Add(CreateCaptionLabel("Copies redacted troubleshooting info to the clipboard — never includes keys or tokens."));

        return page;
    }

    private TabPage BuildProvidersTab()
    {
        var page = new TabPage("Providers") { BackColor = SystemColors.Window };
        providersScrollHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(S(14)) };
        page.Controls.Add(providersScrollHost);
        return page;
    }

    private void RebuildProvidersList()
    {
        providersScrollHost.SuspendLayout();
        while (providersScrollHost.Controls.Count > 0)
        {
            var ctrl = providersScrollHost.Controls[0];
            providersScrollHost.Controls.RemoveAt(0);
            ctrl.Dispose();
        }

        var stack = CreateVerticalStack(providersScrollHost);
        stack.SuspendLayout();
        stack.Controls.Add(CreateCaptionLabel(
            "Choose which AI services appear in the desktop widget and tray hover tooltip (5h / 7d remaining). Credentials stay in each CLI's store or Windows Credential Manager."));

        foreach (var provider in coordinator.Providers)
            BuildProviderCard(stack, provider);

        DisableMnemonicsRecursive(providersScrollHost);
        stack.ResumeLayout(true);
        providersScrollHost.ResumeLayout(true);
    }

    private void BuildProviderCard(FlowLayoutPanel parent, IUsageProvider provider)
    {
        var card = CreateSectionCard(parent, $"{provider.DisplayName}  ({provider.ShortCode})", provider.Id, provider.ShortCode);

        // Header links + Mode row
        var modeRow = CreateHorizontalRow();
        modeRow.Controls.Add(new Label { Text = "Mode:", AutoSize = true, Margin = new Padding(0, S(5), S(8), 0) });

        var currentMode = settings.GetMode(provider.Id);
        var showInTrayCheck = new CheckBox
        {
            Text = "Show in widget & tray (5h / 7d)",
            AutoSize = true,
            Checked = settings.ShowsInTray(provider.Id),
            Enabled = currentMode != ProviderMode.Off,
            Margin = new Padding(S(16), S(4), 0, 0)
        };
        showInTrayCheck.CheckedChanged += (_, _) =>
        {
            settings.SetShowsInTray(provider.Id, showInTrayCheck.Checked);
            SaveAndNotify();
        };

        foreach (var mode in new[] { ProviderMode.Auto, ProviderMode.On, ProviderMode.Off })
        {
            var rb = new RadioButton
            {
                Text = mode.Label(),
                AutoSize = true,
                Checked = currentMode == mode,
                Margin = new Padding(0, S(3), S(10), 0)
            };
            rb.CheckedChanged += (_, _) =>
            {
                if (!rb.Checked)
                    return;
                settings.SetMode(provider.Id, mode);
                showInTrayCheck.Enabled = mode != ProviderMode.Off;
                SaveAndNotify();
                _ = coordinator.RefreshAsync(forceRefresh: false);
            };
            modeRow.Controls.Add(rb);
        }
        modeRow.Controls.Add(showInTrayCheck);

        if (!string.IsNullOrEmpty(provider.DashboardUrl))
        {
            var dashLink = CreateWebLink("Dashboard ↗", provider.DashboardUrl!);
            dashLink.Margin = new Padding(S(12), S(5), 0, 0);
            modeRow.Controls.Add(dashLink);
        }

        card.Controls.Add(modeRow);

        // Auth / Credential section
        if (provider.AuthKind == ProviderAuthKind.LocalCredentials)
        {
            var status = CreateCaptionLabel("Status: Checking…");
            var help = CreateCaptionLabel("");
            help.Visible = false;
            card.Controls.Add(status);
            card.Controls.Add(help);
            _ = PopulateLocalCredentialStatusAsync(provider, status, help);
        }
        else
        {
            BuildApiKeyControls(card, provider);
        }

        // Codex multi-account & subscription renewal tracking
        if (provider is CodexProvider codexProv)
        {
            BuildCodexSubscriptionSection(card, codexProv.Id);
            if (codexProv.IsPrimary)
                BuildCodexMultiAccountSection(card);
        }
    }

    private async Task PopulateLocalCredentialStatusAsync(IUsageProvider provider, Label status, Label help)
    {
        try
        {
            var (detected, helpText) = await Task.Run(() => (provider.IsDetected, provider.CredentialHelpText));
            if (status.IsDisposed)
                return;

            var detectedText = detected ? "Detected" : "Not detected";
            if (provider is CodexProvider cp && !string.IsNullOrEmpty(cp.LastKnownEmail))
            {
                var planSuffix = !string.IsNullOrEmpty(cp.LastKnownPlan) ? $" · {cp.LastKnownPlan}" : "";
                detectedText = $"{cp.LastKnownEmail}{planSuffix}";
            }
            else if (coordinator.GetState(provider.Id).ErrorMessage is { } err
                && settings.GetMode(provider.Id) != ProviderMode.Off)
            {
                detectedText = $"{detectedText} — {err}";
            }

            status.Text = $"Status: {detectedText}";
            help.Text = helpText ?? "";
            help.Visible = !string.IsNullOrEmpty(helpText);
        }
        catch
        {
            if (!status.IsDisposed)
                status.Text = "Status: Could not check local credentials.";
        }
    }

    private void BuildApiKeyControls(FlowLayoutPanel card, IUsageProvider provider)
    {
        if (!string.IsNullOrEmpty(provider.CredentialHelpText))
            card.Controls.Add(CreateCaptionLabel(provider.CredentialHelpText!));

        if (provider.Id == "claude-api")
            card.Controls.Add(CreateWebLink("View prepaid credits in Console ↗", "https://platform.claude.com/settings/billing"));

        var host = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(0, S(4), 0, 0)
        };
        card.Controls.Add(host);

        async Task RenderKeyStateAsync()
        {
            host.Enabled = false;
            if (host.Controls.Count == 0)
                host.Controls.Add(CreateCaptionLabel("Checking saved key…"));

            bool exists;
            try
            {
                exists = await Task.Run(() => WindowsCredentialStore.Exists(provider.KeychainAccount));
            }
            catch
            {
                if (!host.IsDisposed)
                    host.Controls.Add(CreateCaptionLabel("Could not check saved key."));
                return;
            }
            if (host.IsDisposed)
                return;

            host.SuspendLayout();
            while (host.Controls.Count > 0)
            {
                var c = host.Controls[0];
                host.Controls.RemoveAt(0);
                c.Dispose();
            }

            if (exists)
            {
                var row = CreateHorizontalRow();
                row.Controls.Add(new Label
                {
                    Text = $"{provider.ApiKeyPlaceholder}: Saved in Windows Credential Manager",
                    AutoSize = true,
                    Margin = new Padding(0, S(6), S(10), 0)
                });
                var removeBtn = new Button { Text = "Remove", AutoSize = true };
                removeBtn.Click += (_, _) =>
                {
                    _ = WindowsCredentialStore.DeleteApiKey(provider.KeychainAccount);
                    coordinator.CredentialDidChange(provider.Id);
                    _ = RenderKeyStateAsync();
                };
                row.Controls.Add(removeBtn);
                host.Controls.Add(row);

                var assessLabel = CreateCaptionLabel("Checking key…");
                host.Controls.Add(assessLabel);
                _ = PopulateAssessmentAsync(provider, assessLabel, host);
            }
            else
            {
                var row = CreateHorizontalRow();
                var keyBox = new TextBox
                {
                    UseSystemPasswordChar = true,
                    PlaceholderText = provider.ApiKeyPlaceholder,
                    Width = S(280),
                    Margin = new Padding(0, S(3), S(8), S(3))
                };
                var saveBtn = new Button { Text = "Save", AutoSize = true, Enabled = false };
                keyBox.TextChanged += (_, _) => saveBtn.Enabled = !string.IsNullOrWhiteSpace(keyBox.Text);
                saveBtn.Click += (_, _) =>
                {
                    var secret = keyBox.Text.Trim();
                    if (string.IsNullOrEmpty(secret))
                        return;
                    if (WindowsCredentialStore.SetApiKey(provider.KeychainAccount, secret))
                    {
                        keyBox.Text = "";
                        coordinator.CredentialDidChange(provider.Id);
                        _ = RenderKeyStateAsync();
                    }
                };
                row.Controls.Add(keyBox);
                row.Controls.Add(saveBtn);
                if (!string.IsNullOrEmpty(provider.KeyPortalUrl))
                {
                    var getKey = CreateWebLink("Get key ↗", provider.KeyPortalUrl!);
                    getKey.Margin = new Padding(S(8), S(6), 0, 0);
                    row.Controls.Add(getKey);
                }
                host.Controls.Add(row);
            }
            DisableMnemonicsRecursive(host);
            host.Enabled = true;
            host.ResumeLayout(true);
        }

        _ = RenderKeyStateAsync();
    }

    private async Task PopulateAssessmentAsync(IUsageProvider provider, Label target, FlowLayoutPanel host)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var assessment = await Task.Run(() => provider.AssessCredentialAsync(cts.Token), cts.Token);
            if (target.IsDisposed)
                return;
            if (assessment is null)
            {
                target.Visible = false;
                return;
            }

            target.Text = $"[{assessment.KeyTypeLabel}] {assessment.Summary} {assessment.Detail}";
            if (!string.IsNullOrEmpty(assessment.ManageUrl) && !host.IsDisposed)
                host.Controls.Add(CreateWebLink("Manage keys ↗", assessment.ManageUrl!));
        }
        catch
        {
            if (!target.IsDisposed)
                target.Text = "";
        }
    }

    private void BuildCodexMultiAccountSection(FlowLayoutPanel card)
    {
        var section = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(0, S(8), 0, 0)
        };
        section.Controls.Add(new Label
        {
            Text = "Multiple Codex Accounts",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, S(2), 0, S(4))
        });

        foreach (var extra in settings.CodexExtraAccounts.ToList())
        {
            var row = CreateHorizontalRow();
            row.Controls.Add(new Label
            {
                Text = $"{extra.Label}  ({extra.CodexHomePath})",
                AutoSize = true,
                Margin = new Padding(0, S(5), S(10), 0)
            });
            var rmBtn = new Button { Text = "Remove", AutoSize = true };
            var extraId = extra.Id;
            rmBtn.Click += (_, _) =>
            {
                settings.RemoveCodexAccount(extraId);
                SaveAndNotify();
                coordinator.RebuildProviders();
                RebuildProvidersList();
            };
            row.Controls.Add(rmBtn);
            section.Controls.Add(row);
        }

        var discoveredAccounts = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Visible = false,
            Margin = Padding.Empty
        };
        section.Controls.Add(discoveredAccounts);
        _ = PopulateDiscoveredCodexAccountsAsync(discoveredAccounts, settings.CodexExtraAccounts.ToArray());

        // Inline Add Account controls
        var addPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Visible = false,
            Margin = new Padding(0, S(4), 0, S(4))
        };
        var labelInput = new TextBox { PlaceholderText = "Label (e.g. Work)", Width = S(200) };
        var pathInput = new TextBox { Text = CodexAccountConfig.SuggestedHomePath(""), Width = S(280) };
        var cmdBox = new TextBox
        {
            ReadOnly = true,
            Text = CodexAccountConfig.LoginCommand(pathInput.Text),
            Width = S(460),
            Margin = new Padding(0, S(4), 0, S(4))
        };

        labelInput.TextChanged += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(pathInput.Text) || pathInput.Text.StartsWith(@"~\.codex-", StringComparison.OrdinalIgnoreCase))
                pathInput.Text = CodexAccountConfig.SuggestedHomePath(labelInput.Text);
        };
        pathInput.TextChanged += (_, _) => cmdBox.Text = CodexAccountConfig.LoginCommand(pathInput.Text);

        var inputRow = CreateHorizontalRow();
        inputRow.Controls.Add(labelInput);
        inputRow.Controls.Add(pathInput);
        addPanel.Controls.Add(inputRow);
        addPanel.Controls.Add(cmdBox);

        var actionRow = CreateHorizontalRow();
        var copyCmdBtn = new Button { Text = "Copy login command", AutoSize = true };
        copyCmdBtn.Click += (_, _) => Clipboard.SetText(cmdBox.Text);
        var saveAcctBtn = new Button { Text = "Save account", AutoSize = true, Margin = new Padding(S(6), S(3), 0, S(3)) };
        var cancelAcctBtn = new Button { Text = "Cancel", AutoSize = true, Margin = new Padding(S(6), S(3), 0, S(3)) };

        var openAddBtn = new Button { Text = "Add Codex account…", AutoSize = true, Margin = new Padding(0, S(4), 0, 0) };
        openAddBtn.Click += (_, _) =>
        {
            openAddBtn.Visible = false;
            addPanel.Visible = true;
        };
        cancelAcctBtn.Click += (_, _) =>
        {
            addPanel.Visible = false;
            openAddBtn.Visible = true;
        };
        saveAcctBtn.Click += (_, _) =>
        {
            var lbl = labelInput.Text.Trim();
            var hm = pathInput.Text.Trim();
            if (string.IsNullOrEmpty(lbl) || string.IsNullOrEmpty(hm) || hm.Equals(@"~\.codex", StringComparison.OrdinalIgnoreCase))
                return;
            settings.AddCodexAccount(CodexAccountConfig.Create(lbl, hm));
            SaveAndNotify();
            coordinator.RebuildProviders();
            RebuildProvidersList();
        };

        actionRow.Controls.Add(copyCmdBtn);
        actionRow.Controls.Add(saveAcctBtn);
        actionRow.Controls.Add(cancelAcctBtn);
        addPanel.Controls.Add(actionRow);
        addPanel.Controls.Add(CreateCaptionLabel("Run the command in PowerShell to sign in that account. QuotaAI never handles your login or tokens."));

        section.Controls.Add(openAddBtn);
        section.Controls.Add(addPanel);
        card.Controls.Add(section);
    }

    private async Task PopulateDiscoveredCodexAccountsAsync(
        FlowLayoutPanel host, CodexAccountConfig[] configuredAccounts)
    {
        var candidates = await Task.Run(() => CodexAccountDiscovery.Discover(configuredAccounts));
        if (host.IsDisposed || candidates.Count == 0)
            return;

        host.SuspendLayout();
        foreach (var candidate in candidates)
        {
            var row = CreateHorizontalRow();
            row.Controls.Add(new Label
            {
                Text = $"Found signed-in Codex home: {candidate.SuggestedLabel} ({candidate.HomePath})",
                AutoSize = true,
                Margin = new Padding(0, S(5), S(8), 0)
            });
            var addButton = new Button { Text = "Add", AutoSize = true };
            addButton.Click += (_, _) =>
            {
                settings.AddCodexAccount(CodexAccountConfig.Create(candidate.SuggestedLabel, candidate.HomePath));
                SaveAndNotify();
                coordinator.RebuildProviders();
                RebuildProvidersList();
            };
            row.Controls.Add(addButton);
            host.Controls.Add(row);
        }
        DisableMnemonicsRecursive(host);
        host.Visible = true;
        host.ResumeLayout(true);
    }

    private void BuildCodexSubscriptionSection(FlowLayoutPanel card, string providerId)
    {
        var wrap = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(0, S(6), 0, 0)
        };

        var detailsPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            MaximumSize = new Size(S(500), 0),
            Visible = settings.GetRenewal(providerId) is not null
        };

        var trackToggle = new CheckBox
        {
            Text = "Track subscription renewal date",
            AutoSize = true,
            Checked = settings.GetRenewal(providerId) is not null
        };

        void PopulateDetails()
        {
            while (detailsPanel.Controls.Count > 0)
            {
                var c = detailsPanel.Controls[0];
                detailsPanel.Controls.RemoveAt(0);
                c.Dispose();
            }
            if (settings.GetRenewal(providerId) is not { } cur)
                return;

            var picker = new DateTimePicker
            {
                Format = DateTimePickerFormat.Short,
                Value = cur.AnchorDate.LocalDateTime.Date,
                Width = S(115),
                Margin = new Padding(0, S(4), S(8), S(4))
            };
            var platformCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(135), Margin = new Padding(0, S(4), S(8), S(4)) };
            var platforms = Enum.GetValues<RenewalPlatform>();
            foreach (var pl in platforms)
                platformCombo.Items.Add(pl.DisplayName());
            platformCombo.SelectedIndex = Array.IndexOf(platforms, cur.Platform);

            var remindCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(120), Margin = new Padding(0, S(4), S(8), S(4)) };
            remindCombo.Items.AddRange(["Remind: Off", "1 day before", "3 days before", "7 days before"]);
            remindCombo.SelectedIndex = cur.RemindDaysBefore switch { 1 => 1, 3 => 2, 7 => 3, _ => 0 };

            void SaveRenewal(DateTimeOffset? confirmedOverride = null)
            {
                var rDays = remindCombo.SelectedIndex switch { 1 => 1, 2 => 3, 3 => 7, _ => 0 };
                var pl = platforms[Math.Clamp(platformCombo.SelectedIndex, 0, platforms.Length - 1)];
                var existing = settings.GetRenewal(providerId);
                settings.SetRenewal(providerId, new SubscriptionRenewal(
                    new DateTimeOffset(picker.Value.Date),
                    pl,
                    confirmedOverride ?? existing?.ConfirmedAt,
                    rDays));
                SaveAndNotify();
            }

            picker.ValueChanged += (_, _) => SaveRenewal();
            platformCombo.SelectedIndexChanged += (_, _) => SaveRenewal();
            remindCombo.SelectedIndexChanged += (_, _) => SaveRenewal();

            var confirmLabel = CreateCaptionLabel(cur.ConfirmedAt is { } ca
                ? $"Confirmed {ca.ToLocalTime():d}"
                : "Not confirmed yet");
            confirmLabel.Margin = new Padding(S(6), S(8), 0, 0);

            var confirmBtn = new Button { Text = "Confirmed today", AutoSize = true, Margin = new Padding(0, S(3), S(6), S(3)) };
            confirmBtn.Click += (_, _) =>
            {
                var now = DateTimeOffset.Now;
                SaveRenewal(now);
                confirmLabel.Text = $"Confirmed {now:d}";
            };

            detailsPanel.Controls.Add(picker);
            detailsPanel.Controls.Add(platformCombo);
            detailsPanel.Controls.Add(remindCombo);
            detailsPanel.Controls.Add(confirmBtn);
            detailsPanel.Controls.Add(confirmLabel);
        }

        trackToggle.CheckedChanged += (_, _) =>
        {
            if (trackToggle.Checked)
            {
                settings.SetRenewal(providerId, new SubscriptionRenewal(DateTimeOffset.Now.Date, RenewalPlatform.ChatGPT, null, 3));
                detailsPanel.Visible = true;
                PopulateDetails();
            }
            else
            {
                settings.RemoveRenewal(providerId);
                detailsPanel.Visible = false;
            }
            SaveAndNotify();
        };

        if (detailsPanel.Visible)
            PopulateDetails();

        wrap.Controls.Add(trackToggle);
        wrap.Controls.Add(detailsPanel);
        card.Controls.Add(wrap);
    }

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

    private TabPage BuildAlertsTab()
    {
        var page = new TabPage("Alerts") { Padding = new Padding(S(16)), AutoScroll = true, BackColor = SystemColors.Window };
        var stack = CreateVerticalStack(page);
        var card = CreateSectionCard(stack, "Usage & Balance Notifications");

        var enableToggle = new CheckBox
        {
            Text = "Alert when usage crosses threshold",
            AutoSize = true,
            Checked = settings.NotificationsEnabled,
            Margin = new Padding(0, S(4), 0, S(8))
        };
        card.Controls.Add(enableToggle);

        alertThresholdTitle = new Label { AutoSize = true, Margin = new Padding(0, S(4), 0, S(4)) };
        card.Controls.Add(alertThresholdTitle);

        var threshRow = CreateHorizontalRow();
        var list = new List<RadioButton>();
        foreach (var val in AppSettings.NotificationThresholdOptions)
        {
            var rb = new RadioButton
            {
                AutoSize = true,
                Tag = val,
                Checked = Math.Abs(settings.NotificationThreshold - val) < 0.1,
                Enabled = settings.NotificationsEnabled,
                Margin = new Padding(0, S(2), S(14), S(2))
            };
            var captured = val;
            rb.CheckedChanged += (_, _) =>
            {
                if (!rb.Checked) return;
                settings.NotificationThreshold = captured;
                SaveAndNotify();
            };
            list.Add(rb);
            threshRow.Controls.Add(rb);
        }
        alertThresholdRadios = list.ToArray();
        card.Controls.Add(threshRow);

        var balTitle = new Label { Text = "Balance alert below:", AutoSize = true, Margin = new Padding(0, S(10), 0, S(4)) };
        card.Controls.Add(balTitle);
        var balRow = CreateHorizontalRow();
        var balRadios = new List<RadioButton>();
        foreach (var val in AppSettings.BalanceThresholdOptions)
        {
            var rb = new RadioButton
            {
                Text = $"${(int)val}",
                AutoSize = true,
                Checked = Math.Abs(settings.BalanceNotificationThreshold - val) < 0.1,
                Enabled = settings.NotificationsEnabled,
                Margin = new Padding(0, S(2), S(14), S(2))
            };
            var captured = val;
            rb.CheckedChanged += (_, _) =>
            {
                if (!rb.Checked) return;
                settings.BalanceNotificationThreshold = captured;
                SaveAndNotify();
            };
            balRadios.Add(rb);
            balRow.Controls.Add(rb);
        }
        card.Controls.Add(balRow);

        alertFooterLabel = CreateCaptionLabel("");
        alertFooterLabel.Margin = new Padding(0, S(10), 0, 0);
        card.Controls.Add(alertFooterLabel);

        enableToggle.CheckedChanged += (_, _) =>
        {
            settings.NotificationsEnabled = enableToggle.Checked;
            foreach (var r in alertThresholdRadios) r.Enabled = enableToggle.Checked;
            foreach (var r in balRadios) r.Enabled = enableToggle.Checked;
            SaveAndNotify();
        };

        UpdateAlertsLabels();
        return page;
    }

    private void UpdateAlertsLabels()
    {
        if (alertThresholdTitle is null)
            return;

        var countsDown = settings.CountDirection == CountDirection.Remaining;
        alertThresholdTitle.Text = countsDown ? "Alert when remaining falls below:" : "Alert when usage reaches:";
        foreach (var rb in alertThresholdRadios)
        {
            if (rb.Tag is double val)
                rb.Text = countsDown ? $"{(int)(100 - val)}% left" : $"{(int)val}% used";
        }
        if (alertFooterLabel is not null)
        {
            alertFooterLabel.Text = countsDown
                ? "One notification per limit window, re-armed when the window resets. \"20% left\" is the same alert as \"80% used\"."
                : "One notification per limit window, re-armed when the window resets. Balance alerts apply to pay-as-you-go providers.";
        }
    }

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
