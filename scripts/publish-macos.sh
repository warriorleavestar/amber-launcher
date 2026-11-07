#!/bin/bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
case "${1:-$(uname -m)}" in
    arm64|osx-arm64) rid=osx-arm64 ;;
    x86_64|osx-x64) rid=osx-x64 ;;
    *) echo "Usage: $0 [osx-arm64|osx-x64]" >&2; exit 2 ;;
esac

output="$root/publish/$rid"
app="$output/Jay.app"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
bundle="$stage/Jay.app"
mkdir -p "$bundle/Contents/MacOS"
dotnet publish "$root/src/Amber.App/Amber.App.csproj" -c Release -r "$rid" \
    --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none -o "$bundle/Contents/MacOS"
mkdir -p "$bundle/Contents/Resources" "$stage/Jay.iconset"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$root/src/Amber.App/Assets/jay.png" \
        --out "$stage/Jay.iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$root/src/Amber.App/Assets/jay.png" \
        --out "$stage/Jay.iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$stage/Jay.iconset" -o "$bundle/Contents/Resources/Jay.icns"
cat > "$bundle/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key><string>Jay</string>
    <key>CFBundleIdentifier</key><string>io.github.amber.launcher</string>
    <key>CFBundleName</key><string>Jay</string>
    <key>CFBundleIconFile</key><string>Jay.icns</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
plutil -lint "$bundle/Contents/Info.plist"
"$root/scripts/bundle-easytier.sh" "$rid" "$bundle/Contents/Resources/easytier"
codesign --force --sign - "$bundle"
codesign --verify --deep --strict "$bundle"
mkdir -p "$output"
if [[ -d "$app" ]]; then rm -r "$app"; fi
ditto "$bundle" "$app"
ditto -c -k --keepParent "$bundle" "$output/Jay-$rid.zip"
printf '%s\n' "$app"
