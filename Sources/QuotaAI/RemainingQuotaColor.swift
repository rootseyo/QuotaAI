import AppKit

struct RemainingQuotaColor {
    enum Appearance: String, CaseIterable {
        case light, dark

        var label: String {
            switch self {
            case .light: L("Light appearance")
            case .dark: L("Dark appearance")
            }
        }
    }

    enum Band: String, CaseIterable {
        case high, good, medium, low, empty

        init(remaining: Int) {
            switch remaining {
            case 76...: self = .high
            case 51...: self = .good
            case 26...: self = .medium
            case 1...: self = .low
            default: self = .empty
            }
        }

        var label: String {
            switch self {
            case .high: L("76–100%")
            case .good: L("51–75%")
            case .medium: L("26–50%")
            case .low: L("1–25%")
            case .empty: L("0%")
            }
        }

        func defaultRGB(for appearance: Appearance) -> Int {
            switch (self, appearance) {
            case (.high, .light): 0x075735
            case (.high, .dark): 0x82F3B2
            case (.good, .light): 0x00378D
            case (.good, .dark): 0xA5D3FF
            case (.medium, .light): 0x604600
            case (.medium, .dark): 0xFFE083
            case (.low, .light): 0x8A2C00
            case (.low, .dark): 0xFFAD78
            case (.empty, .light): 0xA0102D
            case (.empty, .dark): 0xFF92A9
            }
        }
    }

    private(set) var overrides: [String: Int]

    init(overrides: [String: Int] = [:]) {
        self.overrides = overrides.filter { (0...0xFFFFFF).contains($0.value) }
    }

    func color(for remaining: Int) -> NSColor {
        let band = Band(remaining: remaining)
        return NSColor(name: nil) { appearance in
            let dark = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            return color(for: band, appearance: dark ? .dark : .light)
        }
    }

    func color(for band: Band, appearance: Appearance) -> NSColor {
        let rgb = overrides[key(for: band, appearance: appearance)] ?? band.defaultRGB(for: appearance)
        return NSColor(
            srgbRed: CGFloat(rgb >> 16 & 255) / 255,
            green: CGFloat(rgb >> 8 & 255) / 255,
            blue: CGFloat(rgb & 255) / 255,
            alpha: 1
        )
    }

    mutating func setColor(_ color: NSColor, for band: Band, appearance: Appearance) {
        guard let color = color.usingColorSpace(.sRGB) else { return }
        let channels = [color.redComponent, color.greenComponent, color.blueComponent]
        guard channels.allSatisfy(\.isFinite) else { return }
        let rgb = channels.reduce(0) { packed, value in
            packed << 8 | Int((min(1, max(0, value)) * 255).rounded())
        }
        overrides[key(for: band, appearance: appearance)] = rgb == band.defaultRGB(for: appearance) ? nil : rgb
    }

    private func key(for band: Band, appearance: Appearance) -> String {
        "\(appearance.rawValue).\(band.rawValue)"
    }
}
