# Linux 使用与平台边界

Windows 便携包不能直接作为 Linux 原生包。Linux 发布通常分为 cli/kscan 和 gui/kscan-gui；完整保留发布目录，自包含版本不要求另装 .NET 运行时。源码构建需要 .NET 10 SDK。

CLI 先运行 `kscan version`、`kscan doctor`，再用 `kscan gen hello -o test.png` 和 `kscan decode test.png` 自检。具体选项见 [命令行参考](../docs/manual/命令行参考.md)。

GUI 依赖可用的 fontconfig、字体和 X11/XWayland 支持，具体依赖以发行版为准。Wayland 截图可能需要 grim/slurp；CLI 的诊断输出反映实际能力。取消框选不会继续截图。缺少截图依赖不影响已有图片的识别和生成。

niri 等合成器可将实际安装路径的 `kscan-gui --capture` 绑定到系统快捷键；已有 GUI 时也可使用 `kscan toggle --capture`。没有运行实例时 toggle 返回 3。

原生 layer-shell OVERLAY 贴图尚未实现，目前是 Avalonia 普通窗口降级；Wayland 上不保证绝对定位、穿透或盖过独占全屏。不要把 WSL/WSLg 验证当作实际 niri 多屏验收。

本录制分支的 gdigrab、WASAPI、录制图层为 Windows 实现，Linux 不提供同等录制入口。新命名引擎是跨平台代码，但平台捕获能力仍受各自实现限制。正常配置通常在 `$XDG_CONFIG_HOME/kscan` 或 `~/.config/kscan`，以 doctor 和设置页为准。