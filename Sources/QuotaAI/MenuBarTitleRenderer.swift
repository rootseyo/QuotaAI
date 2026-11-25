import AppKit

enum MenuBarSeverity { case normal, warning, critical, stale }

@MainActor
enum MenuBarTitleRenderer {
    nonisolated static func severity(
        for state: ProviderState,
        balanceThreshold: Double
    ) -> MenuBarSeverity {
        switch state {
        case .stale:
            return .stale
        case .loading:
            return .normal
        case .error:
            return .critical
        case .ready(let usage):
            if let worst = usage.worstWindow {
                return menuBarSeverity(from: UsageMeterSeverity.forUsedPercent(worst.usedPercent))
            }
            if let balance = usage.balance, balance.kind == .remaining {
                return balance.remaining < balanceThreshold ? .warning : .normal
            }
            return .normal
        }
    }

    static func nsColor(for severity: MenuBarSeverity) -> NSColor {
        switch severity {
        case .normal: .labelColor
        case .warning: .systemYellow
        case .critical: .systemRed
        case .stale: .secondaryLabelColor
        }
    }

    nonisolated private static func menuBarSeverity(from meter: UsageMeterSeverity) -> MenuBarSeverity {
        switch meter {
        case .normal: .normal
        case .warning: .warning
        case .critical: .critical
        }
    }

    static func image(
        entries: [(text: String, severity: MenuBarSeverity)],
        accessibilityDescription: String
    ) -> NSImage {
        let font = NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .regular)
        let attributed = NSMutableAttributedString()
        for (index, entry) in entries.enumerated() {
            if index > 0 {
                attributed.append(NSAttributedString(
                    string: " · ",
                    attributes: [.font: font, .foregroundColor: NSColor.tertiaryLabelColor]
                ))
            }
            attributed.append(NSAttributedString(
                string: entry.text,
                attributes: [.font: font, .foregroundColor: nsColor(for: entry.severity)]
            ))
        }
        let size = attributed.size()
        let image = NSImage(size: NSSize(width: ceil(size.width), height: ceil(size.height)), flipped: false) { _ in
            attributed.draw(at: .zero)
            return true
        }
        image.isTemplate = false
        image.accessibilityDescription = accessibilityDescription
        return image
    }

    static func twoLineImage(entries: [MenuBarUsageFormatter.Entry], accessibilityDescription: String) -> NSImage {
        guard !entries.isEmpty else {
            return image(entries: [("QuotaAI", .normal)], accessibilityDescription: accessibilityDescription)
        }
        let font = NSFont.monospacedDigitSystemFont(ofSize: 9, weight: .regular)
        let badgeFont = NSFont.systemFont(ofSize: 9, weight: .semibold)
        let lineHeight: CGFloat = 10
        let badgeWidth: CGFloat = 20
        let gap: CGFloat = 3
        let spacing: CGFloat = 7
        let widths = entries.map { entry in
            max(12, entry.lines.map { ($0.text as NSString).size(withAttributes: [.font: font]).width }.max() ?? 0)
        }
        let width = zip(entries, widths).reduce(CGFloat(0)) { $0 + badgeWidth + gap + ceil($1.1) } + spacing * CGFloat(entries.count - 1)
        let size = NSSize(width: ceil(width), height: 20)
        let image = NSImage(size: size, flipped: true) { _ in
            var x: CGFloat = 0
            for (index, entry) in entries.enumerated() {
                let icon = entry.id == "gemini" && entry.code == "Ag"
                    ? ProviderIcons.antigravity : ProviderIcons.image(for: entry.id)
                if let icon {
                    icon.draw(in: NSRect(x: x + 1, y: 1, width: 18, height: 18),
                              from: .zero, operation: .sourceOver, fraction: 1,
                              respectFlipped: true, hints: nil)
                } else {
                    let code = entry.code as NSString
                    let codeSize = code.size(withAttributes: [.font: badgeFont])
                    code.draw(at: NSPoint(x: x + (badgeWidth - codeSize.width) / 2, y: (20 - codeSize.height) / 2),
                              withAttributes: [.font: badgeFont, .foregroundColor: NSColor.labelColor])
                }
                x += badgeWidth + gap
                for (lineIndex, line) in entry.lines.enumerated() {
                    let color: NSColor
                    if entry.stale || line.remaining == nil { color = .secondaryLabelColor }
                    else if line.remaining == 0 { color = .systemRed }
                    else if line.remaining! < 20 { color = .systemOrange }
                    else if line.remaining! <= 50 { color = .systemYellow }
                    else { color = .labelColor }
                    let y = entry.lines.count == 1 ? (20 - lineHeight) / 2 : CGFloat(lineIndex) * lineHeight
                    (line.text as NSString).draw(at: NSPoint(x: x, y: y), withAttributes: [.font: font, .foregroundColor: color])
                }
                x += ceil(widths[index]) + spacing
            }
            return true
        }
        image.isTemplate = false
        image.accessibilityDescription = accessibilityDescription
        return image
    }

    static func iconImage(severity: MenuBarSeverity, accessibilityDescription: String) -> NSImage {
        let config = NSImage.SymbolConfiguration(pointSize: 12, weight: .regular)
        guard let symbol = NSImage(systemSymbolName: "gauge.with.needle", accessibilityDescription: nil)?
            .withSymbolConfiguration(config) else {
            return NSImage(size: NSSize(width: 14, height: 14))
        }
        let color = nsColor(for: severity)
        let size = symbol.size
        let image = NSImage(size: size, flipped: false) { _ in
            color.set()
            symbol.draw(in: NSRect(origin: .zero, size: size))
            return true
        }
        image.isTemplate = false
        image.accessibilityDescription = accessibilityDescription
        return image
    }
}
