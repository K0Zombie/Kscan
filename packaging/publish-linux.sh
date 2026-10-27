#!/usr/bin/env bash
# 在 Linux 上生成含 GUI + CLI 的 self-contained 目录式 tar.gz。
set -euo pipefail
project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
destination="${1:-$project_root/artifacts}"
rid="${2:-linux-x64}"
case "$rid" in linux-x64|linux-arm64) ;; *) echo '支持 linux-x64 / linux-arm64' >&2; exit 2;; esac
version="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$project_root/Directory.Build.props")"
name="kscan-$version-$rid"
output="$destination/$name"
[[ ! -e "$output" && ! -e "$destination/$name.tar.gz" ]] || { echo "产物已存在：$output" >&2; exit 2; }
mkdir -p "$output"
dotnet publish "$project_root/CLI" -c Release -r "$rid" --self-contained true -o "$output/cli"
dotnet publish "$project_root/GUI" -c Release -r "$rid" --self-contained true -o "$output/gui"
cp "$project_root/packaging/LINUX-README.md" "$output/README.md"
cp -a "$project_root/packaging/arch/gui-extra/usr" "$output/desktop-files"
find "$output" -type d -exec chmod 755 {} +
find "$output" -type f -exec chmod 644 {} +
chmod +x "$output/cli/kscan" "$output/gui/kscan-gui"
tar -C "$destination" -czf "$destination/$name.tar.gz" "$name"
(cd "$destination" && sha256sum "$name.tar.gz" > "$name.tar.gz.sha256")
echo "$destination/$name.tar.gz"
