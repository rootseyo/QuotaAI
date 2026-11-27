import XCTest
@testable import QuotaAI

final class ClaudeProviderTests: XCTestCase {
    func testDecodeCredentialsMillisecondExpiry() throws {
        let json = """
        {"claudeAiOauth":{"accessToken":"at","refreshToken":"rt","expiresAt":1770073758676,"subscriptionType":"max"}}
        """
        let creds = try XCTUnwrap(ClaudeProvider.decodeCredentials(Data(json.utf8)))
        XCTAssertEqual(creds.accessToken, "at")
        XCTAssertEqual(creds.refreshToken, "rt")
        XCTAssertEqual(creds.subscriptionType, "max")
        // 1770073758676 ms == 2026-02-02, well in the past -> expired.
        XCTAssertTrue(creds.isExpired)
    }

    func testUsageMappingFromResponse() throws {
        let json = """
        {
          "five_hour": {"utilization": 12.5, "resets_at": "2026-07-01T20:24:41Z"},
          "seven_day": {"utilization": 5.0, "resets_at": "2026-07-08T00:00:00Z"},
          "seven_day_opus": {"utilization": 40.0, "resets_at": "2026-07-08T00:00:00Z"}
        }
        """
        let response = try JSONDecoder().decode(ClaudeProvider.UsageResponse.self, from: Data(json.utf8))
        let usage = ClaudeProvider.usage(from: response, plan: "max", now: Date())
        XCTAssertEqual(usage.planName, "Max")
        XCTAssertEqual(usage.windows.map(\.label), ["5h limit", "Weekly limit", "Weekly (Opus)"])
        XCTAssertEqual(usage.worstWindow?.label, "Weekly (Opus)")
        XCTAssertEqual(usage.worstWindow?.usedPercent, 40.0)
        XCTAssertNotNil(usage.windows[0].resetsAt)
    }

    func testUsageMappingSkipsMissingWindows() throws {
        let json = #"{"five_hour": {"utilization": 3.0, "resets_at": "2026-07-01T20:24:41Z"}}"#
        let response = try JSONDecoder().decode(ClaudeProvider.UsageResponse.self, from: Data(json.utf8))
        let usage = ClaudeProvider.usage(from: response, plan: nil, now: Date())
        XCTAssertEqual(usage.windows.count, 1)
    }

    func testDecodeCredentialsWithoutRefreshTokenKeepsAccessToken() throws {
        let json = #"{"claudeAiOauth":{"accessToken":"at","expiresAt":4102444800000}}"#
        let creds = try XCTUnwrap(ClaudeProvider.decodeCredentials(Data(json.utf8)))
        XCTAssertNil(creds.refreshToken)
        XCTAssertFalse(creds.isExpired)
    }

    func testUsageMappingIncludesSonnetWeekly() throws {
        let json = #"{"seven_day": {"utilization": 5}, "seven_day_sonnet": {"utilization": 70, "resets_at": null}}"#
        let response = try JSONDecoder().decode(ClaudeProvider.UsageResponse.self, from: Data(json.utf8))
        let usage = ClaudeProvider.usage(from: response, plan: nil, now: Date())
        XCTAssertEqual(usage.windows.map(\.label), ["Weekly limit", "Weekly (Sonnet)"])
        XCTAssertEqual(usage.worstWindow?.usedPercent, 70)
    }

    // MARK: - Session cache

    private let sample = ClaudeProvider.UsageResponse(
        fiveHour: .init(utilization: 10, resetsAt: nil)
    )

    private func creds(_ refresh: String?, expired: Bool, access: String = "disk") -> ClaudeProvider.Credentials {
        ClaudeProvider.Credentials(
            accessToken: access,
            refreshToken: refresh,
            expiresAt: Date().addingTimeInterval(expired ? -10 : 3600),
            subscriptionType: "pro"
        )
    }

    func testExpiredTokenIsRefreshedOnceAndReused() async throws {
        let counter = Counter()
        let sample = self.sample
        let transport = ClaudeTransport(
            refresh: { _ in
                await counter.bump("refresh")
                return TokenGrant(accessToken: "fresh", refreshToken: "rt2", expiresIn: 3600)
            },
            usage: { token in
                XCTAssertEqual(token, "fresh")
                return sample
            }
        )
        let cache = ClaudeSessionCache()
        let stored = creds("rt", expired: true)
        _ = try await cache.usage(stored: stored, now: Date(), forceRefresh: true, transport: transport)
        _ = try await cache.usage(stored: stored, now: Date(), forceRefresh: true, transport: transport)
        let refreshes = await counter.count("refresh")
        XCTAssertEqual(refreshes, 1)
    }

