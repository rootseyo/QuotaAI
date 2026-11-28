import SwiftUI

struct CodexSubscriptionSettings: View {
    let providerID: String
    @ObservedObject var settings: SettingsStore

    private var isTracking: Bool {
        settings.renewal(for: providerID) != nil
    }

    var body: some View {
        DisclosureGroup(L("Subscription")) {
            Toggle(L("Track renewal date"), isOn: Binding(
                get: { isTracking },
                set: { enabled in
                    if enabled {
                        let today = Calendar.current.startOfDay(for: Date())
                        settings.setRenewal(
                            SubscriptionRenewal(
                                anchorDate: today,
                                platform: .chatgpt,
                                confirmedAt: nil,
                                remindDaysBefore: 3
                            ),
                            for: providerID
                        )
                    } else {
                        settings.removeRenewal(for: providerID)
                    }
                }
            ))

            if let renewal = settings.renewal(for: providerID) {
                subscriptionForm(renewal)
            }
        }
        .font(.caption)
    }

    @ViewBuilder
    private func subscriptionForm(_ renewal: SubscriptionRenewal) -> some View {
        DatePicker(
            L("Renews on"),
            selection: anchorDateBinding,
            displayedComponents: .date
        )
        Picker(L("Billed through"), selection: platformBinding) {
            ForEach(SubscriptionRenewal.Platform.allCases, id: \.self) { platform in
                Text(platform.displayName).tag(platform)
            }
        }
        Picker(L("Remind me"), selection: remindDaysBinding) {
            Text(L("Off")).tag(0)
            Text(L("1 day before")).tag(1)
            Text(L("3 days before")).tag(3)
            Text(L("7 days before")).tag(7)
        }
        Button(L("I confirmed this date today")) {
            guard var updated = settings.renewal(for: providerID) else { return }
            updated.confirmedAt = Date()
            settings.setRenewal(updated, for: providerID)
        }
        .help(L("Marks today as the date you verified this renewal on your billing platform."))

        if let confirmedAt = renewal.confirmedAt {
            Text(String(
                format: L("Last confirmed %@"),
                confirmedAt.formatted(date: .abbreviated, time: .omitted)
            ))
            .font(.caption2)
            .foregroundStyle(.secondary)
        } else {
            Text(L("Not confirmed yet — check the platform that bills you."))
                .font(.caption2)
                .foregroundStyle(.secondary)
        }
    }

    private var anchorDateBinding: Binding<Date> {
        Binding(
            get: { settings.renewal(for: providerID)?.anchorDate ?? Date() },
            set: { newValue in
                guard var renewal = settings.renewal(for: providerID) else { return }
                renewal.anchorDate = newValue
                settings.setRenewal(renewal, for: providerID)
            }
        )
    }

    private var platformBinding: Binding<SubscriptionRenewal.Platform> {
        Binding(
            get: { settings.renewal(for: providerID)?.platform ?? .chatgpt },
            set: { newValue in
                guard var renewal = settings.renewal(for: providerID) else { return }
                renewal.platform = newValue
                settings.setRenewal(renewal, for: providerID)
            }
        )
    }

    private var remindDaysBinding: Binding<Int> {
        Binding(
            get: { settings.renewal(for: providerID)?.remindDaysBefore ?? 0 },
            set: { newValue in
                guard var renewal = settings.renewal(for: providerID) else { return }
                renewal.remindDaysBefore = newValue
                settings.setRenewal(renewal, for: providerID)
            }
        )
    }
}
