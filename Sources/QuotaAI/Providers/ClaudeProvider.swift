import CryptoKit
import Foundation
import Security

/// Claude Code usage from Anthropic's OAuth usage endpoint, authenticated with
/// the token that the Claude CLI stores locally (credentials file or Keychain).
///
/// Read-only: the token is read fresh on each fetch and never refreshed here.
/// Anthropic refresh tokens are single-use, so refreshing Claude Code's token
/// would sign Claude Code out. When the stored token has expired we report it
/// and wait for Claude Code to renew it the next time it runs.
struct ClaudeProvider: UsageProvider {
    let id = "claude"
    let displayName = "Claude"
    let shortCode = "Cl"

    var credentialsPath = NSHomeDirectory() + "/.claude/.credentials.json"
    var keychainService = "Claude Code-credentials"
    var cache: ClaudeSessionCache = ClaudeProvider.sharedCache
    var transport: ClaudeTransport = .live
    /// Overrides the on-disk/Keychain lookup (tests only).
    var credentialsLoader: (@Sendable () throws -> Credentials)?

    static let sharedCache = ClaudeSessionCache()

    static let usageURL = URL(string: "https://api.anthropic.com/api/oauth/usage")!
    static let betaHeader = "oauth-2025-04-20"
    static let userAgent = "claude-code/2.1.0"

    var isDetected: Bool {
        FileManager.default.fileExists(atPath: credentialsPath)
            || Self.keychainItemExists(service: keychainService)
    }

    var dashboardURL: URL? {
        URL(string: "https://claude.ai/settings/usage")
    }

    func fetch() async throws -> ProviderUsage {
        try await fetch(forceRefresh: false)
    }

    func fetch(forceRefresh: Bool) async throws -> ProviderUsage {
        let stored = try credentialsLoader?() ?? loadCredentials()
        let now = Date()
        let response = try await cache.usage(
            stored: stored, now: now, forceRefresh: forceRefresh, transport: transport
        )
        return Self.usage(from: response, plan: stored.subscriptionType, now: now)
    }

    // MARK: - Credentials

    struct Credentials: Equatable, Sendable {
        var accessToken: String
        var expiresAt: Date?
        var subscriptionType: String?

        var isExpired: Bool { isExpired(at: Date()) }

        func isExpired(at now: Date) -> Bool {
            guard let expiresAt else { return false }
            // Treat a token about to expire as expired to avoid a racing 401.
            return expiresAt.timeIntervalSince(now) < 30
        }

        /// Stable, non-reversible identity of the stored token. Changes when
        /// Claude Code renews it or the user signs in again.
        var identity: String {
            SHA256.hash(data: Data(accessToken.utf8))
                .map { String(format: "%02x", $0) }.joined()
        }
    }

    private struct CredentialsFile: Decodable {
        struct OAuth: Decodable {
            let accessToken: String
            let expiresAt: Double?
            let subscriptionType: String?
        }
        let claudeAiOauth: OAuth
    }

    func loadCredentials() throws -> Credentials {
        if let data = FileManager.default.contents(atPath: credentialsPath),
           let creds = Self.decodeCredentials(data) {
            return creds
        }
        if let data = Self.keychainSecret(service: keychainService),
           let creds = Self.decodeCredentials(data) {
            return creds
        }
        throw ClaudeError.notSignedIn
    }

    static func decodeCredentials(_ data: Data) -> Credentials? {
        guard let file = try? JSONDecoder().decode(CredentialsFile.self, from: data),
              !file.claudeAiOauth.accessToken.isEmpty else {
            return nil
        }
        let expiresAt = file.claudeAiOauth.expiresAt.map {
            // Anthropic stores expiry in milliseconds since epoch.
            Date(timeIntervalSince1970: $0 / 1000)
        }
        return Credentials(
            accessToken: file.claudeAiOauth.accessToken,
            expiresAt: expiresAt,
            subscriptionType: file.claudeAiOauth.subscriptionType
        )
    }

    // MARK: - Networking

