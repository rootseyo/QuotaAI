import SwiftUI

/// Graph hue identifies a service/model pool. Warning text and symbols retain
/// severity independently, so the graph doesn't lose its identity as quota falls.
enum UsageGraphPalette {
    enum Family: Equatable { case codex, base, argon, claudeGPT, other }

    static func family(providerID: String, label: String) -> Family {
        if providerID == "codex" || providerID.hasPrefix("codex:") { return .codex }
        if providerID == "gemini" {
            if label == L("Argon (weekly)") { return .argon }
            if label == L("Base model (5h)") || label == L("Base model (weekly)") { return .base }
            if label == L("Claude / GPT (5h)") || label == L("Claude / GPT (weekly)") { return .claudeGPT }
        }
        return .other
    }

    static func isWeekly(_ label: String) -> Bool {
        [L("Weekly limit"), L("Weekly (Opus)"), L("Base model (weekly)"),
         L("Argon (weekly)"), L("Claude / GPT (weekly)")].contains(label)
    }

    static func color(providerID: String, label: String) -> Color {
        let weekly = isWeekly(label)
        switch family(providerID: providerID, label: label) {
        case .codex: return weekly ? .teal : .mint
        case .base: return weekly ? .indigo : .blue
        case .argon: return .purple
        case .claudeGPT: return weekly ? Color(red: 0.85, green: 0.40, blue: 0.22) : .orange
        case .other:
            return ProviderBadge.color(for: providerID).opacity(weekly ? 0.75 : 1)
        }
    }
}
