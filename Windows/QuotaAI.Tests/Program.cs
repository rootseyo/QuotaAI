using System.Text;
using System.Text.Json;
using QuotaAI.Core;

if (args is ["--fake-server", var scenario])
{
    await RunFakeServerAsync(scenario);
    return;
}

var executable = Environment.ProcessPath!;
var hostArgs = Path.GetFileNameWithoutExtension(executable) == "dotnet"
    ? new[] { System.Reflection.Assembly.GetExecutingAssembly().Location }
    : Array.Empty<string>();

Check(UpdateChecker.ParseTag("windows-preview-2026.11.27.1") == new Version(2026, 11, 27, 1), "Parse a four-part tag");
Check(UpdateChecker.ParseTag("windows-preview-1.13.1") == new Version(1, 13, 1, 0), "Normalize a three-part tag");
Check(UpdateChecker.ParseTag("macos-preview-1.13.1") is null, "Ignore macOS tags");
Check(UpdateChecker.ParseTag("windows-preview-banana") is null, "Ignore malformed tags");
Check(UpdateChecker.Normalize(new Version(2026, 11, 27)) < new Version(2026, 11, 27, 1), "Date build order");

const string dl = "https://github.com/rootseyo/QuotaAI/releases/download/";
static string Asset(string tag, string name = "QuotaAI.exe", long size = 42, string? digest = "sha256:00", string? url = null) =>
    $$"""{"name":"{{name}}","size":{{size}}{{(digest is null ? "" : $",\"digest\":\"{digest}\"")}},"browser_download_url":"{{url ?? dl + tag + "/" + name}}"}""";
static string Release(string tag, bool draft, params string[] assets) =>
    $$"""{"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"html_url":"https://github.com/rootseyo/QuotaAI/releases/tag/{{tag}}","assets":[{{string.Join(",", assets)}}]}""";
static string Sig(string tag) => Asset(tag, "QuotaAI.exe.sig", 72, digest: null);

var releasesJson = "[" + string.Join(",",
    """{"tag_name":"macos-preview-1.13.1","draft":false,"assets":[]}""",
    Release("windows-preview-2026.12.01", true, Asset("windows-preview-2026.12.01"), Sig("windows-preview-2026.12.01")),
    Release("windows-preview-2026.11.27.1", false, Asset("windows-preview-2026.11.27.1", digest: "sha256:ABCDEF"), Sig("windows-preview-2026.11.27.1")),
    Release("windows-preview-2026.11.27", false, Asset("windows-preview-2026.11.27"), Sig("windows-preview-2026.11.27")),
    Release("windows-preview-2027.01.01", false, Asset("windows-preview-2027.01.01", url: "https://evil.example/QuotaAI.exe"), Sig("windows-preview-2027.01.01")),
    Release("windows-preview-2027.02.01", false, Asset("windows-preview-2027.02.01", digest: null), Sig("windows-preview-2027.02.01")),
    Release("windows-preview-2027.03.01", false, Asset("windows-preview-2027.03.01")),
    Release("windows-preview-2027.04.01", false, Asset("windows-preview-2027.04.01", size: UpdateChecker.MaxAssetBytes + 1), Sig("windows-preview-2027.04.01"))) + "]";
var latestRelease = UpdateChecker.SelectLatest(releasesJson);
Check(latestRelease?.Version == new Version(2026, 11, 27, 1),
    "Skip drafts, foreign URLs, and releases without a digest, a signature, or a sane size");
Check(latestRelease?.Sha256 == "abcdef", "Lowercase the asset digest");
Check(latestRelease?.SignatureUrl == dl + "windows-preview-2026.11.27.1/QuotaAI.exe.sig", "Pair the exe with its signature");
Check(latestRelease?.ReleaseUrl == "https://github.com/rootseyo/QuotaAI/releases/tag/windows-preview-2026.11.27.1", "Keep the release page");

Check(UpdateChecker.SafeReleaseUrl("file:///C:/Windows/System32/calc.exe") == UpdateChecker.ReleasesPageUrl, "Never open a local file");
Check(UpdateChecker.SafeReleaseUrl("https://github.com/attacker/QuotaAI/releases/x") == UpdateChecker.ReleasesPageUrl, "Only open this repository");
Check(UpdateChecker.SafeReleaseUrl(null) == UpdateChecker.ReleasesPageUrl, "Fall back to the releases page");

