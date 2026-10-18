#!/bin/zsh
# Builds Tangelo.app (universal, release), plus Tangelo.dmg and Tangelo.zip, into ./build.
# Usage: ./build.sh [version]     e.g. ./build.sh 1.0.0
set -euo pipefail
cd "$(dirname "$0")"
VERSION="${1:-$(cat VERSION)}"

# Build each architecture, then merge (works with Command Line Tools; no Xcode needed).
for ARCH in arm64 x86_64; do
  swift build -c release --triple "$ARCH-apple-macosx14.0" --scratch-path ".build/$ARCH"
done
mkdir -p build
BIN=build/Tangelo-universal
lipo -create -output "$BIN" .build/arm64/release/Tangelo .build/x86_64/release/Tangelo

APP=build/Tangelo.app
rm -rf "$APP" build/Tangelo.dmg build/Tangelo.zip build/dmg
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/Tangelo"
cp Support/Info.plist "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $VERSION" "$APP/Contents/Info.plist"
if [[ ! -f build/AppIcon.icns ]]; then
  swift Support/make-icon.swift build/AppIcon.iconset
  iconutil -c icns build/AppIcon.iconset -o build/AppIcon.icns
fi
cp build/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
codesign --force --deep --sign "${SIGN_IDENTITY:--}" "$APP"

# Zip and DMG for distribution
ditto -c -k --sequesterRsrc --keepParent "$APP" build/Tangelo.zip
mkdir build/dmg && cp -R "$APP" build/dmg/ && ln -s /Applications build/dmg/Applications
hdiutil create -volname "Tangelo" -srcfolder build/dmg -ov -format UDZO build/Tangelo.dmg >/dev/null
rm -rf build/dmg
echo "Built $APP ($VERSION): $(lipo -archs "$APP/Contents/MacOS/Tangelo")"
shasum -a 256 build/Tangelo.dmg build/Tangelo.zip
