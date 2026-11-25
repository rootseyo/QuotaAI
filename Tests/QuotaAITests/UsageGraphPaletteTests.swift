import XCTest
@testable import QuotaAI

final class UsageGraphPaletteTests: XCTestCase {
    func testServicesAndQuotaPoolsHaveDistinctFamilies() {
        XCTAssertEqual(UsageGraphPalette.family(providerID: "codex", label: L("5h limit")), .codex)
        XCTAssertEqual(UsageGraphPalette.family(providerID: "codex:extra", label: L("Weekly limit")), .codex)
        XCTAssertEqual(UsageGraphPalette.family(providerID: "gemini", label: L("Base model (5h)")), .base)
        XCTAssertEqual(UsageGraphPalette.family(providerID: "gemini", label: L("Argon (weekly)")), .argon)
        XCTAssertEqual(UsageGraphPalette.family(providerID: "gemini", label: L("Claude / GPT (5h)")), .claudeGPT)
        XCTAssertTrue(UsageGraphPalette.isWeekly(L("Base model (weekly)")))
        XCTAssertFalse(UsageGraphPalette.isWeekly(L("Base model (5h)")))
        XCTAssertNotEqual(UsageGraphPalette.color(providerID: "codex", label: L("5h limit")),
                          UsageGraphPalette.color(providerID: "codex", label: L("Weekly limit")))
    }
}
