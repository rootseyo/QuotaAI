using System.Diagnostics;
using QuotaAI.Core;

namespace QuotaAI.Tray;

// Settings window: Providers tab.
internal sealed partial class SettingsForm
{
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
}
