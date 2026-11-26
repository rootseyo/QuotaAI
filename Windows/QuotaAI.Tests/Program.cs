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

static void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}
