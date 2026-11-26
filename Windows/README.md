# QuotaAI for Windows

QuotaAI runs in the Windows notification area and opens a small, movable
widget. The widget stays above ordinary windows by default.

- Hover over the tray icon for a Codex quota summary. Left-click to show or
  hide the widget.
- Right-click the icon to show or hide, toggle **Always on top**, refresh or
  exit. The widget's **X** hides it and leaves the app in the tray.
- The first row shows 5-hour quota and the second shows weekly quota. A missing
  window displays a dash, and a failed refresh marks previously loaded readings
  as cached.
- The remaining percentage is green at 76–100%, blue at 51–75%, gold at
  26–50%, orange at 1–25%, and red at 0%. Windows contrast themes use the
  system text color.

The preview reads Codex quota from the native Windows Codex CLI. Install the
CLI, make sure it is on your PATH, and sign in with ChatGPT in a terminal before
refreshing QuotaAI. QuotaAI asks the CLI's app server for quota over standard
input and output and leaves sign-in to the CLI. It refreshes every five minutes
while open, including when the widget is hidden.

## Development

Use the .NET 10 SDK. These commands build the app and run the mock Codex
integration tests from macOS or Windows.

```sh
dotnet build Windows/QuotaAI.Tray -c Release
dotnet run --project Windows/QuotaAI.Tests -c Release
```

Run the tray app from Windows to check Explorer integration and widget behavior.
The **Windows preview** GitHub Actions workflow publishes a self-contained
Windows x64 executable in the `QuotaAI-Windows-x64` artifact.
