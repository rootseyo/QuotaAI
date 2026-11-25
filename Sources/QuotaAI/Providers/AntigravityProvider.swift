import Foundation
import Security

/// Antigravity CLI's own OAuth credentials (Keychain or fallback file); never accesses desktop app containers.
struct AntigravityProvider: UsageProvider {
    // Keep the old preference key so migration preserves visibility settings.
    let id = "gemini"
    var displayName: String { L("Antigravity") }
    let shortCode = "Ag"
    var credentialsPath = NSHomeDirectory() + "/.gemini/antigravity-cli/antigravity-oauth-token"
    var keychainService = "gemini"
    var keychainAccount = "antigravity"
    var isDetected: Bool {
        FileManager.default.fileExists(atPath: credentialsPath)
            || Self.keychainItemExists(service: keychainService, account: keychainAccount)
    }
    var credentialHelpText: String? { L("Sign in with Antigravity CLI (agy). QuotaAI reads its existing CLI credentials without modifying them.") }
    var dashboardURL: URL? { URL(string: "https://antigravity.google/") }

    static let quotaURL = URL(string: "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary")!
    // Public installed-app OAuth client shipped by Google's agy CLI.
    static let clientID = ["1071006060591-tmhssin2h21lcre235vtolojh4g403ep", "apps.googleusercontent.com"].joined(separator: ".")
    static let clientSecret = ["GOCSPX", "K58FWR486LdLJ1mLB8sXC4z6qDAf"].joined(separator: "-")

    struct CredentialFile: Decodable {
        struct Token: Decodable {
            let access_token: String
            let refresh_token: String?
            let expiry: String?
        }
        let token: Token
    }

    func fetch() async throws -> ProviderUsage {
        let file = try loadCredentials()
        var token = file.token.access_token
        let expiry = Self.parseExpiry(file.token.expiry)
        if token.isEmpty || (expiry?.timeIntervalSinceNow ?? 0) < 60 {
            guard let refresh = file.token.refresh_token, !refresh.isEmpty else { throw AntigravityError.notSignedIn }
            token = try await Self.refresh(refresh)
        }
        var request = URLRequest(url: Self.quotaURL)
        request.httpMethod = "POST"
        request.timeoutInterval = 20
        request.httpBody = Data("{}".utf8)
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        // Match agy's quota API version; older identities omit Gemini groups.
        request.setValue("antigravity/cli/1.4.12", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await HTTP.session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw AntigravityError.badResponse }
        if http.statusCode == 401 { throw AntigravityError.notSignedIn }
        if http.statusCode == 403 { throw AntigravityError.accessDenied }
        guard http.statusCode == 200 else { throw GeminiError.httpStatus(http.statusCode) }
        return try Self.usage(from: JSONDecoder().decode(QuotaResponse.self, from: data), now: Date())
    }

    func loadCredentials() throws -> CredentialFile {
        let fileCreds = FileManager.default.contents(atPath: credentialsPath)
            .flatMap(Self.decodeCredentialFile)
        let keychainCreds = Self.keychainSecret(service: keychainService, account: keychainAccount)
            .flatMap(Self.decodeCredentialFile)
        switch (keychainCreds, fileCreds) {
        case let (k?, f?):
            let kExpiry = Self.parseExpiry(k.token.expiry) ?? .distantPast
            let fExpiry = Self.parseExpiry(f.token.expiry) ?? .distantPast
            return fExpiry > kExpiry ? f : k
        case let (k?, nil):
            return k
        case let (nil, f?):
            return f
        case (nil, nil):
            throw AntigravityError.notSignedIn
        }
    }

    static func parseExpiry(_ raw: String?) -> Date? {
        raw.flatMap { ISO8601DateFormatter.flexible.date(from: $0) ?? ISO8601DateFormatter.plain.date(from: $0) }
    }

