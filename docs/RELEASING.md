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
