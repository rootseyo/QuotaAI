import XCTest
@testable import QuotaAI

final class AntigravityProviderTests: XCTestCase {
    func testSummarySeparatesGeminiFromClaudeAndGPTAndPreservesExtraGroups() throws {
        let response = try JSONDecoder().decode(AntigravityProvider.QuotaResponse.self, from: Data(#"{"groups":[{"displayName":"Gemini Models","buckets":[{"bucketId":"gemini-weekly","window":"weekly","remainingFraction":0.42},{"bucketId":"gemini-5h","window":"5h","remainingFraction":0.75}]},{"displayName":"Claude and GPT models","buckets":[{"bucketId":"3p-5h","displayName":"Five Hour Limit Remaining","remainingFraction":0.01}]},{"displayName":"Future model","buckets":[{"bucketId":"future","displayName":"Weekly","remainingFraction":null}]}]}"#.utf8))
        let usage = try AntigravityProvider.usage(from: response, now: Date())
        XCTAssertEqual(usage.windows.count, 3)
        let entry = MenuBarUsageFormatter.entry(id: "gemini", code: "Ag", name: "Antigravity", state: .ready(usage))
        XCTAssertEqual(entry.lines.map(\.text), ["75%", "42%"])
        XCTAssertEqual(usage.windows.last?.usedPercent, 99)
    }

    func testBaseAndArgonAreSeparateQuotaWindows() throws {
        let data = Data(#"{"groups":[{"displayName":"Gemini Models","buckets":[{"bucketId":"gemini-5h","remainingFraction":0.75},{"bucketId":"gemini-weekly","remainingFraction":0.42}]},{"displayName":"Gemini 4 Argon","buckets":[{"bucketId":"gemini-argon-wtu-weekly","remainingFraction":0.13,"resetTime":"2026-11-12T00:00:00Z"}]}]}"#.utf8)
        let response = try JSONDecoder().decode(AntigravityProvider.QuotaResponse.self, from: data)
        let usage = try AntigravityProvider.usage(from: response, now: Date())
        XCTAssertEqual(usage.windows.map(\.label), [L("Base model (5h)"), L("Base model (weekly)"), L("Argon (weekly)")])
        XCTAssertEqual(usage.windows.map { MenuBarUsageFormatter.remaining($0.usedPercent) }, [75, 42, 13])
        XCTAssertNotNil(usage.windows.last?.resetsAt)
        let entry = MenuBarUsageFormatter.entry(id: "gemini", code: "Ag", name: "Antigravity", state: .ready(usage))
        XCTAssertEqual(entry.lines.map(\.text), ["75%", "42%"])
    }

    func testGroupOrderIgnoresServerOrder() throws {
        let data = Data(#"{"groups":[{"displayName":"Claude and GPT models","buckets":[{"bucketId":"3p-weekly","remainingFraction":0.2},{"bucketId":"3p-5h","remainingFraction":0.3}]},{"displayName":"Gemini 4 Argon","buckets":[{"bucketId":"gemini-argon-wtu-weekly","remainingFraction":0.4}]},{"displayName":"Gemini Models","buckets":[{"bucketId":"gemini-weekly","remainingFraction":0.5},{"bucketId":"gemini-5h","remainingFraction":0.6}]}]}"#.utf8)
        let response = try JSONDecoder().decode(AntigravityProvider.QuotaResponse.self, from: data)
        let usage = try AntigravityProvider.usage(from: response, now: Date())
        XCTAssertEqual(usage.windows.map(\.label), [L("Base model (5h)"), L("Base model (weekly)"), L("Argon (weekly)"), L("Claude / GPT (5h)"), L("Claude / GPT (weekly)")])
        XCTAssertEqual(usage.windows.map { MenuBarUsageFormatter.remaining($0.usedPercent) }, [60, 50, 40, 30, 20])
    }

    func testMissingGeminiQuotaDoesNotUseThirdPartyQuota() throws {
        let response = try JSONDecoder().decode(AntigravityProvider.QuotaResponse.self, from: Data(#"{"groups":[{"displayName":"Claude and GPT models","buckets":[{"bucketId":"3p-5h","remainingFraction":1}]}]}"#.utf8))
        let usage = try AntigravityProvider.usage(from: response, now: Date())
        let entry = MenuBarUsageFormatter.entry(id: "gemini", code: "Ag", name: "Antigravity", state: .ready(usage))
        XCTAssertEqual(entry.lines.map(\.text), ["--", "--"])
    }

    func testEmptyQuotaIsUnavailable() throws {
        let response = try JSONDecoder().decode(AntigravityProvider.QuotaResponse.self, from: Data("{}".utf8))
        XCTAssertThrowsError(try AntigravityProvider.usage(from: response, now: Date()))
    }

    func testDecodesPlainAndGoKeyringEncodedCredentialPayloads() throws {
        let rawJSON = #"{"token":{"access_token":"tok-1","refresh_token":"ref-1","expiry":"2026-11-07T19:15:59Z"}}"#
        let plain = AntigravityProvider.decodeCredentialFile(Data(rawJSON.utf8))
        XCTAssertEqual(plain?.token.access_token, "tok-1")
        XCTAssertEqual(plain?.token.refresh_token, "ref-1")

        let base64Wrapped = "go-keyring-base64:" + Data(rawJSON.utf8).base64EncodedString() + "\n"
        let fromBase64 = AntigravityProvider.decodeCredentialFile(Data(base64Wrapped.utf8))
        XCTAssertEqual(fromBase64?.token.access_token, "tok-1")
        XCTAssertEqual(fromBase64?.token.refresh_token, "ref-1")

        let hexWrapped = "go-keyring-encoded:" + Data(rawJSON.utf8).map { String(format: "%02x", $0) }.joined()
        let fromHex = AntigravityProvider.decodeCredentialFile(Data(hexWrapped.utf8))
        XCTAssertEqual(fromHex?.token.access_token, "tok-1")
        XCTAssertEqual(fromHex?.token.refresh_token, "ref-1")
    }

    func testLiveFetchWhenEnabled() async throws {
        guard ProcessInfo.processInfo.environment["ANTIGRAVITY_LIVE_TEST"] == "1" else {
            throw XCTSkip("Set ANTIGRAVITY_LIVE_TEST=1 to verify Antigravity quota access")
        }
        let usage = try await AntigravityProvider().fetch()
        XCTAssertFalse(usage.windows.isEmpty)
        XCTAssertTrue(usage.windows.allSatisfy { (0...100).contains($0.usedPercent) })
    }
}
