#!/usr/bin/env bash
# 从**已发布的产物目录**生成标准的 pacman 包（.pkg.tar.zst）。
#
# 为什么不用 makepkg：makepkg 的 build() 需要 dotnet-sdk，而 WSL 里未必装。
# 这里两边分工——Windows/CI 负责 dotnet publish，本脚本只负责"装包"，
# 因此只需要 bsdtar + zstd（Arch 的 base 里就有，路径通常在 /usr/sbin）。
#
# 用法：
#   ./make-pacman-package.sh <包名> <版本> <产物目录> <输出目录> [依赖...]
# 例：
#   ./make-pacman-package.sh kscan 0.1.0 /mnt/d/kscan/linux-x64 /mnt/d/kscan glibc
#
# 产物布局（Arch 惯例）：
#   /usr/lib/<包名>/    整份发布产物
#   /usr/bin/<包名>     指向 ../lib/<包名>/<包名> 的符号链接
#
# 环境变量：
#   EXTRA_DIR  可选。一棵额外的文件树，按原样合并进包根 —— GUI 包用它塞
#              .desktop 与图标：
#                EXTRA_DIR=./gui-extra ./make-pacman-package.sh kscan-gui 0.1.0 ...

set -euo pipefail

pkgname="${1:?用法: $0 <包名> <版本> <产物目录> <输出目录> [依赖...]}"
pkgver="${2:?缺少版本}"
artifact_dir="${3:?缺少产物目录}"
out_dir="${4:?缺少输出目录}"
shift 4
depends=("$@")

pkgrel=1
arch=x86_64

if [[ ! -x "$artifact_dir/$pkgname" ]]; then
    echo "错误：$artifact_dir/$pkgname 不存在或不可执行（先跑 dotnet publish）" >&2
    exit 1
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

root="$work/root"
mkdir -p "$root/usr/lib/$pkgname" "$root/usr/bin"

# 产物整份装进 /usr/lib/<pkgname>
cp -a "$artifact_dir/." "$root/usr/lib/$pkgname/"

# /usr/bin 里放符号链接（相对路径，符合 Arch 惯例）
ln -s "../lib/$pkgname/$pkgname" "$root/usr/bin/$pkgname"

# 可选：额外文件树（GUI 包的 .desktop 与图标走这里）
if [[ -n "${EXTRA_DIR:-}" ]]; then
    if [[ ! -d "$EXTRA_DIR" ]]; then
        echo "错误：EXTRA_DIR=$EXTRA_DIR 不存在" >&2
        exit 1
    fi
    cp -a "$EXTRA_DIR/." "$root/"
fi

# 权限规范化：/mnt 挂载出来的权限位不可靠，这里统一成标准值
find "$root" -type d -exec chmod 755 {} +
find "$root" -type f -exec chmod 644 {} +
chmod 755 "$root/usr/lib/$pkgname/$pkgname"

# ---- .PKGINFO ----
# size 是"安装后占用的字节数"，pacman 用它显示体积
install_size="$(du -sb "$root" | cut -f1)"
{
    echo "pkgname = $pkgname"
    echo "pkgbase = $pkgname"
    echo "pkgver = $pkgver-$pkgrel"
    echo "pkgdesc = 跨平台二维码/条码扫描与生成工具"
    echo "url = https://github.com/1357310795/QrCodeScanner"
    echo "builddate = $(date +%s)"
    echo "packager = K0Zombie"
    echo "size = $install_size"
    echo "arch = $arch"
    echo "license = custom"
    for d in "${depends[@]:-}"; do
        [[ -n "$d" ]] && echo "depend = $d"
    done
} > "$root/.PKGINFO"

cd "$root"

# 包内文件列表（相对路径、NUL 分隔，不含元文件）—— makepkg 内部也是这么拿列表的
list_files() {
    find . -mindepth 1 \
        \( -name '.PKGINFO' -o -name '.MTREE' -o -name '.BUILDINFO' -o -name '.INSTALL' \) -prune -o \
        -printf '%P\0'
}

# ---- .MTREE（文件清单与校验；注意 makepkg 是用 gzip 压这个文件的）----
list_files | LANG=C bsdtar -cnf - --format=mtree \
    --options='!all,use-set,type,uid,gid,mode,time,size,sha256,link' \
    --null --files-from - \
    | gzip -c -f -n > .MTREE

# ---- 打包 ----
# .PKGINFO 必须是第一个条目；uid/gid 必须是 0，否则 pacman 装出来的属主不对
mkdir -p "$out_dir"
package="$out_dir/$pkgname-$pkgver-$pkgrel-$arch.pkg.tar.zst"
{ printf '.PKGINFO\0.MTREE\0'; list_files; } \
    | LANG=C bsdtar --uid 0 --gid 0 --no-fflags -cnf - --null --files-from - \
    | zstd -c -T0 > "$package"

echo "已生成：$package"
echo "  安装体积：$((install_size / 1024 / 1024)) MB（未压缩）"
ls -la "$package"
