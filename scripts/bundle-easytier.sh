#!/bin/bash
# 把固定版本的 EasyTier 官方安装包放进发布目录，用户不用再联网下载联机组件。
# 版本、大小和 SHA-256 都从 src/Amber.Core/Multiplayer/EasyTier.cs 读取，和程序里的校验值是同一份，对不上就失败。
# 用法：scripts/bundle-easytier.sh <rid> <目标目录>，例如 scripts/bundle-easytier.sh win-x64 out/win-x64/easytier
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
source_file="$root/src/Amber.Core/Multiplayer/EasyTier.cs"
rid="${1:?usage: $0 <rid> <dir>}"
dest="${2:?usage: $0 <rid> <dir>}"

case "$rid" in
    win-x64) key='("windows", Architecture.X64)' ;;
    win-arm64) key='("windows", Architecture.Arm64)' ;;
    win-x86) key='("windows", Architecture.X86)' ;;
    osx-x64) key='("macos", Architecture.X64)' ;;
    osx-arm64) key='("macos", Architecture.Arm64)' ;;
    linux-x64) key='("linux", Architecture.X64)' ;;
    linux-arm64) key='("linux", Architecture.Arm64)' ;;
    *) echo "unsupported rid: $rid" >&2; exit 2 ;;
esac

version="$(sed -n 's/.*public const string Version = "\([0-9.]*\)";.*/\1/p' "$source_file" | head -n 1)"
line="$(grep -F "$key =>" "$source_file" | head -n 1)"
platform="$(sed -n 's/.*A("\([a-z0-9_-]*\)".*/\1/p' <<<"$line")"
sha="$(sed -n 's/.*A("[^"]*", "\([0-9a-f]\{64\}\)".*/\1/p' <<<"$line")"
size="$(sed -n 's/.*A("[^"]*", "[0-9a-f]*", \([0-9_]*\).*/\1/p' <<<"$line" | tr -d _)"
if [[ -z "$version" || -z "$platform" || -z "$sha" || -z "$size" ]]; then
    echo "could not read EasyTier pin for $rid from $source_file" >&2
    exit 1
fi

name="easytier-$platform-v$version.zip"
url="https://github.com/EasyTier/EasyTier/releases/download/v$version/$name"
mkdir -p "$dest"
curl -fL --retry 5 --retry-all-errors --connect-timeout 20 -o "$dest/$name.tmp" "$url"

actual_size="$(wc -c < "$dest/$name.tmp" | tr -d ' ')"
if command -v sha256sum >/dev/null; then actual_sha="$(sha256sum "$dest/$name.tmp" | cut -d' ' -f1)"
else actual_sha="$(shasum -a 256 "$dest/$name.tmp" | cut -d' ' -f1)"; fi
if [[ "$actual_size" != "$size" || "$actual_sha" != "$sha" ]]; then
    echo "EasyTier package mismatch: size $actual_size (want $size), sha256 $actual_sha (want $sha)" >&2
    rm -f "$dest/$name.tmp"
    exit 1
fi
mv "$dest/$name.tmp" "$dest/$name"

cat > "$dest/README.txt" <<TXT
EasyTier $version ($platform)

这是开源组网工具 EasyTier 的官方发布包，未经修改，Jay 的联机功能用它。
This is the unmodified official release of EasyTier, used by Jay for multiplayer.

License: LGPL-3.0
Source:  https://github.com/EasyTier/EasyTier
Package: $url
SHA-256: $sha
TXT
echo "bundled $name"
