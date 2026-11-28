import SwiftUI

struct SettingsRootView: View {
    @ObservedObject var store: UsageStore
    @ObservedObject var settings: SettingsStore

    var body: some View {
        TabView {
            GeneralSettingsTab(store: store, settings: settings)
                .tabItem {
                    Label(L("General"), systemImage: "gearshape")
                }
            ProvidersSettingsTab(store: store, settings: settings)
                .tabItem {
                    Label(L("Providers"), systemImage: "list.bullet")
                }
            DisplaySettingsTab(settings: settings)
                .tabItem {
                    Label(L("Display"), systemImage: "eye")
                }
            AlertsSettingsTab(store: store, settings: settings)
                .tabItem {
                    Label(L("Alerts"), systemImage: "bell")
                }
        }
        .frame(width: 500, height: 420)
    }
}
