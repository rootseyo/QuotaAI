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

## Updates

QuotaAI checks this repository's GitHub Releases once a day (toggle in
**Settings → General → Updates**, or use **Check for updates…** in the tray
menu). When a newer `windows-preview-<version>` release exists it downloads
`QuotaAI.exe` and `QuotaAI.exe.sig`, checks the exact size, the SHA-256
digest GitHub publishes for the asset, and an ECDSA P-256 signature made in CI
with the release key (public half embedded in the app and kept in
`Windows/update-signing.pub.pem`), then swaps the executable and restarts. If
the new version cannot start, the swap is rolled back. If the folder is not writable, it offers the
release page instead. Development builds (version `0.0.0`) never self-update.
Updating requires a release built with this feature; older builds must be
replaced manually once.

## Development

Use the .NET 10 SDK. These commands build the app and run the integration tests
from macOS or Windows:

```sh
dotnet build Windows/QuotaAI.Tray -c Release
dotnet run --project Windows/QuotaAI.Tests -c Release
```

The **Windows preview** GitHub Actions workflow builds and tests every change
and uploads a development build (version `0.0.0`) as the `QuotaAI-Windows-x64`
artifact. It does not publish releases. Releases are cut locally; see
[docs/RELEASING.md](../docs/RELEASING.md#windows-preview-releases).
