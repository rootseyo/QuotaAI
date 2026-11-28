import XCTest
@testable import QuotaAI

final class CLIToolTests: XCTestCase {
    func testCandidatePathsPutPreferredLocationsBeforePATH() {
        let paths = CLITool.claude.candidatePaths(home: "/Users/test", pathEnv: "/custom/bin: :/other/bin")
        XCTAssertEqual(paths.first, "/Users/test/.local/bin/claude")
        XCTAssertEqual(paths.suffix(2), ["/custom/bin/claude", "/other/bin/claude"])
    }

    func testUnknownToolIsNotResolvedOrCached() {
        let tool = CLITool(name: "quotaai-test-missing-\(UUID().uuidString)") { _ in [] }
        XCTAssertNil(tool.resolve())
        XCTAssertFalse(tool.knownExecutableExists())
    }

    func testResolvesFromPreferredPaths() {
        let tool = CLITool(name: "sh") { _ in ["/nonexistent/sh", "/bin/sh"] }
        XCTAssertEqual(tool.resolve()?.path, "/bin/sh")
        XCTAssertTrue(tool.knownExecutableExists())
    }

    func testRunReturnsStdout() async throws {
        let data = try await CLITool.run(URL(fileURLWithPath: "/bin/echo"), arguments: ["hello"], timeout: 5)
        XCTAssertEqual(String(data: data, encoding: .utf8), "hello\n")
    }

    func testRunReportsNonZeroExit() async {
        do {
            _ = try await CLITool.run(URL(fileURLWithPath: "/usr/bin/false"), arguments: [], timeout: 5)
            XCTFail("expected failure")
        } catch {
            XCTAssertEqual(error as? CLITool.RunError, .failed(1))
        }
    }

    func testRunTerminatesAfterTimeout() async {
        let start = Date()
        do {
            _ = try await CLITool.run(URL(fileURLWithPath: "/bin/sleep"), arguments: ["30"], timeout: 0.5)
            XCTFail("expected timeout")
        } catch {
            XCTAssertEqual(error as? CLITool.RunError, .timeout)
        }
        XCTAssertLessThan(Date().timeIntervalSince(start), 10)
    }

    func testRunReportsLaunchFailure() async {
        do {
            _ = try await CLITool.run(URL(fileURLWithPath: "/nonexistent/tool"), arguments: [], timeout: 5)
            XCTFail("expected launch failure")
        } catch {
            XCTAssertEqual(error as? CLITool.RunError, .launchFailed)
        }
    }

    func testEnvironmentAddsInstallDirectoriesAndOverrides() {
        let environment = CLITool.environment(adding: ["NO_COLOR": "1"])
        XCTAssertTrue(environment["PATH"]?.hasPrefix("/opt/homebrew/bin:/usr/local/bin:") == true)
        XCTAssertEqual(environment["NO_COLOR"], "1")
    }
}
