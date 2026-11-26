import SwiftUI

/// A provider's quota bars share a hue. Shade distinguishes its windows and pools.
enum UsageGraphPalette {
    enum Family: Equatable { case codex, claude, gemini, other }

    static func family(providerID: String) -> Family {
        switch providerID {
        case "codex": .codex
        case let id where id.hasPrefix("codex:"): .codex
        case "claude", "claude-api": .claude
        case "gemini": .gemini
        default: .other
        }
    }

    static func isWeekly(_ label: String) -> Bool {
        [L("Weekly limit"), L("Weekly (Opus)"), L("Base model (weekly)"),
         L("Argon (weekly)"), L("Claude / GPT (weekly)")].contains(label)
    }

    static func color(providerID: String, label: String) -> Color {
        let weekly = isWeekly(label)
        switch family(providerID: providerID) {
        case .codex:
            return rgb(weekly ? 0x4D87DA : 0x1E64DA)
        case .claude:
            if label == L("Weekly (Opus)") { return rgb(0xBF5718) }
            return rgb(weekly ? 0xCE6B32 : 0xE0782C)
        case .gemini:
            switch label {
            case L("Argon (weekly)"):
                return rgb(0x9C65BF)
            case L("Claude / GPT (5h)"), L("Claude / GPT (weekly)"):
                return rgb(weekly ? 0xA164D6 : 0x8855C4)
            default:
                return rgb(weekly ? 0x9C75DF : 0x814BD1)
            }
        case .other:
            return ProviderBadge.color(for: providerID).opacity(weekly ? 0.75 : 1)
        }
    }

    private static func rgb(_ value: UInt32) -> Color {
        Color(
            red: Double(value >> 16 & 255) / 255,
            green: Double(value >> 8 & 255) / 255,
            blue: Double(value & 255) / 255
        )
    }
}