    static func decodeCredentialFile(_ data: Data) -> CredentialFile? {
        guard let text = String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines),
              !text.isEmpty else {
            return nil
        }
        let jsonData: Data
        if text.hasPrefix("go-keyring-base64:") {
            let encoded = String(text.dropFirst("go-keyring-base64:".count))
            guard let decoded = Data(base64Encoded: encoded) else { return nil }
            jsonData = decoded
        } else if text.hasPrefix("go-keyring-encoded:") {
            let encoded = String(text.dropFirst("go-keyring-encoded:".count))
            guard let decoded = decodeHex(encoded) else { return nil }
            jsonData = decoded
        } else {
            jsonData = Data(text.utf8)
        }
        return try? JSONDecoder().decode(CredentialFile.self, from: jsonData)
    }

    private static func decodeHex(_ hex: String) -> Data? {
        guard hex.count.isMultiple(of: 2) else { return nil }
        var bytes = Data(capacity: hex.count / 2)
        var index = hex.startIndex
        while index < hex.endIndex {
            let next = hex.index(index, offsetBy: 2)
            guard let byte = UInt8(hex[index..<next], radix: 16) else { return nil }
            bytes.append(byte)
            index = next
        }
        return bytes
    }

    static func keychainItemExists(service: String, account: String) -> Bool {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: false,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        return SecItemCopyMatching(query as CFDictionary, nil) == errSecSuccess
    }

    static func keychainSecret(service: String, account: String) -> Data? {
        guard keychainItemExists(service: service, account: account) else { return nil }
        // agy stores tokens via go-keyring (/usr/bin/security), which owns the item ACL.
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/security")
        process.arguments = ["find-generic-password", "-s", service, "-a", account, "-w"]
        let stdout = Pipe()
        process.standardOutput = stdout
        process.standardError = Pipe()
        do {
            try process.run()
            let data = stdout.fileHandleForReading.readDataToEndOfFile()
            process.waitUntilExit()
            if process.terminationStatus == 0, !data.isEmpty {
                return data
            }
        } catch {}
        return nil
    }

    private static func refresh(_ refreshToken: String) async throws -> String {
        var request = URLRequest(url: GeminiProvider.tokenURL)
        request.httpMethod = "POST"
        request.timeoutInterval = 20
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        var components = URLComponents()
        components.queryItems = [
            URLQueryItem(name: "grant_type", value: "refresh_token"),
            URLQueryItem(name: "refresh_token", value: refreshToken),
            URLQueryItem(name: "client_id", value: clientID),
            URLQueryItem(name: "client_secret", value: clientSecret),
        ]
        // Literal '+' must be encoded in form bodies.
        request.httpBody = components.percentEncodedQuery?.replacingOccurrences(of: "+", with: "%2B").data(using: .utf8)
        let (data, response) = try await HTTP.session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw AntigravityError.badResponse }
        guard http.statusCode == 200 else { throw AntigravityError.notSignedIn }
        struct Response: Decodable { let access_token: String }
        return try JSONDecoder().decode(Response.self, from: data).access_token
    }

    struct QuotaResponse: Decodable {
        struct Group: Decodable {
            struct Bucket: Decodable {
                let bucketId: String
                let displayName: String?
                let window: String?
                let remainingFraction: Double?
                let resetTime: String?
            }
            let displayName: String
            let buckets: [Bucket]?
        }
        let groups: [Group]?
    }

    static func usage(from response: QuotaResponse, now: Date) throws -> ProviderUsage {
        var ranked: [(rank: Int, index: Int, window: UsageWindow)] = []
        for group in response.groups ?? [] {
            for bucket in group.buckets ?? [] {
                guard let fraction = bucket.remainingFraction, fraction.isFinite else { continue }
                let label: String
                let rank: Int
                switch bucket.bucketId {
                case "gemini-5h": label = L("Base model (5h)"); rank = 0
                case "gemini-weekly": label = L("Base model (weekly)"); rank = 1
                case "gemini-argon-wtu-weekly": label = L("Argon (weekly)"); rank = 2
                case "3p-5h": label = L("Claude / GPT (5h)"); rank = 3
                case "3p-weekly": label = L("Claude / GPT (weekly)"); rank = 4
                default: label = "\(group.displayName): \(bucket.displayName ?? bucket.bucketId)"; rank = 5
                }
                let reset = bucket.resetTime.flatMap { ISO8601DateFormatter.flexible.date(from: $0) ?? ISO8601DateFormatter.plain.date(from: $0) }
                let usedPercent = 100 - max(0, min(1, fraction)) * 100
                let window = UsageWindow(label: label, usedPercent: usedPercent, resetsAt: reset)
                ranked.append((rank, ranked.count, window))
            }
        }
        ranked.sort { lhs, rhs in
            lhs.rank == rhs.rank ? lhs.index < rhs.index : lhs.rank < rhs.rank
        }
        let windows = ranked.map(\.window)
        guard !windows.isEmpty else { throw AntigravityError.badResponse }
        return ProviderUsage(planName: nil, windows: windows, asOf: now)
    }
}

enum AntigravityError: LocalizedError {
    case notSignedIn, badResponse, accessDenied
    var errorDescription: String? {
        switch self {
        case .notSignedIn: L("Sign in to Antigravity CLI by running agy, then refresh QuotaAI.")
        case .badResponse: L("Antigravity did not return readable quota data.")
        case .accessDenied: L("Google denied Antigravity quota access. Open agy to check account verification and access, then refresh.")
        }
    }
}
