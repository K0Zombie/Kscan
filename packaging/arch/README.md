# Linux 打包（Arch / pacman）

面向 niri / Wayland 的 Arch 用户。目标是 **`pacman -U` 装上就能用**，不需要自己配 .NET 环境。

## 一条命令构建安装

```bash
cd Kscan/packaging/arch
makepkg -si
```

前置：`base-devel`、`dotnet-sdk`（构建期需要），以及能访问 nuget.org 还原包。

> 首次构建会还原 NuGet 包并下载 `linux-x64` 的 .NET 运行时包，**可能要几分钟**（之后增量会快很多）。
>
> 不想让 `makepkg` 在源码树里留下 `src/`、`pkg/`、`*.pkg.tar.zst`，可以把中间产物丢到临时目录：
> ```bash
> BUILDDIR=/tmp/makepkg PKGDEST=~/pkgs makepkg -si
> ```
> （`.gitignore` 里也已经忽略了这些路径。）

装完：

```bash
kscan formats                                   # 看支持哪些码制
kscan decode ~/图片.png                          # 识别
kscan gen "https://example.com" ~/二维码.png      # 生成
kscan capture --region --scan                    # 框选取消不会继续截图
```

## 包布局（Arch 惯例）

```
/usr/lib/kscan/     ← 发布产物整份（目录式，非单文件）
/usr/bin/kscan      ← 指向 ../lib/kscan/kscan 的符号链接
```

## 两种打包方式

`PKGBUILD` 顶部有开关 `_self_contained`：

| 值 | 含义 | 体积（实测） | 依赖 |
|---|---|---|---|
| `true`（默认） | 把 .NET 运行时打进包 | 装完约 **92MB**（195 个文件）；`.pkg.tar.zst` 约 35-40MB | 只需 `glibc` |
| `false` | 依赖系统运行时 | 约 2MB | 额外需要 `dotnet-runtime` |

Arch 是滚动更新的，`dotnet-runtime` 版本变化较频繁，所以默认选 self-contained —— 装完即用、不会被系统运行时升级影响。想要小包就改成 `false`。

## 运行期可选依赖

不装也能识别/生成，只是少了截图与剪贴板通路：

| 包 | 用途 |
|---|---|
| `grim` + `slurp` | 框选截图（niri / sway / Hyprland 上最稳） |
| `wl-clipboard` | 剪贴板图片（`wl-copy` / `wl-paste`） |
| `flameshot` | 另一条截图路线；**多显示器 + niri 下不可用**，见 `Kscan/docs/DESIGN.md` §3.3 |

## niri 绑键

`niri` 的 `spawn` **不走 shell**，管道要包一层脚本或写进 `~/.local/bin` 里：

```bash
# ~/.local/bin/qr-shot
#!/bin/sh
text=$(kscan capture --region --scan) || exit $?
printf '%s' "$text" | wl-copy
notify-send '已识别并复制到剪贴板' "$text"
```
```kdl
# ~/.config/niri/config.kdl
binds {
    Mod+Shift+S { spawn "qr-shot"; }
    Mod+Shift+V { spawn-sh "wl-paste --type image | kscan decode -"; }
}
```

## 待办

- GUI 端**已完成**：`gui-extra/` 下是 reverse-DNS 的 `io.github.k0zombie.kscan.desktop`（含 `StartupWMClass=kscan-gui`）与 256px 图标，
  `make-pacman-package.sh` 用 `EXTRA_DIR=./gui-extra` 把它们并进 GUI 包。注意：`PKGBUILD` 目前只打 **CLI** 一个包，
  GUI 包是由 `make-pacman-package.sh` 从预编译产物直接生成的（见本目录 `../../docs/DESIGN.md` §9）。
- 若把项目推到 Git 仓库，`PKGBUILD` 的 `source` 应从当前"直接用工作树"改为 `git+https://…` 并配 `pkgver()`。
