import CryptoKit
import Foundation
import Security

/// Claude Code usage from Anthropic's OAuth usage endpoint, authenticated with
/// the token that the Claude CLI stores locally (credentials file or Keychain).
///
/// The token is read fresh on each fetch. When it has expired, it is refreshed
/// once and the result is kept in memory (`ClaudeSessionCache`) until Claude
/// Code itself writes new credentials — never written back to disk.
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
    static let tokenURL = URL(string: "https://platform.claude.com/v1/oauth/token")!
    static let oauthClientID = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"
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
        var refreshToken: String?
        var expiresAt: Date?
        var subscriptionType: String?

        var isExpired: Bool { isExpired(at: Date()) }

        func isExpired(at now: Date) -> Bool {
            guard let expiresAt else { return false }
            // Refresh a minute early to avoid racing the expiry.
            return expiresAt.timeIntervalSince(now) < 60
        }

        /// Stable, non-reversible identity of the stored sign-in. Changes when
        /// Claude Code refreshes or the user signs in again.
        var identity: String {
            let material = refreshToken ?? accessToken
            return SHA256.hash(data: Data(material.utf8))
                .map { String(format: "%02x", $0) }.joined()
        }
    }

    private struct CredentialsFile: Decodable {
        struct OAuth: Decodable {
            let accessToken: String
            let refreshToken: String?
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
        let refresh = file.claudeAiOauth.refreshToken.flatMap { $0.isEmpty ? nil : $0 }
        return Credentials(
            accessToken: file.claudeAiOauth.accessToken,
            refreshToken: refresh,
            expiresAt: expiresAt,
            subscriptionType: file.claudeAiOauth.subscriptionType
        )
    }

    // MARK: - Networking

    static func refresh(refreshToken: String) async throws -> TokenGrant {
        var request = URLRequest(url: tokenURL)
        request.httpMethod = "POST"
        request.timeoutInterval = 20
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        var components = URLComponents()
        components.queryItems = [
            URLQueryItem(name: "grant_type", value: "refresh_token"),
            URLQueryItem(name: "refresh_token", value: refreshToken),
            URLQueryItem(name: "client_id", value: oauthClientID),
        ]
        request.httpBody = (components.percentEncodedQuery ?? "").data(using: .utf8)

        let (data, response) = try await HTTP.session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw ClaudeError.badResponse }
        switch http.statusCode {
        case 200:
            break
        case 429:
            throw ClaudeError.rateLimited(retryAfter: retryAfter(http))
        case 400, 401, 403:
            // invalid_grant: the refresh token was revoked or already rotated.
            throw ClaudeError.sessionExpired
        default:
            throw ClaudeError.refreshFailed(http.statusCode)
        }

        struct TokenResponse: Decodable {
            let accessToken: String
            let refreshToken: String?
            let expiresIn: Double?
            enum CodingKeys: String, CodingKey {
                case accessToken = "access_token"
                case refreshToken = "refresh_token"
                case expiresIn = "expires_in"
            }
        }
        guard let token = try? JSONDecoder().decode(TokenResponse.self, from: data) else {
            throw ClaudeError.badResponse
        }
        return TokenGrant(
            accessToken: token.accessToken,
            refreshToken: token.refreshToken,
            expiresIn: token.expiresIn
        )
    }

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

/// Result of an OAuth refresh-token grant.
struct TokenGrant: Equatable, Sendable {
    var accessToken: String
    var refreshToken: String?
    var expiresIn: TimeInterval?
}

/// Network seam so the session logic can be tested without Anthropic.
struct ClaudeTransport: Sendable {
    var refresh: @Sendable (_ refreshToken: String) async throws -> TokenGrant
    var usage: @Sendable (_ accessToken: String) async throws -> ClaudeProvider.UsageResponse

    static let live = ClaudeTransport(
        refresh: { try await ClaudeProvider.refresh(refreshToken: $0) },
        usage: { try await ClaudeProvider.fetchUsage(accessToken: $0) }
    )
}

