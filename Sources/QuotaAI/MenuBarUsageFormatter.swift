import Foundation

/// Maps provider-owned window labels to the two values shown in the status bar.
enum MenuBarUsageFormatter {
    struct Line: Equatable {
        let label: String
        let remaining: Int?
        let showsLabel: Bool

        var text: String {
            let value = remaining.map { "\($0)%" } ?? "--"
            return showsLabel ? "\(label) \(value)" : value
        }
    }
    struct Entry: Equatable {
        let id: String
        let code: String
        let name: String
        let lines: [Line]
        let stale: Bool
        let unavailable: Bool
    }

    static func remaining(_ used: Double) -> Int? {
        guard used.isFinite else { return nil }
        return Int(max(0, min(100, CountDirection.remaining.displayPercent(used))).rounded())
    }

    static func entry(id: String, code: String, name: String, state: ProviderState) -> Entry {
        let windows = state.usage?.windows ?? []
        let pairs: [(String, String)]
        if id == "codex" || id.hasPrefix("codex:") || id == "claude" {
            pairs = [("5h", L("5h limit")), ("7d", L("Weekly limit"))]
        } else if id == "gemini" {
            if code == "Ag" {
                pairs = [("5h", L("Base model (5h)")), ("7d", L("Base model (weekly)"))]
            } else {
                pairs = [("Pro", L("Pro (24h)")), ("Fl", L("Flash (24h)"))]
            }
        } else {
            // Preserve other providers' existing summaries, including monetary balances.
            let value = state.usage?.menuSummary(direction: .remaining) ?? "--"
            return Entry(id: id, code: code, name: name, lines: [Line(label: value, remaining: nil, showsLabel: true)],
                         stale: isStale(state), unavailable: state.usage == nil)
        }
        let showsModelLabels = id == "gemini" && code != "Ag"
        var lines = pairs.map { label, source in
            let value = windows.first(where: { $0.label == source }).flatMap { remaining($0.usedPercent) }
            return Line(label: label, remaining: value, showsLabel: showsModelLabels)
        }
        // Plans without a window (e.g. no 5h limit) show only the windows they
        // have, centered, instead of a "--" placeholder. Keep placeholders
        // while there is no usage at all (loading/error).
        if !showsModelLabels, state.usage != nil, lines.contains(where: { $0.remaining != nil }) {
            lines.removeAll { $0.remaining == nil }
        }
        return Entry(id: id, code: code, name: name, lines: lines,
                     stale: isStale(state), unavailable: state.usage == nil)
    }

    private static func isStale(_ state: ProviderState) -> Bool {
        if case .stale = state { return true }
        return false
    }

    static func accessibilityDescription(_ entries: [Entry]) -> String {
        guard !entries.isEmpty else { return L("QuotaAI") }
        let parts = entries.map { entry in
            let values = entry.lines.map { line -> String in
                guard let remaining = line.remaining else {
                    return entry.lines.count == 1 ? line.label : "\(line.label) --"
                }
                let label = entry.code == "Ge" ? "\(line.label) (24h)" : line.label
                return "\(label) \(CountDirection.remaining.accessibilityPercentPhrase(Double(100 - remaining)))"
            }.joined(separator: ", ")
            var text = "\(entry.name): \(values)"
            if entry.stale { text += ", \(L("data is stale"))" }
            if entry.unavailable { text += ", \(L("No usage data"))" }
            return text
        }
        return ([L("QuotaAI")] + parts).joined(separator: ". ")
    }
}
