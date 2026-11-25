import AppKit

@MainActor
enum ProviderIcons {
    static func image(for providerID: String) -> NSImage? {
        switch providerID {
        case "codex": chatGPT
        case let id where id.hasPrefix("codex:"): chatGPT
        case "claude", "claude-api": claude
        case "gemini": gemini
        default: nil
        }
    }

    static let antigravity = load("antigravity", extension: "icns")
    private static let chatGPT = load("chatgpt", extension: "icns")
    private static let claude = load("claude", extension: "png")
    private static let gemini = load("gemini", extension: "png")

    private static func load(_ name: String, extension fileExtension: String) -> NSImage? {
        Bundle.module.url(forResource: name, withExtension: fileExtension)
            .flatMap { NSImage(contentsOf: $0) }
    }
}