/// In-memory session state for the Claude provider:
/// - keeps a refreshed token for the current stored sign-in, so an expired
///   on-disk token costs one refresh instead of one per poll;
/// - coalesces concurrent refreshes and usage requests;
/// - backs off after 429s (honouring `Retry-After`) and after a rejected
///   refresh token, because Anthropic's token endpoint rate-limits hard;
/// - reuses a usage response younger than `minimumUsageInterval` unless the
///   user explicitly refreshes.
/// Everything is keyed by `Credentials.identity`, so signing in again or
/// Claude Code rewriting its credentials starts fresh. Nothing is persisted.
actor ClaudeSessionCache {
    static let minimumUsageInterval: TimeInterval = 55
    static let defaultUsageBackoff: TimeInterval = 60
    static let defaultRefreshBackoff: TimeInterval = 5 * 60

    private var identity: String?
    private var refreshed: ClaudeProvider.Credentials?
    private var refreshTask: Task<ClaudeProvider.Credentials, Error>?
    private var refreshBlocked: (until: Date, error: ClaudeError)?
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
        let task = Task { try await self.performUsage(stored: stored, now: now, transport: transport) }
        usageTask = task
        defer { if identity == key { usageTask = nil } }
        do {
            let response = try await task.value
            if identity == key { lastUsage = (response, Date()) }
            return response
        } catch let error as ClaudeError {
            if identity == key, case .rateLimited(let retryAfter) = error {
                usageBlockedUntil = Date().addingTimeInterval(retryAfter ?? Self.defaultUsageBackoff)
            }
            throw error
        }
    }

    private func performUsage(
        stored: ClaudeProvider.Credentials,
        now: Date,
        transport: ClaudeTransport
    ) async throws -> ClaudeProvider.UsageResponse {
        let creds = try await validCredentials(stored: stored, now: now, transport: transport)
        do {
            return try await transport.usage(creds.accessToken)
        } catch ClaudeError.unauthorized {
            // The token was revoked before its stated expiry: refresh once.
            guard stored.refreshToken != nil else { throw ClaudeError.sessionExpired }
            let renewed = try await refreshCredentials(stored: stored, transport: transport)
            do {
                return try await transport.usage(renewed.accessToken)
            } catch ClaudeError.unauthorized {
                throw ClaudeError.sessionExpired
            }
        }
    }

    private func validCredentials(
        stored: ClaudeProvider.Credentials,
        now: Date,
        transport: ClaudeTransport
    ) async throws -> ClaudeProvider.Credentials {
        if !stored.isExpired(at: now) { return stored }
        if let refreshed, !refreshed.isExpired(at: now) { return refreshed }
        return try await refreshCredentials(stored: stored, transport: transport)
    }

    private func refreshCredentials(
        stored: ClaudeProvider.Credentials,
        transport: ClaudeTransport
    ) async throws -> ClaudeProvider.Credentials {
        if let refreshTask { return try await refreshTask.value }
        if let blocked = refreshBlocked, blocked.until > Date() { throw blocked.error }
        // A rotated refresh token from our previous refresh supersedes the stored one.
        guard let refreshToken = refreshed?.refreshToken ?? stored.refreshToken else {
            throw ClaudeError.sessionExpired
        }

        let key = stored.identity
        let task = Task { () throws -> ClaudeProvider.Credentials in
            let grant = try await transport.refresh(refreshToken)
            return ClaudeProvider.Credentials(
                accessToken: grant.accessToken,
                refreshToken: grant.refreshToken ?? refreshToken,
                expiresAt: grant.expiresIn.map { Date().addingTimeInterval($0) },
                subscriptionType: stored.subscriptionType
            )
        }
        refreshTask = task
        defer { if identity == key { refreshTask = nil } }
        do {
            let creds = try await task.value
            if identity == key {
                refreshed = creds
                refreshBlocked = nil
            }
            return creds
        } catch let error as ClaudeError {
            if identity == key {
                let delay: TimeInterval
                if case .rateLimited(let retryAfter) = error {
                    delay = retryAfter ?? Self.defaultRefreshBackoff
                } else {
                    delay = Self.defaultRefreshBackoff
                }
                refreshBlocked = (Date().addingTimeInterval(delay), error)
            }
            throw error
        }
    }

    /// Drops all state when the stored sign-in changes.
    private func adopt(identity newIdentity: String) {
        guard identity != newIdentity else { return }
        identity = newIdentity
        refreshed = nil
        refreshTask = nil
        refreshBlocked = nil
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
    case refreshFailed(Int)
    case badResponse
    case httpStatus(Int)

    var errorDescription: String? {
        switch self {
        case .notSignedIn: return L("Not signed in to Claude Code")
        case .sessionExpired, .unauthorized:
            return L("Claude Code sign-in expired — run claude in Terminal to sign in again")
        case .rateLimited: return L("Anthropic rate-limited the usage request")
        case .refreshFailed(let code): return L("Claude token refresh failed (HTTP \(code))")
        case .badResponse: return L("Unexpected response from Anthropic")
        case .httpStatus(let code): return L("Anthropic returned HTTP \(code)")
        }
    }
}
