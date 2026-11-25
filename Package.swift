// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "QuotaAI",
    defaultLocalization: "en",
    platforms: [.macOS(.v14)],
    products: [
        // Named case-distinct from the QuotaAI app binary: on the default
        // case-insensitive APFS, products "QuotaAI" and "quotaai" would
        // clobber each other in .build. bundle.sh installs it into the app
        // bundle as Contents/Helpers/quotaai.
        .executable(name: "quotaai-cli", targets: ["quotaai-cli"]),
    ],
    dependencies: [
        .package(url: "https://github.com/sparkle-project/Sparkle", from: "2.6.0"),
    ],
    targets: [
        .target(
            name: "QuotaAIStatusKit",
            path: "Sources/QuotaAIStatusKit"
        ),
        .executableTarget(
            name: "QuotaAI",
            dependencies: [
                "QuotaAIStatusKit",
                .product(name: "Sparkle", package: "Sparkle"),
            ],
            path: "Sources/QuotaAI",
            resources: [.process("Resources/en.lproj"), .process("Resources/es.lproj"),
                        .copy("Resources/Localizable.xcstrings"), .process("Resources/ProviderIcons")]
        ),
        .executableTarget(
            name: "quotaai-cli",
            dependencies: ["QuotaAIStatusKit"],
            path: "Sources/quotaai-cli"
        ),
        .testTarget(
            name: "QuotaAIStatusKitTests",
            dependencies: ["QuotaAIStatusKit"],
            path: "Tests/QuotaAIStatusKitTests",
            resources: [.process("Fixtures")]
        ),
        .testTarget(
            name: "QuotaAITests",
            dependencies: ["QuotaAI", "QuotaAIStatusKit"],
            path: "Tests/QuotaAITests"
        ),
    ]
)
