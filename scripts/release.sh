#!/bin/zsh
# Builds, signs (Developer ID), notarizes, staples, and zips QuotaAI.app.
#
# Prerequisites (see docs/RELEASING.md):
#   - A "Developer ID Application" certificate in your login keychain
#   - App Store Connect API key for notarytool
#
# Required environment:
#   SIGN_IDENTITY        e.g. "Developer ID Application: Your Name (TEAMID)"
#   QUOTAAI_SPARKLE_PUBLIC_KEY   public key matching the Sparkle keychain signing key
#   QUOTAAI_VERSION   e.g. 1.0.1 (defaults to 1.0)
#
# Sparkle signs with the QuotaAI Keychain account by default. CI can supply
# the private key via QUOTAAI_SPARKLE_PRIVATE_KEY instead.
# Set NOTARY_PROFILE, or all of
# APPLE_API_KEY_ID / APPLE_API_ISSUER / APPLE_API_KEY (path to .p8).
set -euo pipefail

cd "$(dirname "$0")/.."

: "${SIGN_IDENTITY:?Set SIGN_IDENTITY to your Developer ID Application identity}"
: "${QUOTAAI_SPARKLE_PUBLIC_KEY:?Set QUOTAAI_SPARKLE_PUBLIC_KEY to the QuotaAI update signing public key}"
if [[ -z "${NOTARY_PROFILE:-}" ]]; then
    : "${APPLE_API_KEY_ID:?Set NOTARY_PROFILE or APPLE_API_KEY_ID/ISSUER/KEY}"
    : "${APPLE_API_ISSUER:?Set APPLE_API_ISSUER}"
    : "${APPLE_API_KEY:?Set APPLE_API_KEY (path to .p8)}"
fi

APP="QuotaAI.app"
ZIP="QuotaAI.zip"

# 1. Build + sign with hardened runtime.
SIGN_IDENTITY="$SIGN_IDENTITY" scripts/bundle.sh

# 2. Zip for notarization (ditto preserves the bundle + signature).
rm -f "$ZIP"
/usr/bin/ditto -c -k --keepParent "$APP" "$ZIP"

# 3. Notarize and wait.
if [[ -n "${NOTARY_PROFILE:-}" ]]; then
    xcrun notarytool submit "$ZIP" --keychain-profile "$NOTARY_PROFILE" --wait
else
    xcrun notarytool submit "$ZIP" \
        --key "$APPLE_API_KEY" \
        --key-id "$APPLE_API_KEY_ID" \
        --issuer "$APPLE_API_ISSUER" \
        --wait
fi

# 4. Staple the ticket to the app, then re-zip the stapled bundle.
xcrun stapler staple "$APP"
rm -f "$ZIP"
/usr/bin/ditto -c -k --keepParent "$APP" "$ZIP"

# 5. Generate the Sparkle appcast, signed with the EdDSA key in the Keychain.
#    The enclosure URL must point at the versioned release asset.
VERSION="${QUOTAAI_VERSION:-1.0}"
GENERATE_APPCAST=$(find .build/artifacts/sparkle -name generate_appcast -type f | head -1)
if [[ -z "$GENERATE_APPCAST" ]]; then
    echo "error: generate_appcast not found" >&2
    exit 1
fi
APPCAST_DIR=$(mktemp -d)
trap 'rm -rf "$APPCAST_DIR"' EXIT
cp "$ZIP" "$APPCAST_DIR/"
appcast_args=(
    --download-url-prefix "https://github.com/rootseyo/QuotaAI/releases/download/v${VERSION}/"
    --maximum-versions 1
    "$APPCAST_DIR"
)
if [[ -n "${QUOTAAI_SPARKLE_PRIVATE_KEY:-}" ]]; then
    printf '%s\n' "$QUOTAAI_SPARKLE_PRIVATE_KEY" | "$GENERATE_APPCAST" --ed-key-file - "${appcast_args[@]}"
else
    "$GENERATE_APPCAST" --account com.rootseyo.quotaai "${appcast_args[@]}"
fi
cp "$APPCAST_DIR/appcast.xml" appcast.xml
echo "Appcast written to $PWD/appcast.xml"

echo "Release artifact ready: $PWD/$ZIP"
xcrun stapler validate "$APP"
