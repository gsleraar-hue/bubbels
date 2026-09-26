#!/bin/bash
# Builds Bubbels.app for macOS 13+ as a universal binary (Apple Silicon + Intel),
# then packs it as a .zip and a .dmg. Needs only the Xcode command line tools.
#
#   mac/build.sh 1.0.0
#
# The app is ad-hoc signed, not notarised (that needs a paid Apple account).
set -euo pipefail

VERSION="${1:-1.0.0}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$HERE")"
OUT="$ROOT/dist"
APP="$OUT/Bubbels.app"

rm -rf "$OUT"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

echo "== compile"
for ARCH in arm64 x86_64; do
  swiftc -O -swift-version 5 \
    -target "$ARCH-apple-macos13.0" \
    -o "$OUT/Bubbels-$ARCH" \
    "$HERE"/Sources/*.swift
done
lipo -create -output "$APP/Contents/MacOS/Bubbels" "$OUT/Bubbels-arm64" "$OUT/Bubbels-x86_64"
rm "$OUT/Bubbels-arm64" "$OUT/Bubbels-x86_64"

echo "== icon"
ICONSET="$OUT/Bubbels.iconset"
mkdir -p "$ICONSET"
for SIZE in 16 32 128 256 512; do
  sips -z $SIZE $SIZE "$ROOT/build/bubbels-1024.png" --out "$ICONSET/icon_${SIZE}x${SIZE}.png" >/dev/null
  DOUBLE=$((SIZE * 2))
  sips -z $DOUBLE $DOUBLE "$ROOT/build/bubbels-1024.png" --out "$ICONSET/icon_${SIZE}x${SIZE}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/Bubbels.icns"
rm -rf "$ICONSET"

echo "== Info.plist"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Bubbels</string>
  <key>CFBundleDisplayName</key><string>Bubbels</string>
  <key>CFBundleIdentifier</key><string>io.github.gsleraar-hue.bubbels</string>
  <key>CFBundleExecutable</key><string>Bubbels</string>
  <key>CFBundleIconFile</key><string>Bubbels</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>MIT licence</string>
</dict>
</plist>
PLIST

echo "== sign (ad-hoc)"
codesign --force --deep --sign - "$APP"
codesign --verify --verbose "$APP"

echo "== pack"
( cd "$OUT" && ditto -c -k --keepParent Bubbels.app "Bubbels-$VERSION-mac.zip" )
STAGE="$OUT/dmg"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "Bubbels" -srcfolder "$STAGE" -ov -format UDZO "$OUT/Bubbels-$VERSION-mac.dmg" >/dev/null
rm -rf "$STAGE"

ls -la "$OUT"
