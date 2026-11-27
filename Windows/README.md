# QuotaAI for Windows

QuotaAI runs in the Windows notification area and opens a compact, borderless
desktop widget that follows your Windows dark or light theme and scales across
High-DPI displays. It includes a full **Settings** window matching the macOS
app (**General**, **Providers**, **Display**, and **Alerts**).

- **Hover over the tray icon** to view the **5-hour (`5h`) and weekly (`7d`)
  remaining percentages** for each configured AI service (`Codex`, multiple
  Codex accounts, `Claude`, `Antigravity`, `Z.ai`, plus summaries for `Cursor`,
  `Claude API`, `OpenRouter`, `DeepSeek`, `Kimi`, and `Venice`).
- **Left-click** the tray icon to show or hide the desktop widget;
  **double-click** (or click the **⚙** button on the widget / **Settings…** in
  the right-click menu) to open **Settings**.
- Drag anywhere on the widget card to reposition it. Clicking **×** (or pressing
  `Esc`) hides the widget and leaves QuotaAI in the tray. Widget position, pin
  state, provider visibility, display preferences, custom quota band colors, and
  alert thresholds are saved under `%LOCALAPPDATA%\QuotaAI\settings.json`.
- API keys entered in Settings are stored only in **Windows Credential Manager**
  (never in plaintext files on disk) and sent only to each provider's own API.

## Development

Use the .NET 10 SDK. These commands build the app and run the integration tests
from macOS or Windows:

```sh
dotnet build Windows/QuotaAI.Tray -c Release
dotnet run --project Windows/QuotaAI.Tests -c Release
```

The **Windows preview** GitHub Actions workflow publishes a self-contained
single-file Windows x64 executable in the `QuotaAI-Windows-x64` artifact.
Pushing a `windows-preview-*` tag also publishes that executable as a GitHub
prerelease without changing the latest stable release.
