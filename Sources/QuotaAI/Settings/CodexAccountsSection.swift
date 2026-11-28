import SwiftUI

struct CodexAccountsSection: View {
    @ObservedObject var store: UsageStore
    @ObservedObject var settings: SettingsStore
    @State private var showingAddForm = false
    @State private var draftLabel = ""
    @State private var draftHomePath = ""
    @State private var addFormError: String?

    var body: some View {
        Group {
            Text(L("Codex accounts"))
                .font(.caption)
                .foregroundStyle(.secondary)

            primaryAccountRow

            ForEach(settings.codexExtraAccounts) { account in
                extraAccountRow(account)
            }

            ForEach(discoveredAccounts) { candidate in
                discoveredAccountRow(candidate)
            }

            if showingAddForm {
                addAccountForm
            } else {
                Button(L("Add account…")) {
                    draftLabel = ""
                    draftHomePath = CodexAccountConfig.suggestedHomePath(for: "")
                    showingAddForm = true
                    addFormError = nil
                }
            }

            Text(L("OpenAI doesn't publish subscription renewal dates, so confirm yours on the platform that bills you — ChatGPT, Apple, or Google Play. Renewal dates are separate from usage-limit resets."))
                .font(.caption2)
                .foregroundStyle(.tertiary)

            Text(L("Removing only forgets the path; it does not sign out."))
                .font(.caption2)
                .foregroundStyle(.tertiary)
        }
        .onAppear(perform: refreshDiscovered)
        .onChange(of: settings.codexExtraAccounts) { _, _ in refreshDiscovered() }
    }

    @State private var discoveredAccounts: [CodexAccountDiscovery.Candidate] = []

    private func refreshDiscovered() {
        discoveredAccounts = CodexAccountDiscovery.discover(configured: settings.codexExtraAccounts)
    }

    private func discoveredAccountRow(_ candidate: CodexAccountDiscovery.Candidate) -> some View {
        HStack(alignment: .firstTextBaseline) {
            VStack(alignment: .leading, spacing: 2) {
                Text(String(format: L("Found signed-in Codex home: %@"), candidate.suggestedLabel))
                    .font(.caption)
                Text(candidate.homePath)
                    .font(.caption2.monospaced())
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Button(L("Add")) {
                settings.addCodexAccount(CodexAccountConfig(
                    label: candidate.suggestedLabel,
                    codexHomePath: candidate.homePath
                ))
                refreshDiscovered()
            }
            .font(.caption)
            .help(L("Adds this account to QuotaAI using the folder name as its label."))
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel(String(format: L("Found signed-in Codex home: %@"), candidate.homePath))
    }

    private var primaryAccountRow: some View {
        let status = codexAccountStatus(providerID: "codex")
        return VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(L("Default (~/.codex)"))
                        .font(.caption)
                    Text("~/.codex")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Text(status)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.trailing)
            }
            .accessibilityElement(children: .combine)
            .accessibilityLabel(String(format: L("Default Codex account, %@"), status))

            CodexSubscriptionSettings(providerID: "codex", settings: settings)
        }
    }

    private func extraAccountRow(_ account: CodexAccountConfig) -> some View {
        let providerID = "codex:\(account.id.uuidString)"
        let status = codexAccountStatus(providerID: providerID)
        return VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(account.label)
                        .font(.caption)
                    Text(account.codexHomePath)
                        .font(.caption2.monospaced())
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Text(status)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.trailing)
                Button(L("Remove")) {
                    settings.removeCodexAccount(id: account.id)
                }
                .font(.caption)
            }
            .accessibilityElement(children: .combine)
            .accessibilityLabel(String(format: L("%@, %@, %@"), account.label, account.codexHomePath, status))

            CodexSubscriptionSettings(providerID: providerID, settings: settings)
        }
    }

    private func codexAccountStatus(providerID: String) -> String {
        if let state = store.states[providerID] {
            switch state {
            case .error(let message):
                return message
            case .loading:
                return L("Loading…")
            case .ready(let usage), .stale(let usage, _, _):
                if let email = CodexAccountCache.shared.lastEmail(for: providerID) {
                    let plan = usage.planName ?? CodexPlan.displayName(
                        CodexAccountCache.shared.lastPlanType(for: providerID)
                    )
                    if let plan {
                        return "\(email) · \(plan)"
                    }
                    return email
                }
                return L("Not signed in")
            }
        }
        if let email = CodexAccountCache.shared.lastEmail(for: providerID) {
            let plan = CodexPlan.displayName(CodexAccountCache.shared.lastPlanType(for: providerID))
            if let plan { return "\(email) · \(plan)" }
            return email
        }
        return L("Not signed in")
    }

    private var addAccountForm: some View {
        let loginCommand = CodexAccountConfig.loginCommand(homePath: draftHomePath)
        return VStack(alignment: .leading, spacing: 8) {
            TextField(L("Label"), text: $draftLabel)
                .onChange(of: draftLabel) { _, newValue in
                    if draftHomePath.isEmpty || draftHomePath.hasPrefix("~/.codex-") {
                        draftHomePath = CodexAccountConfig.suggestedHomePath(for: newValue)
                    }
                }
            TextField(L("Home path"), text: $draftHomePath)
                .font(.caption.monospaced())

            Text(loginCommand)
                .font(.caption.monospaced())
                .textSelection(.enabled)
                .padding(6)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 4))

            Button(L("Copy login command")) {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(loginCommand, forType: .string)
                AccessibilityNotification.Announcement(L("Login command copied")).post()
            }
            .font(.caption)
            .accessibilityLabel(L("Copy login command"))
            .accessibilityHint(L("Copies the Terminal command to sign in this Codex account"))

            Text(L("Run this in Terminal to sign in that account. QuotaAI never handles your login or tokens — the Codex CLI does."))
                .font(.caption2)
                .foregroundStyle(.secondary)

            if let addFormError {
                Text(addFormError)
                    .font(.caption)
                    .foregroundStyle(.orange)
            }

            HStack {
                Button(L("Save")) { saveAccount() }
                    .disabled(!isAddFormValid)
                Button(L("Cancel"), role: .cancel) {
                    showingAddForm = false
                    addFormError = nil
                }
            }
        }
        .padding(.vertical, 4)
    }

    private var isAddFormValid: Bool {
        let label = draftLabel.trimmingCharacters(in: .whitespacesAndNewlines)
        let path = draftHomePath.trimmingCharacters(in: .whitespacesAndNewlines)
        return !label.isEmpty && !path.isEmpty && path != "~/.codex"
    }

    private func saveAccount() {
        let label = draftLabel.trimmingCharacters(in: .whitespacesAndNewlines)
        let path = draftHomePath.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !label.isEmpty else {
            addFormError = L("Label is required")
            return
        }
        guard !path.isEmpty else {
            addFormError = L("Home path is required")
            return
        }
        guard path != "~/.codex" else {
            addFormError = L("Choose a path other than ~/.codex")
            return
        }
        settings.addCodexAccount(CodexAccountConfig(label: label, codexHomePath: path))
        showingAddForm = false
        addFormError = nil
    }
}
