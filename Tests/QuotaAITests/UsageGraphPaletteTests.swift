import XCTest
@testable import QuotaAI

final class UsageGraphPaletteTests: XCTestCase {
    func testProvidersHaveDistinctFamilies() {
        XCTAssertEqual(UsageGraphPalette.family(providerID: "codex"), .codex)
        XCTAssertEqual(UsageGraphPalette.family(providerID: "codex:extra"), .codex)
        XCTAssertEqual(UsageGraphPalette.family(providerID: "claude"), .claude)
        XCTAssertEqual(UsageGraphPalette.family(providerID: "claude-api"), .claude)
        XCTAssertEqual(UsageGraphPalette.family(providerID: "gemini"), .gemini)
        XCTAssertEqual(UsageGraphPalette.family(providerID: "cursor"), .other)
    }

    func testWindowsUseShadesWithinTheirProviderPalette() {
        let shortLimit = L("5h limit")
        let weeklyLimit = L("Weekly limit")
        XCTAssertEqual(UsageGraphPalette.color(providerID: "codex", label: shortLimit),
                       UsageGraphPalette.color(providerID: "codex:extra", label: shortLimit))
        XCTAssertNotEqual(UsageGraphPalette.color(providerID: "codex", label: shortLimit),
                          UsageGraphPalette.color(providerID: "codex", label: weeklyLimit))
        XCTAssertNotEqual(UsageGraphPalette.color(providerID: "claude", label: weeklyLimit),
                          UsageGraphPalette.color(providerID: "claude", label: L("Weekly (Opus)")))

        let geminiLabels = ["Base model (5h)", "Base model (weekly)", "Argon (weekly)",
                            "Claude / GPT (5h)", "Claude / GPT (weekly)"] as [String.LocalizationValue]
        let geminiColors = geminiLabels.map { UsageGraphPalette.color(providerID: "gemini", label: L($0)) }
        XCTAssertEqual(Set(geminiColors).count, geminiLabels.count)
        XCTAssertTrue(UsageGraphPalette.isWeekly(L("Base model (weekly)")))
        XCTAssertFalse(UsageGraphPalette.isWeekly(L("Base model (5h)")))
    }
}
