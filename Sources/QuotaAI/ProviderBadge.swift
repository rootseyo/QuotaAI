import SwiftUI

/// A provider icon, with a colored monogram for providers without one.
struct ProviderBadge: View {
    let provider: any UsageProvider
    var size: CGFloat = 20

    var body: some View {
        Group {
            if let icon = ProviderIcons.image(for: provider.id) {
                Image(nsImage: icon)
                    .resizable()
                    .scaledToFit()
            } else {
                RoundedRectangle(cornerRadius: size * 0.24, style: .continuous)
                    .fill(Self.color(for: provider.id).gradient)
                    .overlay {
                        Text(provider.shortCode)
                            .font(.system(size: size * 0.45, weight: .bold, design: .rounded))
                            .foregroundStyle(.white)
                    }
            }
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }

    static func color(for providerID: String) -> Color {
        switch providerID {
        case "codex": return Color(red: 0.06, green: 0.65, blue: 0.55)
        case "cursor": return Color(red: 0.45, green: 0.36, blue: 0.90)
        case "claude", "claude-api": return Color(red: 0.85, green: 0.47, blue: 0.25)
        case "gemini": return Color(red: 0.26, green: 0.52, blue: 0.96)
        case "openrouter": return Color(red: 0.35, green: 0.42, blue: 0.85)
        case "deepseek": return Color(red: 0.20, green: 0.60, blue: 0.86)
        case "moonshot": return Color(red: 0.16, green: 0.22, blue: 0.48)
        case "zai": return Color(red: 0.18, green: 0.62, blue: 0.41)
        case "venice": return Color(red: 0.78, green: 0.29, blue: 0.34)
        default: return .gray
        }
    }
}
