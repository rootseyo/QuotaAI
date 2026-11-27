import XCTest
@testable import QuotaAI

final class MenuBarMissingWindowTests: XCTestCase {
    func testPlanWithoutFiveHourWindowShowsWeeklyOnly() {
        let state = ProviderState.ready(ProviderUsage(
            planName: "Pro",
            windows: [UsageWindow(label: L("Weekly limit"), usedPercent: 30, resetsAt: nil)],
            asOf: nil
        ))
        let entry = MenuBarUsageFormatter.entry(id: "claude", code: "Cl", name: "Claude", state: state)
        XCTAssertEqual(entry.lines.map(\.label), ["7d"])
        XCTAssertEqual(entry.lines.first?.remaining, 70)
    }

    func testBothWindowsShowTwoLines() {
        let state = ProviderState.ready(ProviderUsage(
            planName: nil,
            windows: [
                UsageWindow(label: L("5h limit"), usedPercent: 10, resetsAt: nil),
                UsageWindow(label: L("Weekly limit"), usedPercent: 30, resetsAt: nil),
            ],
            asOf: nil
        ))
        let entry = MenuBarUsageFormatter.entry(id: "codex", code: "Cx", name: "Codex", state: state)
        XCTAssertEqual(entry.lines.map(\.label), ["5h", "7d"])
    }

    func testLoadingKeepsPlaceholders() {
        let entry = MenuBarUsageFormatter.entry(id: "codex", code: "Cx", name: "Codex", state: .loading)
        XCTAssertEqual(entry.lines.map(\.text), ["--", "--"])
    }
}
