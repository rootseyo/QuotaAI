import Foundation

public enum StatusPaths {
    public static let appBundleIdentifier = "com.rootseyo.quotaai"

    public static var snapshotFileURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("QuotaAI/status.json", isDirectory: false)
    }
}
