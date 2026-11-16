#!/bin/bash
# Builds TextSnag.app into ./dist (and optionally installs it).
#
#   scripts/build-app.sh            build + sign dist/TextSnag.app
#   scripts/build-app.sh --install  also copy it to /Applications and launch it
#   scripts/build-app.sh --zip      also make dist/TextSnag.zip for a GitHub release
#
# Signing: uses SIGN_IDENTITY if set, otherwise the first "Developer ID Application"
# identity in your keychain, otherwise an ad-hoc signature (fine for your own Mac).
# Notarizing (so other people's Macs open it without a warning): set NOTARY_PROFILE to a
# profile created with `xcrun notarytool store-credentials` and pass --zip.
set -euo pipefail
cd "$(dirname "$0")/.."

APP=dist/TextSnag.app
swift build -c release --arch arm64 --arch x86_64

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/apple/Products/Release/TextSnag "$APP/Contents/MacOS/TextSnag"
cp Resources/Info.plist "$APP/Contents/Info.plist"
[ -f Resources/AppIcon.icns ] && cp Resources/AppIcon.icns "$APP/Contents/Resources/"

IDENTITY="${SIGN_IDENTITY:-$(security find-identity -v -p codesigning 2>/dev/null | grep -m1 'Developer ID Application' | sed -E 's/.*"(.*)"/\1/' || true)}"
if [ -n "$IDENTITY" ]; then
  echo "Signing with: $IDENTITY"
  codesign --force --options runtime --timestamp --sign "$IDENTITY" "$APP"
else
  echo "No Developer ID found; signing ad-hoc."
  codesign --force --sign - "$APP"
fi
codesign --verify --strict "$APP"

if [[ " $* " == *" --zip "* ]]; then
  rm -f dist/TextSnag.zip
  ditto -c -k --keepParent "$APP" dist/TextSnag.zip
  if [ -n "${NOTARY_PROFILE:-}" ]; then
    xcrun notarytool submit dist/TextSnag.zip --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$APP"
    rm dist/TextSnag.zip && ditto -c -k --keepParent "$APP" dist/TextSnag.zip
  fi
  echo "Release zip: dist/TextSnag.zip"
fi

if [[ " $* " == *" --install "* ]]; then
  pkill -x TextSnag 2>/dev/null || true
  rm -rf /Applications/TextSnag.app
  ditto "$APP" /Applications/TextSnag.app
  open /Applications/TextSnag.app
  echo "Installed to /Applications/TextSnag.app"
fi
echo "Built $APP"
