# Antigravity migration

QuotaAI's Google quota entry now uses Antigravity CLI instead of the retired
consumer Gemini CLI OAuth service. Install/sign in with `agy` first. An existing
Antigravity CLI sign-in is reused; no account migration or login is performed by
QuotaAI.

The provider keeps the `gemini` preference/snapshot ID to preserve visibility
settings, but its display name is Antigravity. The legacy Gemini provider remains
in source for compatibility and tests; it is no longer in the default list.

Credentials are read from Antigravity CLI's macOS Keychain entry (`service="gemini"`,
`account="antigravity"`, stored via `go-keyring`) or its fallback file
`~/.gemini/antigravity-cli/antigravity-oauth-token`.
Only these CLI stores are used, never desktop app containers or encrypted cookies.
Expired Google tokens are refreshed in memory with the installed-app OAuth client
shipped by `agy`; no credentials are persisted or logged.

The read-only quota source is Google's
`https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary`.
The request uses agy's quota client identity `antigravity/cli/1.4.12`.
This is an internal API and may change with future CLI releases.
The response format and successful token refresh were verified locally on
2026-11-05 without recording credentials or response bodies.

The status bar maps `gemini-5h` and `gemini-weekly` to 5h and 7d remaining.
The popup shows Base model (5h), Base model (weekly), and the independent
Argon (weekly) meter when returned by Google. Other model groups, including the
separate Claude/GPT pools, also remain visible. Argon never inherits base quota. Missing Gemini buckets show `--`; other pools never substitute for them.
No 24-hour limits are inferred. Existing stale-state behavior is retained.

Verification:

```sh
swift test
ANTIGRAVITY_LIVE_TEST=1 swift test --filter AntigravityProviderTests
scripts/bundle.sh
```

Official references:
- https://antigravity.google/docs/cli/gcli-migration/
- https://antigravity.google/docs/cli/commands/usage
- https://developers.google.com/gemini-code-assist/docs/deprecations/code-assist-individuals
