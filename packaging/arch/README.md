# Arch 打包与安装

本 PKGBUILD 构建的是 CLI，不包含完整 Windows GUI、录制或便携包。

在 packaging/arch 下运行 `makepkg -si`。需要 base-devel、适配项目的 .NET 10 SDK，以及首次还原 NuGet 和运行时包的网络条件。也可设置 BUILDDIR 和 PKGDEST，把中间产物放到独立目录。

默认 `_self_contained=true`，发布目录包含运行时；设为 false 时需匹配的系统 .NET 运行时。依赖与最终体积以当前构建结果为准，不再以旧版实测大小作保证。

发布文件安装到 /usr/lib/kscan，/usr/bin/kscan 是符号链接；许可文件按 PKGBUILD 安装。运行 `kscan version`、`kscan doctor` 验证。框选截图需要合成器支持及 grim/slurp 等依赖；剪贴板管道可使用发行版的 wl-clipboard。

使用方法见 [CLI参考](../../docs/manual/命令行参考.md)，平台边界见 [Linux说明](../LINUX-README.md)。本轮改动未在 Arch 真机重测安装与桌面集成。