import SwiftUI

struct DisplaySettingsTab: View {
    @ObservedObject var settings: SettingsStore
    @Environment(\.colorScheme) private var colorScheme
    @State private var editingAppearance: RemainingQuotaColor.Appearance?

    private var paletteAppearance: RemainingQuotaColor.Appearance {
        editingAppearance ?? (colorScheme == .dark ? .dark : .light)
    }

    var body: some View {
        Form {
            Section {
                Picker(L("Count direction"), selection: $settings.countDirection) {
                    Text(L("% used")).tag(CountDirection.used)
                    Text(L("% left")).tag(CountDirection.remaining)
                }
                .pickerStyle(.segmented)

                Picker(L("Reset times"), selection: $settings.resetTimeStyle) {
                    Text(L("Relative")).tag(ResetTimeStyle.relative)
                    Text(L("Exact")).tag(ResetTimeStyle.absolute)
                }
                .pickerStyle(.segmented)

                Picker(L("Menu Bar Display Style"), selection: $settings.menuBarDisplayStyle) {
                    Text(L("Original")).tag(MenuBarDisplayStyle.original)
                    Text(L("Two-Line Remaining")).tag(MenuBarDisplayStyle.twoLineRemaining)
                }
                .pickerStyle(.segmented)

                Picker(L("Menu bar style"), selection: $settings.menuBarStyle) {
                    Text(L("Full")).tag(MenuBarStyle.full)
                    Text(L("Compact (worst only)")).tag(MenuBarStyle.compact)
                    Text(L("Icon only")).tag(MenuBarStyle.icon)
                }
                .pickerStyle(.segmented)
                .disabled(settings.menuBarDisplayStyle != .original)
            }
            Section(L("Remaining Quota Colors")) {
                Picker(L("Appearance"), selection: Binding(
                    get: { paletteAppearance },
                    set: { editingAppearance = $0 }
                )) {
                    ForEach(RemainingQuotaColor.Appearance.allCases, id: \.self) { appearance in
                        Text(appearance.label).tag(appearance)
                    }
                }
                .pickerStyle(.segmented)

                HStack(spacing: 8) {
                    ForEach(RemainingQuotaColor.Band.allCases, id: \.self) { band in
                        VStack(spacing: 4) {
                            ColorPicker(band.label, selection: colorBinding(for: band), supportsOpacity: false)
                                .labelsHidden()
                            Text(band.label)
                                .font(.caption)
                                .accessibilityHidden(true)
                        }
                        .frame(maxWidth: .infinity)
                    }
                }

                Button(L("Reset Colors")) {
                    settings.remainingQuotaColors = RemainingQuotaColor()
                }
                .disabled(settings.remainingQuotaColors.overrides.isEmpty)
            }
        }
        .formStyle(.grouped)
    }

    private func colorBinding(for band: RemainingQuotaColor.Band) -> Binding<Color> {
        let appearance = paletteAppearance
        return Binding(
            get: { Color(nsColor: settings.remainingQuotaColors.color(for: band, appearance: appearance)) },
            set: { settings.remainingQuotaColors.setColor(NSColor($0), for: band, appearance: appearance) }
        )
    }
}
