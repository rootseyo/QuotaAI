import Combine
import Foundation
import Sparkle

/// Sparkle-backed auto-updates. The updater checks the appcast feed declared
/// in Info.plist (SUFeedURL, pointing at the latest GitHub release asset) and
/// verifies downloads against the EdDSA public key (SUPublicEDKey).
///
/// Local builds omit the feed and signing key, so they have no updater.
@MainActor
final class Updater: ObservableObject {
    static let shared = Updater()

    @Published private(set) var canCheck = false
    private let controller: SPUStandardUpdaterController?

    var isConfigured: Bool { controller != nil }

    private init() {
        let bundle = Bundle.main
        guard bundle.object(forInfoDictionaryKey: "QuotaAIUpdatesEnabled") as? Bool == true,
              let feed = bundle.object(forInfoDictionaryKey: "SUFeedURL") as? String,
              URL(string: feed)?.scheme == "https",
              let key = bundle.object(forInfoDictionaryKey: "SUPublicEDKey") as? String,
              Data(base64Encoded: key)?.count == 32 else {
            controller = nil
            return
        }

        let updaterController = SPUStandardUpdaterController(
            startingUpdater: true,
            updaterDelegate: nil,
            userDriverDelegate: nil
        )
        controller = updaterController
        updaterController.updater.publisher(for: \.canCheckForUpdates)
            .receive(on: DispatchQueue.main)
            .assign(to: &$canCheck)
    }

    func checkForUpdates() {
        guard canCheck else { return }
        controller?.checkForUpdates(nil)
    }
}
