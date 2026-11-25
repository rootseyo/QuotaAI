import AppKit
import SwiftUI
import XCTest
@testable import QuotaAI

final class APIUsagePresentationTests: XCTestCase {
    @MainActor
    func testAPIUsageFitsMenuWidthAndRepeatedSummariesKeepFooterReachable() async throws {
        let short = await makeMenu(providerCount: 1)
        let repeated = await makeMenu(providerCount: 16)
        defer {
            short.window.close()
            repeated.window.close()
        }

        XCTAssertLessThanOrEqual(short.host.fittingSize.width, 320)
        XCTAssertLessThanOrEqual(repeated.host.fittingSize.width, 320)
        XCTAssertLessThan(short.host.fittingSize.height, 420)

        let shortScroll = try XCTUnwrap(findScrollView(in: short.host))
        let shortDocument = try XCTUnwrap(shortScroll.documentView)
        XCTAssertEqual(shortScroll.contentInsets.top, 0)
        XCTAssertEqual(shortScroll.contentInsets.bottom, 0)
        XCTAssertEqual(shortScroll.contentView.bounds.minY, 0, accuracy: 1)
        XCTAssertEqual(shortDocument.frame.minY, 0, accuracy: 1)
        XCTAssertEqual(shortScroll.contentView.bounds.height, shortDocument.frame.height, accuracy: 2,
                       "Short menus must fit content without an empty viewport above or below it")

        let scroll = try XCTUnwrap(findScrollView(in: repeated.host))
        let document = try XCTUnwrap(scroll.documentView)
        XCTAssertGreaterThan(document.frame.height, scroll.contentView.bounds.height)

        // The fixed footer remains outside the provider viewport when many
        // multi-row API summaries are present, and the provider list scrolls.
        let viewport = repeated.host.convert(scroll.bounds, from: scroll)
        XCTAssertGreaterThan(repeated.host.bounds.height - viewport.height, 50)
        scroll.contentView.scroll(to: NSPoint(x: 0, y: document.frame.height))
        scroll.reflectScrolledClipView(scroll.contentView)
        XCTAssertGreaterThan(scroll.contentView.bounds.origin.y, 0)
        XCTAssertEqual(repeated.host.convert(scroll.bounds, from: scroll), viewport)
    }

    @MainActor
    private func makeMenu(providerCount: Int) async -> (host: NSHostingView<MenuContent>, window: NSWindow) {
        _ = NSApplication.shared
        let suite = "APIUsagePresentationTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        let settings = SettingsStore(defaults: defaults)
        settings.setMode(.off, for: "codex")
        let providers = (0..<providerCount).map { APIUsageLayoutProvider(id: "api-layout-\($0)") }
        let store = UsageStore(settings: settings, providers: providers)
        let host = NSHostingView(rootView: MenuContent(store: store, settings: settings))
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 320, height: 750),
                              styleMask: .borderless, backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        for _ in 0..<8 {
            host.setFrameSize(host.fittingSize)
            host.layoutSubtreeIfNeeded()
            try? await Task.sleep(nanoseconds: 20_000_000)
        }
        defaults.removePersistentDomain(forName: suite)
        return (host, window)
    }

    @MainActor
    private func findScrollView(in view: NSView) -> NSScrollView? {
        if let scroll = view as? NSScrollView { return scroll }
        return view.subviews.lazy.compactMap { self.findScrollView(in: $0) }.first
    }
}

private struct APIUsageLayoutProvider: UsageProvider {
    let id: String
    var displayName: String { "Claude API" }
    var shortCode: String { "CA" }
    var isDetected: Bool { true }

    func fetch() async throws -> ProviderUsage {
        let now = Date()
        return ProviderUsage(
            planName: nil,
            windows: [],
            asOf: now,
            balance: BalanceInfo(remaining: 1.25, used: nil, currencySymbol: "$", kind: .spent),
            apiUsage: APIUsageSummary(
                costUSD: 1234.56,
                inputTokens: 123_456_789,
                outputTokens: 98_765_432,
                cacheReadTokens: 45_678_901,
                cacheCreationTokens: 12_345_678,
                periodStart: now,
                periodEnd: now.addingTimeInterval(-86_400)
            )
        )
    }
}