using (var signer = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
{
    var payload = System.Security.Cryptography.SHA256.HashData("QuotaAI update"u8);
    var signature = signer.SignHash(payload, System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence);
    var publicPem = signer.ExportSubjectPublicKeyInfoPem();
    Check(UpdateChecker.VerifySignature(payload, signature, publicPem), "Accept a valid release signature");
    var tampered = (byte[])payload.Clone();
    tampered[0] ^= 1;
    Check(!UpdateChecker.VerifySignature(tampered, signature, publicPem), "Reject a signature over other bytes");
    Check(!UpdateChecker.VerifySignature(payload, signature, UpdateChecker.SigningPublicKeyPem), "Reject a signature from another key");
    Check(!UpdateChecker.VerifySignature(payload, [1, 2, 3], publicPem), "Reject a malformed signature");

    // Made in CI's format (openssl dgst -sha256 -sign) with the real release key.
    var opensslSignature = Convert.FromBase64String(
        "MEUCIQDvYgS93zLjIhYXNnPP4sFIfn6xqJ6w7efvSNCT/5TwBQIgZQrZCeHSBEj30WrKjAeASC67aB2dwZxWcGU7qqaxg8s=");
    Check(UpdateChecker.VerifySignature(payload, opensslSignature, UpdateChecker.SigningPublicKeyPem),
        "Accept an openssl signature made with the release key");
}

var publicKeyFile = FindRepoFile(Path.Combine("Windows", "update-signing.pub.pem"));
Check(publicKeyFile is null || File.ReadAllText(publicKeyFile).Trim().ReplaceLineEndings("\n") == UpdateChecker.SigningPublicKeyPem.Trim().ReplaceLineEndings("\n"),
    "The embedded update key must match Windows/update-signing.pub.pem (used by CI to check the signing secret)");

var usage = await ReadFakeAsync("success");
Check(usage.FiveHour?.RemainingPercent == 75, "Five-hour quota must use the 300-minute window");
Check(usage.Weekly?.RemainingPercent == 42, "Weekly quota must use the 10,080-minute window");
Check(usage.FiveHour?.ResetsAt?.ToUnixTimeSeconds() == 1_730_947_200, "Read the reset timestamp");
Check(usage.Weekly?.ResetsAt is null, "Keep an unavailable reset time empty");

var partial = await ReadFakeAsync("partial");
Check(partial.FiveHour is null, "A different window must not appear as 5 hours");
Check(partial.Weekly?.RemainingPercent == 0, "Clamp out-of-range usage");

try
{
    await ReadFakeAsync("signed-out");
    throw new Exception("Signed-out Codex must surface a login prompt.");
}
catch (CodexUnavailableException error)
{
    Check(error.Message.StartsWith("Sign in to the Codex CLI"), "Show a redacted login prompt");
}

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
try
{
    await ReadFakeAsync("unresponsive", cancellation.Token);
    throw new Exception("A canceled request must stop the server.");
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }

// Settings persistence & clamping tests
var tempDir = Path.Combine(Path.GetTempPath(), $"quotaai-test-{Guid.NewGuid():N}");
try
{
    var widgetPath = Path.Combine(tempDir, "widget.json");
    new WidgetSettings(1240, 760, AlwaysOnTop: false).Save(widgetPath);
    var loadedWidget = WidgetSettings.Load(widgetPath);
    Check(loadedWidget.X == 1240 && loadedWidget.Y == 760 && !loadedWidget.AlwaysOnTop, "Persist widget location and pin state");

    var clamped = WidgetSettings.ClampToWorkingArea(5000, -100, 320, 220, 0, 0, 1920, 1040);
    Check(clamped == (1600, 0), "Clamp saved widget coordinates into the screen working area");

    var settingsPath = Path.Combine(tempDir, "settings.json");
    var appSettings = new AppSettings
    {
        RefreshIntervalSeconds = 30,
        NotificationsEnabled = true,
        NotificationThreshold = 90,
        CountDirection = CountDirection.Used,
        ResetTimeStyle = ResetTimeStyle.Absolute
    };
    appSettings.SetMode("claude", ProviderMode.On);
    appSettings.SetShowsInTray("cursor", false);
    appSettings.SetRemainingColor(QuotaColorBand.High, ThemeAppearance.Dark, 0x11EE88);
    appSettings.AddCodexAccount(CodexAccountConfig.Create("Work", @"~\.codex-work"));
    appSettings.Save(settingsPath);

    var reloaded = AppSettings.Load(settingsPath);
    Check(reloaded.RefreshIntervalSeconds == 30, "Persist refresh interval");
    Check(reloaded.GetMode("claude") == ProviderMode.On, "Persist provider mode");
    Check(!reloaded.ShowsInTray("cursor"), "Persist tray visibility");
    Check(reloaded.CodexExtraAccounts.Count == 1 && reloaded.CodexExtraAccounts[0].Label == "Work", "Persist extra Codex accounts");
    Check(RemainingQuotaPalette.GetRgb(reloaded.RemainingColorOverrides, 90, darkAppearance: true) == 0x11EE88, "Persist custom quota band color");
}
finally
{
    if (Directory.Exists(tempDir))
        Directory.Delete(tempDir, recursive: true);
}

// Tray 5h / 7d tooltip formatter & Win32 127-char limit tests
{
    var now = DateTimeOffset.Now;
    var entries = new List<TrayTooltipFormatter.EntryInput>
    {
        new("codex", "Cx", "Codex", ProviderState.Ready(new ProviderUsage("Pro", [
            new UsageWindow("5h limit", 16, now.AddHours(4)),
            new UsageWindow("Weekly limit", 38, now.AddDays(5))
        ], now))),
        new("claude", "Cl", "Claude", ProviderState.Ready(new ProviderUsage("Max", [
            new UsageWindow("5h limit", 32, now.AddHours(2)),
            new UsageWindow("Weekly limit", 55, now.AddDays(3))
        ], now))),
        new("gemini", "Ag", "Antigravity", ProviderState.Ready(new ProviderUsage(null, [
            new UsageWindow("Base model (5h)", 8, now.AddHours(3)),
            new UsageWindow("Base model (weekly)", 77, now.AddDays(4))
        ], now)))
    };

    var tip = TrayTooltipFormatter.Format(entries, CountDirection.Remaining, TrayDisplayStyle.TwoLineRemaining, TrayBarStyle.Full);
    Check(tip.Contains("Codex: 5h 84% · 7d 62%"), $"Format Codex 5h/7d remaining ({tip})");
    Check(tip.Contains("Claude: 5h 68% · 7d 45%"), "Format Claude 5h/7d remaining");
    Check(tip.Contains("Antigravity: 5h 92% · 7d 23%"), "Format Antigravity 5h/7d remaining");
    Check(tip.Length <= TrayTooltipFormatter.MaxWin32TooltipLength, "Stay within 127-char Win32 tooltip limit");

    var weeklyOnly = TrayTooltipFormatter.Format(
        [new("claude", "Cl", "Claude", ProviderState.Ready(new ProviderUsage("Pro", [
            new UsageWindow("Weekly limit", 30, now.AddDays(3))
        ], now)))],
        CountDirection.Remaining, TrayDisplayStyle.TwoLineRemaining, TrayBarStyle.Full);
    Check(weeklyOnly.Contains("Claude: 7d 70%") && !weeklyOnly.Contains("--"),
        $"Show only the weekly window when there is no 5h limit ({weeklyOnly})");

    // Stress-test with 10 providers enabled
    var crowded = Enumerable.Range(1, 10).Select(i =>
        new TrayTooltipFormatter.EntryInput($"codex:{i}", $"C{i}", $"Codex Account Number {i}", entries[0].State)).ToList();
    var crowdedTip = TrayTooltipFormatter.Format(crowded, CountDirection.Remaining, TrayDisplayStyle.TwoLineRemaining, TrayBarStyle.Full);
    Check(crowdedTip.Length <= TrayTooltipFormatter.MaxWin32TooltipLength, $"Compact 10-provider tooltip fits <=127 chars ({crowdedTip.Length})");
}

// Provider response parser unit checks (Claude, Antigravity go-keyring, Cursor)
{
    var now = DateTimeOffset.Now;
    using var claudeDoc = JsonDocument.Parse("""
        {"five_hour":{"utilization":20,"resets_at":"2026-11-27T02:00:00Z"},"seven_day":{"utilization":45,"resets_at":"2026-12-01T00:00:00Z"}}
        """);
    var claudeUsage = ClaudeProvider.ParseUsage(claudeDoc.RootElement, "max", now);
    Check(claudeUsage.Windows.Count == 2 && claudeUsage.Windows[0].RemainingPercent == 80 && claudeUsage.Windows[1].RemainingPercent == 55,
        "Parse Claude 5h and 7d utilization");

    var rawJson = """{"token":{"access_token":"tok","refresh_token":"ref"}}""";
    var wrapped = "go-keyring-base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(rawJson));
    Check(AntigravityProvider.UnwrapGoKeyringText(wrapped) == rawJson, "Unwrap Antigravity go-keyring-base64 payload");

    using var agDoc = JsonDocument.Parse("""
        {"groups":[{"displayName":"Gemini","buckets":[
          {"bucketId":"gemini-5h","remainingFraction":0.92,"resetTime":"2026-11-27T01:00:00Z"},
          {"bucketId":"gemini-weekly","remainingFraction":0.23,"resetTime":"2026-12-02T00:00:00Z"}
        ]}]}
        """);
    var agUsage = AntigravityProvider.ParseUsage(agDoc.RootElement, now);
    Check(agUsage.Windows.Count == 2 && agUsage.Windows[0].RemainingPercent == 92 && agUsage.Windows[1].RemainingPercent == 23,
        "Parse Antigravity 5h and weekly buckets");

    var fakeJwt = "eyJhbGciOiJIUzI1NiJ9." + Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"sub":"user_123"}""")).TrimEnd('=');
    Check(CursorProvider.ExtractJwtSubject(fakeJwt) == "user_123", "Extract JWT subject for Cursor session cookie");
}

Console.WriteLine("Codex usage integration tests passed.");

Task<CodexUsage> ReadFakeAsync(string scenario, CancellationToken cancellationToken = default) =>
    CodexAppServer.ReadAsync(executable, [.. hostArgs, "--fake-server", scenario], cancellationToken);

static async Task RunFakeServerAsync(string scenario)
{
    await ExpectAsync("initialize");
    if (scenario == "unresponsive")
    {
        await Task.Delay(Timeout.Infinite);
        return;
    }

    Console.WriteLine("""{"id":1,"result":{}}""");
    await ExpectAsync("initialized");
    await ExpectAsync("account/rateLimits/read");
    Console.WriteLine("""{"method":"account/rateLimits/updated","params":{}}""");

    var response = scenario switch
    {
        "success" => """
            {"id":2,"result":{"rateLimits":{"primary":{"windowDurationMins":300,"usedPercent":99}},
            "rateLimitsByLimitId":{"codex":{"primary":{"windowDurationMins":10080,"usedPercent":58,"resetsAt":null},
            "secondary":{"windowDurationMins":300,"usedPercent":25,"resetsAt":1730947200}}}}}
            """,
        "partial" => """
            {"id":2,"result":{"rateLimits":{"primary":{"windowDurationMins":60,"usedPercent":10},
            "secondary":{"windowDurationMins":10080,"usedPercent":120,"resetsAt":999999999999}}}}
            """,
        "signed-out" => """{"id":2,"error":{"message":"authentication required"}}""",
        _ => throw new ArgumentException("Unknown test scenario", nameof(scenario))
    };
    Console.WriteLine(response.ReplaceLineEndings(""));
}

static async Task ExpectAsync(string method)
{
    using var request = JsonDocument.Parse(await Console.In.ReadLineAsync() ?? "");
    Check(request.RootElement.GetProperty("method").GetString() == method, $"Expected {method}");
}

static string? FindRepoFile(string relative)
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, relative);
        if (File.Exists(candidate))
            return candidate;
    }
    return null;
}

static void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}