    static func fetchUsage(accessToken: String) async throws -> UsageResponse {
        var request = URLRequest(url: usageURL)
        request.timeoutInterval = 20
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue(betaHeader, forHTTPHeaderField: "anthropic-beta")
        request.setValue(userAgent, forHTTPHeaderField: "User-Agent")

        let (data, response) = try await HTTP.session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw ClaudeError.badResponse }
        if http.statusCode == 401 { throw ClaudeError.unauthorized }
        if http.statusCode == 429 { throw ClaudeError.rateLimited(retryAfter: retryAfter(http)) }
        guard http.statusCode == 200 else { throw ClaudeError.httpStatus(http.statusCode) }
        guard let decoded = try? JSONDecoder().decode(UsageResponse.self, from: data) else {
            throw ClaudeError.badResponse
        }
        return decoded
    }

    /// `Retry-After` in seconds (the HTTP-date form is not used by Anthropic).
    static func retryAfter(_ response: HTTPURLResponse) -> TimeInterval? {
        guard let value = response.value(forHTTPHeaderField: "Retry-After"),
              let seconds = TimeInterval(value.trimmingCharacters(in: .whitespaces)),
              seconds > 0 else { return nil }
        return seconds
    }

    // MARK: - Response mapping

    /// Windows are keyed dynamically; we pull the ones we display.
    struct UsageResponse: Decodable, Equatable, Sendable {
        struct Window: Decodable, Equatable, Sendable {
            let utilization: Double?
            let resetsAt: String?
            enum CodingKeys: String, CodingKey {
                case utilization
                case resetsAt = "resets_at"
            }
        }
        var fiveHour: Window?
        var sevenDay: Window?
        var sevenDayOpus: Window?
        var sevenDaySonnet: Window?

        enum CodingKeys: String, CodingKey {
            case fiveHour = "five_hour"
            case sevenDay = "seven_day"
            case sevenDayOpus = "seven_day_opus"
            case sevenDaySonnet = "seven_day_sonnet"
        }
    }

    static func usage(from response: UsageResponse, plan: String?, now: Date) -> ProviderUsage {
        func window(_ w: UsageResponse.Window?, _ label: String) -> UsageWindow? {
            guard let w, let utilization = w.utilization else { return nil }
            let resetsAt = w.resetsAt.flatMap {
                ISO8601DateFormatter.flexible.date(from: $0) ?? ISO8601DateFormatter.plain.date(from: $0)
            }
            return UsageWindow(label: label, usedPercent: max(0, min(100, utilization)), resetsAt: resetsAt)
        }

        let windows = [
            window(response.fiveHour, L("5h limit")),
            window(response.sevenDay, L("Weekly limit")),
            window(response.sevenDayOpus, L("Weekly (Opus)")),
            window(response.sevenDaySonnet, L("Weekly (Sonnet)")),
        ].compactMap { $0 }

        return ProviderUsage(planName: plan?.capitalized, windows: windows, asOf: now)
    }

    // MARK: - Keychain

    static func keychainItemExists(service: String) -> Bool {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecReturnData as String: false,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        return SecItemCopyMatching(query as CFDictionary, nil) == errSecSuccess
    }

    static func keychainSecret(service: String) -> Data? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess else { return nil }
        return result as? Data
    }
}

/// Network seam so the session logic can be tested without Anthropic.
struct ClaudeTransport: Sendable {
    var usage: @Sendable (_ accessToken: String) async throws -> ClaudeProvider.UsageResponse

    static let live = ClaudeTransport(
        usage: { try await ClaudeProvider.fetchUsage(accessToken: $0) }
    )
}

/// In-memory request state for the Claude provider (nothing is persisted):
/// - an expired stored token is reported without touching the network;
/// - concurrent usage requests share one call;
/// - after HTTP 429 it backs off (honouring `Retry-After`), because the usage
///   endpoint's rate limit is shared with Claude Code itself;
/// - a usage response younger than `minimumUsageInterval` is reused unless
///   the user explicitly refreshes.
/// State is keyed by `Credentials.identity`, so a renewed token starts fresh.
actor ClaudeSessionCache {
    static let minimumUsageInterval: TimeInterval = 55
    static let defaultUsageBackoff: TimeInterval = 60

    private var identity: String?
    private var usageBlockedUntil: Date?
    private var lastUsage: (response: ClaudeProvider.UsageResponse, at: Date)?
    private var usageTask: Task<ClaudeProvider.UsageResponse, Error>?

    func usage(
        stored: ClaudeProvider.Credentials,
        now: Date,
        forceRefresh: Bool,
        transport: ClaudeTransport
    ) async throws -> ClaudeProvider.UsageResponse {
        adopt(identity: stored.identity)

        if stored.isExpired(at: now) { throw ClaudeError.sessionExpired }
        if !forceRefresh, let last = lastUsage,
           now.timeIntervalSince(last.at) < Self.minimumUsageInterval {
            return last.response
        }
        if let until = usageBlockedUntil, until > now {
            throw ClaudeError.rateLimited(retryAfter: until.timeIntervalSince(now))
        }
        if let usageTask {
            return try await usageTask.value
        }

        let key = stored.identity
        let token = stored.accessToken
        let task = Task { try await transport.usage(token) }
        usageTask = task
        defer { if identity == key { usageTask = nil } }
        do {
            let response = try await task.value
            if identity == key { lastUsage = (response, Date()) }
            return response
        } catch ClaudeError.unauthorized {
            throw ClaudeError.sessionExpired
        } catch let error as ClaudeError {
            if identity == key, case .rateLimited(let retryAfter) = error {
                usageBlockedUntil = Date().addingTimeInterval(retryAfter ?? Self.defaultUsageBackoff)
            }
            throw error
        }
    }

    /// Drops all state when the stored token changes.
    private func adopt(identity newIdentity: String) {
        guard identity != newIdentity else { return }
        identity = newIdentity
        usageBlockedUntil = nil
        lastUsage = nil
        usageTask = nil
    }
}

enum ClaudeError: LocalizedError, Equatable {
    case notSignedIn
    case sessionExpired
    case unauthorized
    case rateLimited(retryAfter: TimeInterval?)
    case badResponse
    case httpStatus(Int)

    var errorDescription: String? {
        switch self {
        case .notSignedIn: return L("Not signed in to Claude Code")
        case .sessionExpired, .unauthorized:
            return L("Claude Code sign-in expired — run Claude Code once to refresh it")
        case .rateLimited: return L("Anthropic rate-limited the usage request")
        case .badResponse: return L("Unexpected response from Anthropic")
        case .httpStatus(let code): return L("Anthropic returned HTTP \(code)")
        }
    }
}
