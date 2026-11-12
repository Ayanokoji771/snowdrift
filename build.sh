#!/bin/bash
# Builds build/Snowdrift.app. Usage: ./build.sh [--install]
set -euo pipefail
cd "$(dirname "$0")"

VERSION="1.0.0"
APP="build/Snowdrift.app"

# Pick a macOS SDK this Swift compiler can use. Command Line Tools sometimes ship
# a newer default SDK than the compiler, so try the candidates in order.
pick_sdk() {
  local probe
  probe="$(mktemp -d)"
  echo 'import AppKit; print(NSApplication.self)' > "$probe/t.swift"
  local candidates=()
  if [ -n "${SDKROOT:-}" ]; then candidates+=("$SDKROOT"); fi
  candidates+=("$(xcrun --sdk macosx --show-sdk-path 2>/dev/null || true)")
  for s in $(ls -d /Library/Developer/CommandLineTools/SDKs/MacOSX[0-9]*.sdk 2>/dev/null | sort -rV); do candidates+=("$s"); done
  for s in "${candidates[@]}"; do
    [ -d "$s" ] || continue
    if swiftc -sdk "$s" -typecheck "$probe/t.swift" >/dev/null 2>&1; then
      rm -rf "$probe"
      echo "$s"
      return 0
    fi
  done
  rm -rf "$probe"
  return 1
}

SDK="$(pick_sdk)" || { echo "No usable macOS SDK found. Install Xcode or the Command Line Tools." >&2; exit 1; }
echo "SDK: $SDK"

# Compile with swiftc directly (works even when SwiftPM in the Command Line Tools is broken).
mkdir -p build/bin
ARCHS="${ARCHS:-arm64 x86_64}"
SLICES=()
for arch in $ARCHS; do
  swiftc -O -swift-version 5 -sdk "$SDK" -target "$arch-apple-macos14.0" \
    -framework AppKit -framework Metal -framework MetalKit -framework Carbon -framework ServiceManagement -framework SwiftUI \
    Sources/Snowdrift/*.swift -o "build/bin/Snowdrift-$arch"
  SLICES+=("build/bin/Snowdrift-$arch")
done
lipo -create "${SLICES[@]}" -output build/bin/Snowdrift
BIN="build/bin/Snowdrift"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/Snowdrift"

# App icon, painted by the app itself.
ICONSET="build/AppIcon.iconset"
rm -rf "$ICONSET"
mkdir -p "$ICONSET"
"$BIN" --icon build/icon_1024.png
for size in 16 32 128 256 512; do
  sips -z $size $size build/icon_1024.png --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z $double $double build/icon_1024.png --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Snowdrift</string>
  <key>CFBundleDisplayName</key><string>Snowdrift</string>
  <key>CFBundleIdentifier</key><string>io.github.snowdrift.app</string>
  <key>CFBundleExecutable</key><string>Snowdrift</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>MIT License</string>
  <key>NSAppleEventsUsageDescription</key><string>Snowdrift показывает в снежном острове, что играет в Spotify или «Музыке», и управляет воспроизведением. Snowdrift shows what's playing in Spotify or Music and controls playback.</string>
</dict>
</plist>
PLIST

# Ad-hoc signature: enough to run on this Mac. For sharing, sign with a Developer ID and notarize.
codesign --force --deep --sign - "$APP"
echo "Built $APP"

if [ "${1:-}" = "--install" ]; then
  pkill -x Snowdrift 2>/dev/null || true
  rm -rf /Applications/Snowdrift.app
  cp -R "$APP" /Applications/
  echo "Installed to /Applications/Snowdrift.app"
fi
