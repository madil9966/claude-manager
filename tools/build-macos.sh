#!/usr/bin/env bash
# Builds Claude Manager for macOS (run on a Mac; GitHub Actions does this on every release tag):
#   dist-mac/ClaudeManager-<version>-macos-arm64.dmg / .zip   (Apple Silicon)
#   dist-mac/ClaudeManager-<version>-macos-x64.dmg   / .zip   (Intel)
# The app is self-contained (no .NET install needed) and ad-hoc signed (not notarized yet).
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/Directory.Build.props | head -1)
OUT=dist-mac
WORK=artifacts/mac
rm -rf "$OUT" "$WORK"
mkdir -p "$OUT" "$WORK"
echo "== Claude Manager $VERSION for macOS"

# App icon (.icns) from the 1024 px PNG.
ICONSET="$WORK/AppIcon.iconset"
mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z $s $s assets/app-1024.png --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s * 2)) $((s * 2)) assets/app-1024.png --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$WORK/AppIcon.icns"

HOST_ARCH=$(uname -m)
for RID in osx-arm64 osx-x64; do
  ARCH=${RID#osx-}
  echo "== $RID"
  PUB="$WORK/publish-$ARCH"
  dotnet publish src/ClaudeManager.Mac/ClaudeManager.Mac.csproj -c Release -r "$RID" --self-contained true \
    -p:UseAppHost=true -p:DebugType=none -p:DebugSymbols=false -o "$PUB"

  APP="$WORK/$ARCH/Claude Manager.app"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp -R "$PUB/." "$APP/Contents/MacOS/"
  cp "$WORK/AppIcon.icns" "$APP/Contents/Resources/"
  sed "s/__VERSION__/$VERSION/g" packaging/macos/Info.plist > "$APP/Contents/Info.plist"
  plutil -lint "$APP/Contents/Info.plist"
  chmod +x "$APP/Contents/MacOS/ClaudeManager"

  # Ad-hoc signature: required for arm64 binaries to run at all. (Notarization needs an Apple Developer ID.)
  codesign --force --deep --sign - "$APP"
  codesign --verify --deep --strict "$APP"

  # Self-test on the matching architecture.
  if { [ "$HOST_ARCH" = "arm64" ] && [ "$ARCH" = "arm64" ]; } || { [ "$HOST_ARCH" = "x86_64" ] && [ "$ARCH" = "x64" ]; }; then
    "$APP/Contents/MacOS/ClaudeManager" --selftest | tee "$WORK/selftest-$ARCH.txt"
    grep -q "ALL PASSED" "$WORK/selftest-$ARCH.txt"
  fi

  # Disk image (drag to Applications) and a zip.
  STAGE="$WORK/dmg-$ARCH"
  mkdir -p "$STAGE"
  cp -R "$APP" "$STAGE/"
  ln -s /Applications "$STAGE/Applications"
  cp LICENSE "$STAGE/LICENSE.txt"
  hdiutil create -volname "Claude Manager" -srcfolder "$STAGE" -ov -format UDZO "$OUT/ClaudeManager-$VERSION-macos-$ARCH.dmg" >/dev/null
  (cd "$WORK/$ARCH" && ditto -c -k --keepParent "Claude Manager.app" "../../../$OUT/ClaudeManager-$VERSION-macos-$ARCH.zip")
done

echo "== Done"
ls -lh "$OUT"
