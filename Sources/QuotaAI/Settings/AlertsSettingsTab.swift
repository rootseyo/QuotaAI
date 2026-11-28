import SwiftUI

struct AlertsSettingsTab: View {
    @ObservedObject var store: UsageStore
    @ObservedObject var settings: SettingsStore

    private var countsDown: Bool { settings.countDirection == .remaining }

    var body: some View {
        Form {
            Section {
                Toggle(L("Alert when usage crosses threshold"), isOn: $settings.notificationsEnabled)
                    .onChange(of: settings.notificationsEnabled) { _, enabled in
                        if enabled {
                            store.notificationManager.requestAuthorizationIfNeeded()
                        }
                    }

                // Thresholds are stored as used-percent; only the labels adapt
                // to the count direction, so the alert fires at the same real
                // usage level in either display mode.
                Picker(
                    countsDown ? L("Alert when remaining falls below") : L("Alert when usage reaches"),
                    selection: $settings.notificationThreshold
                ) {
                    ForEach(SettingsStore.notificationThresholdOptions, id: \.self) { value in
                        Text(countsDown
                            ? L("\(Int(100 - value))% left")
                            : L("\(Int(value))%")
                        ).tag(value)
                    }
                }
                .pickerStyle(.segmented)
                .disabled(!settings.notificationsEnabled)
                .modifier(DisabledControlAccessibilityHint(
                    isDisabled: !settings.notificationsEnabled,
                    hint: L("Enable notifications to change this.")
                ))

                Picker(L("Balance alert below"), selection: $settings.balanceNotificationThreshold) {
                    ForEach(SettingsStore.balanceThresholdOptions, id: \.self) { value in
                        Text("$\(Int(value))").tag(value)
                    }
                }
                .pickerStyle(.segmented)
                .disabled(!settings.notificationsEnabled)
                .modifier(DisabledControlAccessibilityHint(
                    isDisabled: !settings.notificationsEnabled,
                    hint: L("Enable notifications to change this.")
                ))
            } footer: {
                Text(countsDown
                    ? L("One notification per limit window, re-armed when the window resets. \"20% left\" is the same alert as \"80% used\" — it follows your Display setting.")
                    : L("One notification per limit window, re-armed when the window resets. Balance alerts apply to pay-as-you-go providers.")
                )
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}

struct DisabledControlAccessibilityHint: ViewModifier {
    let isDisabled: Bool
    let hint: String

    func body(content: Content) -> some View {
        if isDisabled {
            content.accessibilityHint(hint)
        } else {
            content
        }
    }
}