    func testRecentUsageIsReusedUnlessForced() async throws {
        let counter = Counter()
        let sample = self.sample
        let transport = ClaudeTransport(
            refresh: { _ in XCTFail("no refresh expected"); throw ClaudeError.badResponse },
            usage: { _ in await counter.bump("usage"); return sample }
        )
        let cache = ClaudeSessionCache()
        let stored = creds("rt", expired: false)
        _ = try await cache.usage(stored: stored, now: Date(), forceRefresh: false, transport: transport)
        _ = try await cache.usage(stored: stored, now: Date(), forceRefresh: false, transport: transport)
        var calls = await counter.count("usage")
        XCTAssertEqual(calls, 1)
        _ = try await cache.usage(stored: stored, now: Date(), forceRefresh: true, transport: transport)
        calls = await counter.count("usage")
        XCTAssertEqual(calls, 2)
    }

    func testRateLimitBacksOffWithoutHittingNetwork() async throws {
        let counter = Counter()
        let transport = ClaudeTransport(
            refresh: { _ in throw ClaudeError.badResponse },
            usage: { _ in
                await counter.bump("usage")
                throw ClaudeError.rateLimited(retryAfter: 120)
            }
        )
        let cache = ClaudeSessionCache()
        let stored = creds("rt", expired: false)
        for _ in 0..<3 {
            do {
                _ = try await cache.usage(stored: stored, now: Date(), forceRefresh: true, transport: transport)
                XCTFail("expected rate limit")
            } catch let error as ClaudeError {
                guard case .rateLimited = error else { return XCTFail("unexpected \(error)") }
            }
        }
        let calls = await counter.count("usage")
        XCTAssertEqual(calls, 1)
    }

    func testRejectedRefreshTokenReportsExpiredAndBacksOff() async throws {
        let counter = Counter()
        let transport = ClaudeTransport(
            refresh: { _ in
                await counter.bump("refresh")
                throw ClaudeError.sessionExpired
            },
            usage: { _ in XCTFail("no usage expected"); throw ClaudeError.badResponse }
        )
        let cache = ClaudeSessionCache()
        let stored = creds("rt", expired: true)
        for _ in 0..<2 {
            do {
                _ = try await cache.usage(stored: stored, now: Date(), forceRefresh: true, transport: transport)
                XCTFail("expected failure")
            } catch let error as ClaudeError {
                XCTAssertEqual(error, .sessionExpired)
            }
        }
        var refreshes = await counter.count("refresh")
        XCTAssertEqual(refreshes, 1)

        // Signing in again (new stored credentials) clears the backoff.
        do {
            _ = try await cache.usage(stored: creds("rt-new", expired: true), now: Date(), forceRefresh: true, transport: transport)
        } catch {}
        refreshes = await counter.count("refresh")
        XCTAssertEqual(refreshes, 2)
    }

    func testUnauthorizedValidTokenRefreshesOnce() async throws {
        let counter = Counter()
        let sample = self.sample
        let transport = ClaudeTransport(
            refresh: { _ in
                await counter.bump("refresh")
                return TokenGrant(accessToken: "fresh", refreshToken: nil, expiresIn: 3600)
            },
            usage: { token in
                if token == "disk" { throw ClaudeError.unauthorized }
                return sample
            }
        )
        let cache = ClaudeSessionCache()
        let response = try await cache.usage(stored: creds("rt", expired: false), now: Date(), forceRefresh: true, transport: transport)
        XCTAssertEqual(response, sample)
        let refreshes = await counter.count("refresh")
        XCTAssertEqual(refreshes, 1)
    }

    func testExpiredTokenWithoutRefreshTokenIsSessionExpired() async {
        let transport = ClaudeTransport(
            refresh: { _ in XCTFail("no refresh token"); throw ClaudeError.badResponse },
            usage: { _ in XCTFail("no usage expected"); throw ClaudeError.badResponse }
        )
        do {
            _ = try await ClaudeSessionCache().usage(stored: creds(nil, expired: true), now: Date(), forceRefresh: true, transport: transport)
            XCTFail("expected failure")
        } catch {
            XCTAssertEqual(error as? ClaudeError, .sessionExpired)
        }
    }
}

private actor Counter {
    private var values: [String: Int] = [:]
    func bump(_ key: String) { values[key, default: 0] += 1 }
    func count(_ key: String) -> Int { values[key, default: 0] }
}
