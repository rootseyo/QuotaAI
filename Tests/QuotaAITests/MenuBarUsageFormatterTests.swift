import AppKit
import XCTest
@testable import QuotaAI

final class MenuBarUsageFormatterTests: XCTestCase {
    private func usage(_ windows: [UsageWindow]) -> ProviderUsage {
        ProviderUsage(planName: nil, windows: windows, asOf: nil)
    }
    private func window(_ label: String, _ used: Double) -> UsageWindow {
        UsageWindow(label: label, usedPercent: used, resetsAt: nil)
    }
    func testRemainingBounds() {
        for (used, expected) in [(0.0, 100), (100, 0), (99, 1), (1, 99), (-5, 100), (120, 0)] {
            XCTAssertEqual(MenuBarUsageFormatter.remaining(used), expected)
        }
        XCTAssertNil(MenuBarUsageFormatter.remaining(.nan))
    }
    func testCodexMappingAndMissingWindows() {
        let cases: [([UsageWindow], [String])] = [
            ([window(L("5h limit"), 25), window(L("Weekly limit"), 58)], ["75%", "42%"]),
            ([window(L("5h limit"), 25)], ["75%", "--"]),
            ([window(L("Weekly limit"), 58)], ["--", "42%"]),
            ([], ["--", "--"])
        ]
        for (windows, expected) in cases {
            let entry = MenuBarUsageFormatter.entry(id: "codex:account", code: "CxA", name: "Account", state: .ready(usage(windows)))
            XCTAssertEqual(entry.lines.map(\.text), expected)
        }
    }
    func testClaudeAndGeminiMapping() {
        let claude = MenuBarUsageFormatter.entry(id: "claude", code: "Cl", name: "Claude", state: .ready(usage([window(L("Weekly (Opus)"), 99), window(L("Weekly limit"), 40)])))
        XCTAssertEqual(claude.lines.map(\.text), ["--", "60%"])
        let gemini = MenuBarUsageFormatter.entry(id: "gemini", code: "Ge", name: "Gemini", state: .ready(usage([window(L("Flash (24h)"), 35)])))
        XCTAssertEqual(gemini.lines.map(\.text), ["Pro --", "Fl 65%"])
    }
    func testStaleAndAccessibility() {
        let previous = ProviderState.ready(usage([window(L("5h limit"), 25)]))
        let stale = ProviderState.nextState(after: previous, failure: "offline", at: Date())
        let entry = MenuBarUsageFormatter.entry(id: "codex", code: "Cx", name: "Codex", state: stale)
        XCTAssertTrue(entry.stale)
        XCTAssertEqual(entry.lines.first?.text, "75%")
        XCTAssertTrue(MenuBarUsageFormatter.accessibilityDescription([entry]).contains("5h"))
        XCTAssertTrue(MenuBarUsageFormatter.accessibilityDescription([entry]).contains("75%"))
        XCTAssertTrue(MenuBarUsageFormatter.accessibilityDescription([entry]).contains(L("data is stale")))
    }

    @MainActor func testRemainingColorsUseFiveBandsInBothAppearances() throws {
        let palette = RemainingQuotaColor()
        let bands = [76...100, 51...75, 26...50, 1...25, 0...0]
        for name in [NSAppearance.Name.aqua, .darkAqua] {
            try XCTUnwrap(NSAppearance(named: name)).performAsCurrentDrawingAppearance {
                let colors = bands.compactMap { band in
                    let upper = palette.color(for: band.upperBound).usingColorSpace(.sRGB)
                    let lower = palette.color(for: band.lowerBound).usingColorSpace(.sRGB)
                    XCTAssertEqual(upper, lower)
                    return upper
                }
                XCTAssertEqual(Set(colors).count, bands.count)
            }
        }
    }

    @MainActor func testIconsRenderInBothAppearancesAtStatusBarHeight() throws {
        XCTAssertNotNil(ProviderIcons.antigravity)
        for id in ["codex", "codex:work", "claude", "claude-api", "gemini"] {
            XCTAssertNotNil(ProviderIcons.image(for: id), "Missing icon for \(id)")
        }
        let entries = [("codex", "Cx"), ("gemini", "Ge"), ("claude", "Cl")].map { id, code in
            MenuBarUsageFormatter.entry(id: id, code: code, name: id,
                                       state: .ready(usage([window(L("5h limit"), 25)])))
        }
        for name in [NSAppearance.Name.aqua, .darkAqua] {
            try XCTUnwrap(NSAppearance(named: name)).performAsCurrentDrawingAppearance {
                let image = MenuBarTitleRenderer.twoLineImage(
                    entries: entries, colors: RemainingQuotaColor(), accessibilityDescription: "usage"
                )
                XCTAssertEqual(image.size.height, 20)
                XCTAssertGreaterThan(image.size.width, 70)
                XCTAssertNotNil(image.tiffRepresentation)
                XCTAssertEqual(image.accessibilityDescription, "usage")
            }
        }
    }

    @MainActor func testStylePersists() {
        let suite = "MenuBarUsageFormatterTests.\(UUID())"
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let settings = SettingsStore(defaults: defaults)
        XCTAssertEqual(settings.menuBarDisplayStyle, .twoLineRemaining)
        settings.menuBarDisplayStyle = .original
        XCTAssertEqual(SettingsStore(defaults: defaults).menuBarDisplayStyle, .original)
    }
}
