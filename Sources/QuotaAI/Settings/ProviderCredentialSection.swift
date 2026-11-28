import SwiftUI

struct ProviderCredentialSection: View {
    let provider: any UsageProvider
    @ObservedObject var store: UsageStore
    @ObservedObject private var openRouterAuth = OpenRouterAuthFlow.shared
    @State private var draftKey = ""
    @State private var hasKey: Bool
    @State private var assessmentGeneration = 0
    @State private var credentialError: String?

    init(provider: any UsageProvider, store: UsageStore) {
        self.provider = provider
        self.store = store
        _hasKey = State(initialValue: KeychainStore.exists(provider.keychainAccount))
    }

    var body: some View {
        switch provider.authKind {
        case .localCredentials:
            LabeledContent(L("Status"), value: provider.isDetected ? L("Detected") : L("Not detected"))
        case .apiKey(let keyURL):
            Group {
                if let credentialError {
                    Text(credentialError).font(.caption).foregroundStyle(.orange)
                }
                if let help = provider.credentialHelpText {
                    Text(help)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                if provider.id == "claude-api" {
                    Link(L("View prepaid credits"), destination: URL(string: "https://platform.claude.com/settings/billing")!)
                        .font(.caption)
                }
                if hasKey {
                    LabeledContent(provider.apiKeyPlaceholder) {
                        HStack {
                            Text(L("Saved"))
                            Button(L("Remove")) { removeKey() }
                        }
                    }
                    CredentialAssessmentView(
                        provider: provider,
                        assessmentGeneration: assessmentGeneration
                    )
                } else {
                    SecureField(provider.apiKeyPlaceholder, text: $draftKey)
                    HStack {
                        Button(L("Save")) { saveKey() }
                            .disabled(draftKey.trimmingCharacters(in: .whitespaces).isEmpty)
                        if let url = URL(string: keyURL) {
                            Link(L("Get key"), destination: url)
                        }
                    }
                }
            }
        case .oauth:
            Group {
                if let credentialError {
                    Text(credentialError).font(.caption).foregroundStyle(.orange)
                }
                if hasKey {
                    LabeledContent(L("Status")) {
                        HStack {
                            Text(L("Connected"))
                            Button(L("Disconnect")) { removeKey() }
                        }
                    }
                    CredentialAssessmentView(
                        provider: provider,
                        assessmentGeneration: assessmentGeneration
                    )
                } else {
                    HStack {
                        Button(L("Connect…")) { OpenRouterAuthFlow.shared.start() }
                            .disabled(openRouterAuth.status == .connecting)
                        if openRouterAuth.status == .connecting {
                            ProgressView().controlSize(.small)
                            Text(L("Waiting for browser authorization…"))
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                    }
                    if case .failed(let message) = openRouterAuth.status {
                        Text(message)
                            .font(.caption)
                            .foregroundStyle(.orange)
                    }
                    SecureField(L("Or paste an API key"), text: $draftKey)
                    HStack {
                        Button(L("Save")) { saveKey() }
                            .disabled(draftKey.trimmingCharacters(in: .whitespaces).isEmpty)
                        if let url = provider.dashboardURL {
                            Link(L("Get key"), destination: url)
                        }
                    }
                }
            }
            .onReceive(NotificationCenter.default.publisher(for: .providerCredentialsChanged)) { _ in
                let connected = KeychainStore.exists(provider.keychainAccount)
                if connected && !hasKey {
                    assessmentGeneration += 1
                }
                hasKey = connected
            }
            .onChange(of: openRouterAuth.status) { _, newStatus in
                switch newStatus {
                case .connected:
                    AccessibilityNotification.Announcement(L("OpenRouter connected")).post()
                case .failed:
                    AccessibilityNotification.Announcement(L("Connection failed")).post()
                case .idle, .connecting:
                    break
                }
            }
        }
    }

    private func saveKey() {
        let key = draftKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty else { return }
        guard KeychainStore.set(key, account: provider.keychainAccount) else {
            credentialError = L("Could not save the API key in Keychain. Please try again.")
            return
        }
        credentialError = nil
        draftKey = ""
        hasKey = true
        assessmentGeneration += 1
        store.credentialDidChange(for: provider.id)
        AccessibilityNotification.Announcement(L("API key saved")).post()
    }

    private func removeKey() {
        guard KeychainStore.delete(provider.keychainAccount) else {
            credentialError = L("Could not remove the API key from Keychain. Please try again.")
            return
        }
        credentialError = nil
        hasKey = false
        store.credentialDidChange(for: provider.id)
        AccessibilityNotification.Announcement(L("API key removed")).post()
    }
}

struct CredentialAssessmentView: View {
    let provider: any UsageProvider
    let assessmentGeneration: Int
    @State private var assessment: CredentialAssessment?
    @State private var isAssessing = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if isAssessing {
                HStack(spacing: 6) {
                    ProgressView()
                        .controlSize(.small)
                    Text(L("Checking key…"))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            } else if let assessment {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text(assessment.keyTypeLabel)
                        .font(.caption2)
                        .padding(.horizontal, 6)
                        .padding(.vertical, 2)
                        .background(.quaternary, in: Capsule())
                        .help(assessment.summary)
                    Text(assessment.summary)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                .accessibilityElement(children: .combine)
                .accessibilityLabel(
                    String(
                        format: L("Key type: %@. %@"),
                        assessment.keyTypeLabel,
                        assessment.summary
                    )
                )

                DisclosureGroup(L("About this key")) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(assessment.detail)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        if let hint = assessment.upgradeHint {
                            Text(hint)
                                .font(.caption)
                                .foregroundStyle(.orange)
                        }
                        if let url = assessment.manageURL {
                            Link(L("Manage keys"), destination: url)
                                .font(.caption)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.top, 4)
                }
                .font(.caption)
            }
        }
        .task(id: assessmentGeneration) {
            isAssessing = true
            assessment = await provider.assessCredential()
            isAssessing = false
        }
    }
}
