import Foundation

/// Finds and runs a user-installed command-line tool (`claude`, `codex`).
///
/// GUI apps launch with a minimal PATH, so lookup checks the usual install
/// locations, then the inherited PATH, then a login shell. Only successful
/// lookups are cached, so a tool installed after launch is picked up on the
/// next refresh.
///
/// GOTCHA: never call `Process.waitUntilExit()` here. It spins the calling
/// thread's run loop and can miss the exit notification on a
/// cooperative-pool thread, hanging forever. Use the termination handler.
final class CLITool: @unchecked Sendable {
    let name: String
    private let preferredPaths: @Sendable (_ home: String) -> [String]
    private let lock = NSLock()
    private var cached: URL?

    init(name: String, preferredPaths: @escaping @Sendable (_ home: String) -> [String]) {
        self.name = name
        self.preferredPaths = preferredPaths
    }

    static let claude = CLITool(name: "claude") { home in
        ["\(home)/.local/bin/claude", "\(home)/.claude/local/claude",
         "/opt/homebrew/bin/claude", "/usr/local/bin/claude"]
    }

    static let codex = CLITool(name: "codex") { home in
        ["/opt/homebrew/bin/codex", "/usr/local/bin/codex", "\(home)/.local/bin/codex"]
    }

    // MARK: - Lookup

    func candidatePaths(home: String, pathEnv: String?) -> [String] {
        var paths = preferredPaths(home)
        for entry in (pathEnv ?? "").split(separator: ":") {
            let trimmed = entry.trimmingCharacters(in: .whitespaces)
            if !trimmed.isEmpty { paths.append("\(trimmed)/\(name)") }
        }
        return paths
    }

    /// Cheap file-existence check for `isDetected` (no process launch).
    func knownExecutableExists() -> Bool {
        candidatePaths(home: Self.home, pathEnv: nil)
            .contains { FileManager.default.isExecutableFile(atPath: $0) }
    }

    func resolve() -> URL? {
        lock.withLock {
            if let cached { return cached }
            let pathEnv = ProcessInfo.processInfo.environment["PATH"]
            let resolved = candidatePaths(home: Self.home, pathEnv: pathEnv)
                .first { FileManager.default.isExecutableFile(atPath: $0) }
                .map { URL(fileURLWithPath: $0) }
                ?? resolveViaLoginShell()
            cached = resolved
            return resolved
        }
    }

    /// Forgets the cached path (testing only).
    func resetCache() {
        lock.withLock { cached = nil }
    }

    private func resolveViaLoginShell() -> URL? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/zsh")
        process.arguments = ["-lc", "command -v \(name)"]
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice
        process.standardInput = FileHandle.nullDevice

        let exited = DispatchSemaphore(value: 0)
        process.terminationHandler = { _ in exited.signal() }
        do { try process.run() } catch { return nil }
        // A slow shell profile must not stall the refresh.
        guard exited.wait(timeout: .now() + 5) == .success else {
            process.terminate()
            return nil
        }
        guard process.terminationStatus == 0 else { return nil }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        guard let path = String(data: data, encoding: .utf8)?
            .trimmingCharacters(in: .whitespacesAndNewlines),
              !path.isEmpty,
              FileManager.default.isExecutableFile(atPath: path) else { return nil }
        return URL(fileURLWithPath: path)
    }

    // MARK: - Running

    enum RunError: Error, Equatable {
        case launchFailed
        case timeout
        case failed(Int32)
    }

    static var home: String { FileManager.default.homeDirectoryForCurrentUser.path }

    /// The inherited environment with the common install directories on PATH,
    /// so npm-installed tools can find `node`.
    static func environment(adding extra: [String: String] = [:]) -> [String: String] {
        var environment = ProcessInfo.processInfo.environment
        environment["PATH"] = ["/opt/homebrew/bin", "/usr/local/bin", "\(home)/.local/bin",
                               environment["PATH"] ?? "/usr/bin:/bin"]
            .joined(separator: ":")
        environment.merge(extra) { _, new in new }
        return environment
    }

    /// Runs a tool to completion and returns its stdout. stdin and stderr are
    /// discarded; the process is terminated after `timeout`.
    static func run(
        _ executable: URL,
        arguments: [String],
        environment: [String: String] = CLITool.environment(),
        workingDirectory: URL? = nil,
        timeout: TimeInterval
    ) async throws -> Data {
        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        process.environment = environment
        process.currentDirectoryURL = workingDirectory
        let stdout = Pipe()
        process.standardOutput = stdout
        process.standardError = FileHandle.nullDevice
        process.standardInput = FileHandle.nullDevice

        let state = RunState()
        let group = DispatchGroup()
        group.enter()
        process.terminationHandler = { _ in group.leave() }
        do {
            try process.run()
        } catch {
            throw RunError.launchFailed
        }
        group.enter()
        DispatchQueue.global(qos: .utility).async {
            state.setOutput(stdout.fileHandleForReading.readDataToEndOfFile())
            group.leave()
        }
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + timeout) {
            if process.isRunning {
                state.markTimedOut()
                process.terminate()
            }
        }

        return try await withCheckedThrowingContinuation { continuation in
            group.notify(queue: .global(qos: .utility)) {
                if state.timedOut {
                    continuation.resume(throwing: RunError.timeout)
                } else if process.terminationStatus != 0 {
                    continuation.resume(throwing: RunError.failed(process.terminationStatus))
                } else {
                    continuation.resume(returning: state.output)
                }
            }
        }
    }

    private final class RunState: @unchecked Sendable {
        private let lock = NSLock()
        private var _output = Data()
        private var _timedOut = false
        var output: Data { lock.withLock { _output } }
        var timedOut: Bool { lock.withLock { _timedOut } }
        func setOutput(_ data: Data) { lock.withLock { _output = data } }
        func markTimedOut() { lock.withLock { _timedOut = true } }
    }
}
