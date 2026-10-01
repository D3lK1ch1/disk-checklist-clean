#!/usr/bin/env bash
# Packages DiskCleanup.Avalonia as an ad-hoc-signed .app inside a .dmg (Apple Silicon only).
# Must run on macOS - codesign and hdiutil are Mac-only tools.
#
# Unlike Tauri (AccountabilityApp), `dotnet publish` only produces a folder of binaries,
# so this script builds the .app bundle, signs it and makes the .dmg itself.
#
# Usage (from anywhere): bash scripts/package-mac.sh
# Output: publish/mac/DiskCleanup_<version>_aarch64.dmg
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/DiskCleanup.Avalonia/DiskCleanup.Avalonia.csproj"
OUT="$ROOT/publish/mac"
APP_NAME="Disk Cleanup"
EXE_NAME="DiskCleanup.Avalonia"   # default AssemblyName = project name

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT")"
if [ -z "$VERSION" ]; then
    echo "No <Version> found in $PROJECT" >&2
    exit 1
fi

rm -rf "$OUT"
mkdir -p "$OUT"

# 1. Publish. -f net10.0 picks the Mac/Linux TFM (the project also targets net10.0-windows).
dotnet publish "$PROJECT" -c Release -f net10.0 -r osx-arm64 --self-contained true \
    -o "$OUT/publish"

# 2. Build the .app bundle: Contents/MacOS holds the binaries, Info.plist tells macOS
#    which file to launch (CFBundleExecutable).
APP="$OUT/$APP_NAME.app"
mkdir -p "$APP/Contents/MacOS"
cp -R "$OUT/publish/." "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/$EXE_NAME"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>com.d3lk1ch1.diskcleanup</string>
    <key>CFBundleExecutable</key>
    <string>$EXE_NAME</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
PLIST

# 3. Ad-hoc sign ("-s -" = no Developer ID). Apple Silicon kills unsigned arm64 binaries
#    outright; ad-hoc signing turns that into the normal "Apple could not verify" Gatekeeper
#    prompt, which the README's Open Anyway / xattr steps get past.
#    No --options runtime: hardened runtime blocks .NET's JIT without extra entitlements.
codesign --force --deep -s - "$APP"
codesign --verify --deep --strict "$APP"

# 4. Make the .dmg. The Applications symlink gives the usual drag-to-install window.
STAGING="$OUT/dmg-staging"
mkdir -p "$STAGING"
cp -R "$APP" "$STAGING/"
ln -s /Applications "$STAGING/Applications"

DMG="$OUT/DiskCleanup_${VERSION}_aarch64.dmg"
hdiutil create -volname "$APP_NAME" -srcfolder "$STAGING" -ov -format UDZO "$DMG"

echo ""
echo "Built: $DMG"
