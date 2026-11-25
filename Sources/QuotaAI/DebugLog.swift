import Foundation

/// Opt-in stderr tracing for development only (`QUOTAAI_DEBUG=1`).
/// Never logs credentials; callers must pass only structural information.
enum DebugLog {
    nonisolated private static let enabled =
        ProcessInfo.processInfo.environment["QUOTAAI_DEBUG"] != nil

    nonisolated static func write(_ message: @autoclosure () -> String) {
        guard enabled else { return }
        FileHandle.standardError.write(Data("[QuotaAI] \(message())\n".utf8))
    }
}
