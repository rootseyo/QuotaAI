import AppKit
import SwiftUI

struct MenuContent: View {
    @ObservedObject var store: UsageStore
    @ObservedObject var settings: SettingsStore
    @Environment(\.openWindow) private var openWindow
    @State private var menuScreenVisibleHeight: CGFloat?

    private let tipJarURL = URL(string: "https://buymeacoffee.com/seyo")!

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            usageView
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .frame(width: 320, alignment: .topLeading)
        .fixedSize(horizontal: false, vertical: true)
        .background {
            MenuWindowScreenReader { height in
                DispatchQueue.main.async {
                    guard menuScreenVisibleHeight != height else { return }
                    menuScreenVisibleHeight = height
                }
            }
        }
    }

    private var usageView: some View {
        VStack(alignment: .leading, spacing: 12) {
            MenuProviderScrollView(maxHeight: providerMaximumHeight) {
                ProviderUsageSections(store: store, settings: settings)
            }
            Divider()
            footer
        }
    }

    private var providerMaximumHeight: CGFloat {
        max(1, min(680, (menuScreenVisibleHeight ?? 820) - 150))
    }

    private var footer: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                if let refreshed = store.lastRefreshed {
                    Text(L("Updated \(refreshed.formatted(date: .omitted, time: .shortened))"))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Button {
                    store.refresh(forceRefresh: true)
                } label: {
                    Label(L("Refresh"), systemImage: "arrow.clockwise")
                        .labelStyle(.titleAndIcon)
                }
                .buttonStyle(.plain)
                .font(.caption)
                .help(L("Refresh"))
                .accessibilityLabel(L("Refresh"))
            }
            HStack {
                Button {
                    openUsageDetails()
                } label: {
                    Text(L("Usage Details"))
                }
                .buttonStyle(.plain)
                .font(.caption)
                .keyboardShortcut("d", modifiers: [.command])
                .help(L("Usage Details"))
                .accessibilityLabel(L("Usage Details"))
                SettingsLink {
                    Text(L("Settings…"))
                }
                .buttonStyle(.plain)
                .font(.caption)
                .help(L("Settings…"))
                .accessibilityLabel(L("Settings…"))
                .simultaneousGesture(TapGesture().onEnded {
                    NSApp.activate(ignoringOtherApps: true)
                    Self.closeMenuBarWindow()
                })
                Spacer()
            }
            HStack {
                Button(L("Check for Updates…")) {
                    Self.closeMenuBarWindow()
                    Updater.shared.checkForUpdates()
                }
                .buttonStyle(.plain)
                .font(.caption)
                .help(L("Check for Updates…"))
                .accessibilityLabel(L("Check for Updates…"))
                Spacer()
                Link(destination: tipJarURL) {
                    Text(L("Support ♥"))
                }
                .font(.caption)
                .help(L("Support ♥"))
                .accessibilityLabel(L("Support ♥"))
                Spacer()
                Button(L("Quit")) { NSApp.terminate(nil) }
                    .buttonStyle(.plain)
                    .font(.caption)
                    .help(L("Quit"))
                    .accessibilityLabel(L("Quit"))
            }
        }
    }

    private func openUsageDetails() {
        Self.closeMenuBarWindow()
        openWindow(id: "usage-details")
        NSApp.activate(ignoringOtherApps: true)
    }

    /// MenuBarExtra's window doesn't auto-dismiss when another window opens;
    /// close it explicitly so Settings doesn't appear behind the dropdown.
    private static func closeMenuBarWindow() {
        for window in NSApp.windows where window.className.contains("MenuBarExtraWindow") {
            window.close()
        }
    }
}

/// Size the viewport from the hosted content in the same layout pass. SwiftUI
/// ScrollView + GeometryReader can retain the initial tall popup proposal after
/// its content shrinks. Explicit AppKit sizing also disables automatic top insets.
private struct MenuProviderScrollView<Content: View>: NSViewRepresentable {
    let maxHeight: CGFloat
    @ViewBuilder let content: () -> Content

    func makeNSView(context: Context) -> ContentScrollView {
        ContentScrollView()
    }

    func updateNSView(_ view: ContentScrollView, context: Context) {
        view.host.rootView = AnyView(content().frame(width: 292, alignment: .topLeading)
            .fixedSize(horizontal: false, vertical: true))
        view.updateDocumentSize()
        view.invalidateIntrinsicContentSize()
    }

    func sizeThatFits(_ proposal: ProposedViewSize, nsView: ContentScrollView, context: Context) -> CGSize? {
        nsView.updateDocumentSize()
        return CGSize(width: 292, height: min(maxHeight, nsView.host.fittingSize.height))
    }

    final class ContentScrollView: NSScrollView {
        let host = NSHostingView(rootView: AnyView(EmptyView()))

        override init(frame: NSRect) {
            super.init(frame: frame)
            drawsBackground = false
            borderType = .noBorder
            hasVerticalScroller = true
            autohidesScrollers = true
            scrollerStyle = .overlay
            automaticallyAdjustsContentInsets = false
            contentInsets = NSEdgeInsets(top: 0, left: 0, bottom: 0, right: 0)
            contentView = TopAlignedClipView()
            contentView.drawsBackground = false
            documentView = host
        }

        required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

        func updateDocumentSize() {
            host.setFrameSize(NSSize(width: 292, height: host.fittingSize.height))
        }
    }

    final class TopAlignedClipView: NSClipView {
        override var isFlipped: Bool { true }
    }
}

/// Reports the visible height of the display that owns MenuBarExtra's popup.
/// The popup window is created after the initial SwiftUI layout, so this is a
/// zero-size probe rather than a GeometryReader that could participate in a
/// layout feedback loop.
private struct MenuWindowScreenReader: NSViewRepresentable {
    let onVisibleHeightChange: (CGFloat) -> Void

    func makeNSView(context: Context) -> ScreenProbeView {
        ScreenProbeView(onVisibleHeightChange: onVisibleHeightChange)
    }

    func updateNSView(_ nsView: ScreenProbeView, context: Context) {
        nsView.onVisibleHeightChange = onVisibleHeightChange
    }

    final class ScreenProbeView: NSView {
        var onVisibleHeightChange: (CGFloat) -> Void
        private weak var observedWindow: NSWindow?
        private var lastReportedHeight: CGFloat?

        init(onVisibleHeightChange: @escaping (CGFloat) -> Void) {
            self.onVisibleHeightChange = onVisibleHeightChange
            super.init(frame: .zero)
        }

        required init?(coder: NSCoder) {
            fatalError("init(coder:) has not been implemented")
        }

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            if observedWindow !== window {
                removeObservers()
                observedWindow = window
                if let window {
                    NotificationCenter.default.addObserver(
                        self,
                        selector: #selector(windowScreenChanged),
                        name: NSWindow.didChangeScreenNotification,
                        object: window
                    )
                }
                NotificationCenter.default.addObserver(
                    self,
                    selector: #selector(windowScreenChanged),
                    name: NSApplication.didChangeScreenParametersNotification,
                    object: nil
                )
            }
            reportVisibleHeight()
        }

        deinit {
            removeObservers()
        }

        @objc private func windowScreenChanged() {
            reportVisibleHeight()
        }

        func reportVisibleHeight() {
            guard let height = window?.screen?.visibleFrame.height else { return }
            guard lastReportedHeight != height else { return }
            lastReportedHeight = height
            onVisibleHeightChange(height)
        }

        private func removeObservers() {
            NotificationCenter.default.removeObserver(self)
            observedWindow = nil
        }
    }
}
