import Foundation

/// Reads Claude subscription usage through the user's own `claude` CLI:
/// `claude -p /usage --output-format json` runs the local `/usage` command
/// (no model call, no cost). Claude Code reads and renews its own credentials,
/// so QuotaAI never touches the Keychain on this path and an expired token is
/// refreshed by Claude Code itself.
///
/// The `/usage` text is human-oriented, so parsing is defensive: anything
/// unrecognised throws `parseFailed` and the provider falls back to the OAuth
/// usage endpoint.
enum ClaudeCLIClient {
    /// Minimum time between CLI polls unless the user refreshes explicitly.
    static let pollInterval: TimeInterval = 5 * 60
    /// Plan name rarely changes; re-read it at most hourly.
    static let planInterval: TimeInterval = 60 * 60

    enum CLIError: Error, Equatable {
        case notInstalled
        case timeout
        case failed(Int32)
        case parseFailed
    }

    struct Reading: Equatable, Sendable {
        var fiveHour: Window?
        var weekly: Window?
        var weeklyOpus: Window?
        var weeklySonnet: Window?

        struct Window: Equatable, Sendable {
            var usedPercent: Double
            var resetsAt: Date?
        }

        var isEmpty: Bool {
            fiveHour == nil && weekly == nil && weeklyOpus == nil && weeklySonnet == nil
        }
    }

    // MARK: - Commands

    /// Hooks are disabled so polling never fires the user's Claude Code hooks,
    /// and sessions are not persisted so no transcript is written.
    static let usageArguments = [
        "-p", "/usage",
        "--output-format", "json",
        "--no-session-persistence",
        "--settings", #"{"disableAllHooks":true}"#,
    ]
    static let authStatusArguments = ["auth", "status", "--json"]

    static func readUsage(executable: URL, now: Date = Date()) async throws -> Reading {
        let data = try await run(executable, arguments: usageArguments)
        struct Envelope: Decodable {
            let result: String?
            let isError: Bool?
            enum CodingKeys: String, CodingKey {
                case result
                case isError = "is_error"
            }
        }
        guard let envelope = try? JSONDecoder().decode(Envelope.self, from: data),
              envelope.isError != true,
              let text = envelope.result else {
            throw CLIError.parseFailed
        }
        let reading = parseUsage(text, now: now)
        guard !reading.isEmpty else { throw CLIError.parseFailed }
        return reading
    }

    /// Plan from `claude auth status --json` (`subscriptionType`), or nil.
    /// Throws `ClaudeError.notSignedIn` when the CLI reports no login.
    static func readPlan(executable: URL) async throws -> String? {
        let data = try await run(executable, arguments: authStatusArguments)
        struct Status: Decodable {
            let loggedIn: Bool?
            let subscriptionType: String?
        }
        guard let status = try? JSONDecoder().decode(Status.self, from: data) else { return nil }
        if status.loggedIn == false { throw ClaudeError.notSignedIn }
        return status.subscriptionType
    }

    // MARK: - Parsing

