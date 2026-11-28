# Releasing QuotaAI

For a local build, run `scripts/bundle.sh` and open `QuotaAI.app`. The local
build uses ad-hoc signing and runs with automatic updates disabled.

## Signed releases

1. Choose a version and update [CHANGELOG.md](../CHANGELOG.md).
2. Use a QuotaAI Developer ID Application certificate and notarization
   credentials. Set `NOTARY_PROFILE`, or set `APPLE_API_KEY_ID`,
   `APPLE_API_ISSUER`, and `APPLE_API_KEY` (the path to a `.p8` file).
3. Create an EdDSA signing key for QuotaAI once. Run
   `.build/artifacts/sparkle/Sparkle/bin/generate_keys --account com.rootseyo.quotaai`
   and set `QUOTAAI_SPARKLE_PUBLIC_KEY` to the resulting public key.
4. Run `scripts/release.sh` with `SIGN_IDENTITY`, `QUOTAAI_VERSION`, and the
   credentials configured above. The script signs, notarizes, and staples
   `QuotaAI.app` and produces `QuotaAI.zip` and `appcast.xml`.
5. Tag the source as `vX.Y.Z` for that version and publish both files on the
   [QuotaAI release](https://github.com/rootseyo/QuotaAI/releases). For a
   public release, Sparkle reads the appcast from the latest release.
6. Download the published files and verify the app opens and can check for
   updates.

The manual GitHub Actions release workflow takes the same version from the
tag. Configure the certificate and notarization secrets listed in the
[workflow](../.github/workflows/release.yml), the repository variable
`QUOTAAI_SPARKLE_PUBLIC_KEY`, and the secret `QUOTAAI_SPARKLE_PRIVATE_KEY`.
The private key must match the public key embedded in the app.

The generated app, archive, and appcast belong in release assets and local
build output. Keep credential files outside the repository.

## Windows preview releases

Windows releases are built, signed, and published from a Windows machine with
[scripts/release-windows.ps1](../scripts/release-windows.ps1). CI only builds
and tests. The in-app updater installs a release only if `QuotaAI.exe.sig` is
a valid ECDSA P-256 signature from the key in
[Windows/update-signing.pub.pem](../Windows/update-signing.pub.pem), so the
private key stays on the release machine (default
`%USERPROFILE%\.quotaai\windows-update-signing.pem`) and is never uploaded.
Keep an offline backup of it: losing it means existing installs can no longer
update and must be replaced by hand.

Requirements: the .NET 10 SDK, `openssl` (bundled with Git for Windows), and
the GitHub CLI signed in with `gh auth login`.

1. Commit and push everything to `main`.
2. Dry run: `./scripts/release-windows.ps1 -Version 2026.11.29 -DryRun`.
   It checks the git state, runs the tests, builds a self-contained x64 exe
   stamped with the version, signs it, and verifies the signature.
3. Publish: run the same command without `-DryRun`. It pushes the tag
   `windows-preview-<version>` and creates a GitHub prerelease with
   `QuotaAI.exe` and `QuotaAI.exe.sig`.

Versions must increase (date-based such as `2026.11.29` or `2026.11.29.1`
works); the updater ignores anything not newer than the running build.
