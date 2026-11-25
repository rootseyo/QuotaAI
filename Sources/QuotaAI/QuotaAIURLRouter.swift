import Foundation

extension Notification.Name {
    static let quotaAIRefreshRequested = Notification.Name("quotaAIRefreshRequested")
    static let quotaAIOpenUsageDetails = Notification.Name("quotaAIOpenUsageDetails")
}

enum QuotaAIURLRouter {
    /// Handles quotaai:// URLs that are not OAuth callbacks.
    /// Returns true when the URL was consumed.
    @MainActor
    static func handle(_ url: URL, settings: SettingsStore) -> Bool {
        guard url.scheme == OpenRouterAuthFlow.callbackScheme else { return false }

        switch url.host {
        case "refresh":
            guard settings.agentAccessEnabled else { return true }
            NotificationCenter.default.post(name: .quotaAIRefreshRequested, object: nil)
            return true
        case "details":
            NotificationCenter.default.post(name: .quotaAIOpenUsageDetails, object: nil)
            return true
        default:
            return false
        }
    }
}
