import XCTest
@testable import QuotaAI

final class ClaudeCLIClientTests: XCTestCase {
    private let seoul = TimeZone(identifier: "Asia/Seoul")!

    private func date(_ y: Int, _ m: Int, _ d: Int, _ h: Int, _ min: Int = 0) -> Date {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = seoul
        return calendar.date(from: DateComponents(year: y, month: m, day: d, hour: h, minute: min))!
    }

    /// Captured from `claude -p /usage --output-format json` (Claude Code 2.1.332).
    private let sample = """
    You are currently using your subscription to power your Claude Code usage

    Current session: 25% used · resets Nov 28 at 9:40pm (Asia/Seoul)
    Current week (all models): 4% used · resets Dec 5 at 9am (Asia/Seoul)

    What's contributing to your usage?
    Approximate · sessions on this machine only · shares overlap

    Last 24h · 105 requests · 5 sessions
      38%  Start fresh when you switch tasks · run /clear
    """

    func testParsesSessionAndWeekly() {
        let now = date(2026, 11, 28, 15)
        let reading = ClaudeCLIClient.parseUsage(sample, now: now)
        XCTAssertEqual(reading.fiveHour?.usedPercent, 25)
        XCTAssertEqual(reading.fiveHour?.resetsAt, date(2026, 11, 28, 21, 40))
        XCTAssertEqual(reading.weekly?.usedPercent, 4)
        XCTAssertEqual(reading.weekly?.resetsAt, date(2026, 12, 5, 9))
        XCTAssertNil(reading.weeklyOpus)
        // "38%" tip lines have no "used" and are ignored.
        XCTAssertNil(reading.weeklySonnet)
    }

    func testParsesModelSpecificWeeklyAndMissingSession() {
        let text = """
        Current week (all models): 61% used · resets Jan 2 at 12am (Asia/Seoul)
        Current week (Sonnet only): 80.5% used · resets Jan 2 at 12pm (Asia/Seoul)
        Current week (Opus): 100% used
        """
        let reading = ClaudeCLIClient.parseUsage(text, now: date(2026, 12, 30, 10))
        XCTAssertNil(reading.fiveHour)
        XCTAssertEqual(reading.weekly?.resetsAt, date(2027, 1, 2, 0), "rolls over to next year")
        XCTAssertEqual(reading.weeklySonnet?.usedPercent, 80.5)
        XCTAssertEqual(reading.weeklySonnet?.resetsAt, date(2027, 1, 2, 12))
        XCTAssertEqual(reading.weeklyOpus?.usedPercent, 100)
        XCTAssertNil(reading.weeklyOpus?.resetsAt)
    }

    func testTimeOnlyResetIsTodayOrTomorrow() {
        let now = date(2026, 11, 28, 22)
        XCTAssertEqual(ClaudeCLIClient.resetDate(in: "resets 11pm (Asia/Seoul)", now: now), date(2026, 11, 28, 23))
        XCTAssertEqual(ClaudeCLIClient.resetDate(in: "resets 9:30am (Asia/Seoul)", now: now), date(2026, 11, 29, 9, 30))
    }

    func testUnrecognisedTextIsEmpty() {
        XCTAssertTrue(ClaudeCLIClient.parseUsage("/usage is only available for subscription plans", now: Date()).isEmpty)
    }

    func testUsageArgumentsDisableHooksAndPersistence() {
        XCTAssertTrue(ClaudeCLIClient.usageArguments.contains("--no-session-persistence"))
        XCTAssertTrue(ClaudeCLIClient.usageArguments.contains(#"{"disableAllHooks":true}"#))
    }

    /// Opt-in: runs the real `claude` CLI (no model call, no cost).
    func testLiveCLI() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["CLAUDE_CLI_LIVE_TEST"] == "1")
        let executable = try XCTUnwrap(ClaudeCLIClient.resolveExecutable())
        let reading = try await ClaudeCLIClient.readUsage(executable: executable)
        XCTAssertNotNil(reading.weekly)
        _ = try await ClaudeCLIClient.readPlan(executable: executable)
    }

    // MARK: - Provider wiring

    private func provider(cli: ClaudeCLISource) -> ClaudeProvider {
        var provider = ClaudeProvider()
        provider.cli = cli
        provider.cliCache = ClaudeCLICache()
        provider.cache = ClaudeSessionCache()
        provider.credentialsLoader = {
            ClaudeProvider.Credentials(accessToken: "oauth", expiresAt: Date().addingTimeInterval(3600), subscriptionType: "max")
        }
        provider.transport = ClaudeTransport(usage: { _ in
            ClaudeProvider.UsageResponse(sevenDay: .init(utilization: 50, resetsAt: nil))
        })
        return provider
    }

    func testCLIIsPreferredAndCached() async throws {
        let counter = CallCounter()
        let cli = ClaudeCLISource(
            executable: { URL(fileURLWithPath: "/usr/bin/true") },
            readUsage: { _ in
                await counter.bump()
                return .init(weekly: .init(usedPercent: 10, resetsAt: nil))
            },
            readPlan: { _ in "pro" }
        )
        let provider = provider(cli: cli)
        let usage = try await provider.fetch(forceRefresh: false)
        _ = try await provider.fetch(forceRefresh: false)
        XCTAssertEqual(usage.planName, "Pro")
        XCTAssertEqual(usage.windows.map(\.label), [L("Weekly limit")])
        XCTAssertEqual(usage.windows.first?.usedPercent, 10)
        let calls = await counter.value
        XCTAssertEqual(calls, 1, "second fetch within the poll interval reuses the CLI reading")
    }

    func testFallsBackToOAuthWhenCLIFails() async throws {
        let cli = ClaudeCLISource(
            executable: { URL(fileURLWithPath: "/usr/bin/true") },
            readUsage: { _ in throw ClaudeCLIClient.CLIError.parseFailed },
            readPlan: { _ in nil }
        )
        let usage = try await provider(cli: cli).fetch(forceRefresh: true)
        XCTAssertEqual(usage.planName, "Max")
        XCTAssertEqual(usage.windows.first?.usedPercent, 50)
    }

    func testFallsBackToOAuthWhenCLIMissing() async throws {
        let cli = ClaudeCLISource(
            executable: { nil },
            readUsage: { _ in XCTFail("not installed"); throw ClaudeCLIClient.CLIError.notInstalled },
            readPlan: { _ in nil }
        )
        let usage = try await provider(cli: cli).fetch(forceRefresh: true)
        XCTAssertEqual(usage.windows.first?.usedPercent, 50)
    }
}

private actor CallCounter {
    var value = 0
    func bump() { value += 1 }
}