    /// Parses lines such as
    /// `Current session: 25% used · resets Nov 28 at 9:40pm (Asia/Seoul)` and
    /// `Current week (all models): 4% used · resets Dec 5 at 9am (Asia/Seoul)`.
    static func parseUsage(_ text: String, now: Date) -> Reading {
        var reading = Reading()
        for rawLine in text.split(whereSeparator: \.isNewline) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            guard let colon = line.firstIndex(of: ":") else { continue }
            let title = line[..<colon].lowercased()
            let rest = String(line[line.index(after: colon)...])
            guard let percent = usedPercent(in: rest) else { continue }
            let window = Reading.Window(usedPercent: percent, resetsAt: resetDate(in: rest, now: now))

            if title.hasPrefix("current session") {
                reading.fiveHour = window
            } else if title.hasPrefix("current week") {
                if title.contains("opus") {
                    reading.weeklyOpus = window
                } else if title.contains("sonnet") {
                    reading.weeklySonnet = window
                } else {
                    reading.weekly = window
                }
            }
        }
        return reading
    }

    static func usedPercent(in text: String) -> Double? {
        guard let groups = firstMatch(#"(\d+(?:\.\d+)?)\s*%\s*used"#, in: text),
              let number = groups[0], let value = Double(number), value.isFinite else { return nil }
        return max(0, min(100, value))
    }

    /// `resets Nov 28 at 9:40pm (Asia/Seoul)`, `resets 9am (Asia/Seoul)`.
    /// The year is not printed, so the next matching date on/after `now` wins.
    static func resetDate(in text: String, now: Date) -> Date? {
        let pattern = #"resets\s+(?:([A-Za-z]{3,9})\s+(\d{1,2})(?:,?\s+at)?\s+)?(\d{1,2})(?::(\d{2}))?\s*(am|pm)(?:\s*\(([^)]+)\))?"#
        guard let g = firstMatch(pattern, in: text) else { return nil }
        let match = (monthName: g[0], day: g[1], hour: g[2], minute: g[3], meridiem: g[4], zone: g[5])

        var calendar = Calendar(identifier: .gregorian)
        if let zoneName = match.zone, let zone = TimeZone(identifier: String(zoneName)) {
            calendar.timeZone = zone
        } else {
            calendar.timeZone = .current
        }
        guard var hour = match.hour.flatMap({ Int($0) }), (1...12).contains(hour) else { return nil }
        let minute = match.minute.flatMap { Int($0) } ?? 0
        let isPM = match.meridiem?.lowercased() == "pm"
        if hour == 12 { hour = 0 }
        if isPM { hour += 12 }

        let today = calendar.dateComponents([.year, .month, .day], from: now)
        var components = DateComponents()
        components.hour = hour
        components.minute = minute

        if let monthName = match.monthName, let dayText = match.day {
            guard let month = monthNumber(String(monthName)), let day = Int(dayText) else { return nil }
            components.month = month
            components.day = day
            for year in [today.year ?? 0, (today.year ?? 0) + 1] {
                components.year = year
                // Allow a little slack: a reset that just passed is still "this" one.
                if let date = calendar.date(from: components), date >= now.addingTimeInterval(-86400) {
                    return date
                }
            }
            return nil
        }

        // Time only: today, or tomorrow if that time already passed.
        components.year = today.year
        components.month = today.month
        components.day = today.day
        guard let date = calendar.date(from: components) else { return nil }
        return date >= now ? date : calendar.date(byAdding: .day, value: 1, to: date)
    }

    /// Capture groups (1...n) of the first case-insensitive match, nil when absent.
    private static func firstMatch(_ pattern: String, in text: String) -> [String?]? {
        guard let regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]),
              let result = regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) else {
            return nil
        }
        return (1..<result.numberOfRanges).map { index in
            Range(result.range(at: index), in: text).map { String(text[$0]) }
        }
    }

    private static func monthNumber(_ name: String) -> Int? {
        let months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"]
        return months.firstIndex(of: String(name.lowercased().prefix(3))).map { $0 + 1 }
    }

    // MARK: - Executable resolution

    nonisolated(unsafe) private static var resolvedExecutableCache: URL??
    private static let executableCacheLock = NSLock()

    static func candidatePaths(home: String, pathEnv: String?) -> [String] {
        var paths = [
            "\(home)/.local/bin/claude",
            "\(home)/.claude/local/claude",
            "/opt/homebrew/bin/claude",
            "/usr/local/bin/claude",
        ]
        for entry in (pathEnv ?? "").split(separator: ":") {
            let trimmed = entry.trimmingCharacters(in: .whitespaces)
            if !trimmed.isEmpty { paths.append("\(trimmed)/claude") }
        }
        return paths
    }

    /// Cheap file-existence check used by `isDetected` (no process launch).
    static func knownExecutableExists() -> Bool {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        return candidatePaths(home: home, pathEnv: nil)
            .contains { FileManager.default.isExecutableFile(atPath: $0) }
    }

    static func resolveExecutable() -> URL? {
        executableCacheLock.withLock {
            if let cached = resolvedExecutableCache { return cached }
            let home = FileManager.default.homeDirectoryForCurrentUser.path
            let pathEnv = ProcessInfo.processInfo.environment["PATH"]
            var resolved = candidatePaths(home: home, pathEnv: pathEnv)
                .first { FileManager.default.isExecutableFile(atPath: $0) }
                .map { URL(fileURLWithPath: $0) }
            if resolved == nil { resolved = resolveViaShell() }
            // Only cache hits, so installing Claude Code later is picked up.
            if resolved != nil { resolvedExecutableCache = .some(resolved) }
            return resolved
        }
    }

    private static func resolveViaShell() -> URL? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/zsh")
        process.arguments = ["-lc", "command -v claude"]
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
            process.waitUntilExit()
            guard process.terminationStatus == 0 else { return nil }
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            guard let path = String(data: data, encoding: .utf8)?
                .trimmingCharacters(in: .whitespacesAndNewlines),
                  !path.isEmpty,
                  FileManager.default.isExecutableFile(atPath: path) else { return nil }
            return URL(fileURLWithPath: path)
        } catch {
            return nil
        }
    }

    // MARK: - Process

    static func run(_ executable: URL, arguments: [String], timeout: TimeInterval = 20) async throws -> Data {
        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        // A neutral working directory so no project settings apply.
        process.currentDirectoryURL = FileManager.default.temporaryDirectory
        var environment = ProcessInfo.processInfo.environment
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        // GUI apps get a minimal PATH; npm-installed Claude Code needs node.
        environment["PATH"] = ["/opt/homebrew/bin", "/usr/local/bin", "\(home)/.local/bin", environment["PATH"] ?? "/usr/bin:/bin"]
            .joined(separator: ":")
        environment["NO_COLOR"] = "1"
        process.environment = environment
        let stdout = Pipe()
        process.standardOutput = stdout
        process.standardError = FileHandle.nullDevice
        process.standardInput = FileHandle.nullDevice

        // Non-blocking: `waitUntilExit()` can miss the exit notification on a
        // cooperative-pool thread and hang forever, so use the handlers.
        let state = RunState()
        let group = DispatchGroup()
        group.enter()
        process.terminationHandler = { _ in group.leave() }
        do {
            try process.run()
        } catch {
            throw CLIError.notInstalled
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
                    continuation.resume(throwing: CLIError.timeout)
                } else if process.terminationStatus != 0 {
                    continuation.resume(throwing: CLIError.failed(process.terminationStatus))
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

/// Caches CLI readings so the `claude` process runs at most every
/// `ClaudeCLIClient.pollInterval`, and concurrent refreshes share one run.
actor ClaudeCLICache {
    typealias UsageReader = @Sendable () async throws -> ClaudeCLIClient.Reading
    typealias PlanReader = @Sendable () async throws -> String?

    private var last: (reading: ClaudeCLIClient.Reading, at: Date)?
    private var plan: (value: String?, at: Date)?
    private var inflight: Task<ClaudeCLIClient.Reading, Error>?

    func reading(
        now: Date,
        forceRefresh: Bool,
        readUsage: @escaping UsageReader
    ) async throws -> (reading: ClaudeCLIClient.Reading, at: Date) {
        if !forceRefresh, let last, now.timeIntervalSince(last.at) < ClaudeCLIClient.pollInterval {
            return last
        }
        if let inflight, let last {
            _ = try await inflight.value
            return last
        }
        let task = Task { try await readUsage() }
        inflight = task
        defer { inflight = nil }
        let reading = try await task.value
        let entry = (reading: reading, at: Date())
        last = entry
        return entry
    }

    func plan(now: Date, readPlan: @escaping PlanReader) async throws -> String? {
        if let plan, now.timeIntervalSince(plan.at) < ClaudeCLIClient.planInterval {
            return plan.value
        }
        let value = try await readPlan()
        plan = (value, now)
        return value
    }

    func reset() {
        last = nil
        plan = nil
        inflight = nil
    }
}
