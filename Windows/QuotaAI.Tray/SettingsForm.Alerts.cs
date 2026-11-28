using System.Diagnostics;
using QuotaAI.Core;

namespace QuotaAI.Tray;

// Settings window: Alerts tab.
internal sealed partial class SettingsForm
{
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
}
