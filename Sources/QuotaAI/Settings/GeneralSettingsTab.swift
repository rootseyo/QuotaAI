import SwiftUI
import ServiceManagement

struct GeneralSettingsTab: View {
    @ObservedObject var store: UsageStore
    @ObservedObject var settings: SettingsStore
    @Environment(\.openWindow) private var openWindow
    @State private var launchAtLogin = SMAppService.mainApp.status == .enabled
    @State private var diagnosticsCopied = false
    @State private var cliInstallPath: String?
    @State private var cliInstallResultMessage: String?

    var body: some View {
        Form {
            Section {
                Toggle(L("Launch at Login"), isOn: $launchAtLogin)
                    .onChange(of: launchAtLogin) { _, enabled in setLaunchAtLogin(enabled) }
            }
            Section {
                Toggle(L("Enable agent & CLI access"), isOn: $settings.agentAccessEnabled)
                    .onChange(of: settings.agentAccessEnabled) { _, enabled in
                        if enabled {
                            StatusSnapshotWriter.writeIfEnabled(store: store, settings: settings)
                        }
                    }

                cliToolInstallSection
            } footer: {
                Text(L("Writes a machine-readable usage snapshot (never credentials) to Application Support for the quotaai command-line tool and other local agents."))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Section {
                Picker(L("Refresh interval"), selection: Binding(
                    get: { settings.refreshInterval },
                    set: { newValue in
                        settings.refreshInterval = newValue
                        store.rescheduleTimer()
                    }
                )) {
                    ForEach(SettingsStore.refreshOptions, id: \.seconds) { option in
                        Text(option.label).tag(option.seconds)
                    }
                }
                .pickerStyle(.menu)
            }
            Section {
                Button {
                    copyDiagnostics()
                } label: {
                    if diagnosticsCopied {
                        Label(L("Copied"), systemImage: "checkmark")
                    } else {
                        Text(L("Copy Diagnostics"))
                    }
                }
            } footer: {
                Text(L("Copies redacted troubleshooting info to the clipboard — never includes keys or tokens."))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Section {
                Button(L("About QuotaAI")) {
                    openWindow(id: "about")
                    NSApp.activate(ignoringOtherApps: true)
                }
            }
        }
        .formStyle(.grouped)
        .onAppear {
            refreshCLIInstallStatus()
        }
    }

    @ViewBuilder
    private var cliToolInstallSection: some View {
        if CLIToolInstaller.isBundledApp {
            Group {
                if let path = cliInstallPath {
                    Text(String(format: L("Command-line tool installed at %@"), path))
                } else {
                    Text(L("Command-line tool not on your PATH."))
                }

                if let cliInstallResultMessage {
                    Text(cliInstallResultMessage)
                }
            }
            .font(.caption)
            .foregroundStyle(.secondary)

            Button(CLIToolInstaller.isInstalledAtLocalPath()
                ? L("Reinstall Command-Line Tool…")
                : L("Install Command-Line Tool…")) {
                installCLITool()
            }
        } else {
            Text(L("Run from QuotaAI.app to install the command-line tool."))
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private func refreshCLIInstallStatus() {
        cliInstallPath = CLIToolInstaller.detectedInstallPath()
    }

    private func installCLITool() {
        cliInstallResultMessage = nil

        switch CLIToolInstaller.install() {
        case .success:
            refreshCLIInstallStatus()
            let message = L("Installed. Open a new terminal and run quotaai --help.")
            cliInstallResultMessage = message
            AccessibilityNotification.Announcement(message).post()
        case .failure(_ as CancellationError):
            break
        case .failure(let error):
            let message = String(format: L("Couldn't install the command-line tool: %@"), error.localizedDescription)
            cliInstallResultMessage = message
            AccessibilityNotification.Announcement(message).post()
        }
    }

    private func copyDiagnostics() {
        let report = Diagnostics.report(store: store, settings: settings)
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(report, forType: .string)
        diagnosticsCopied = true
        AccessibilityNotification.Announcement(L("Diagnostics copied")).post()
        Task {
            try? await Task.sleep(for: .seconds(2))
            diagnosticsCopied = false
        }
    }

    private func setLaunchAtLogin(_ enabled: Bool) {
        do {
            if enabled {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
        } catch {
            launchAtLogin = SMAppService.mainApp.status == .enabled
        }
    }
}
