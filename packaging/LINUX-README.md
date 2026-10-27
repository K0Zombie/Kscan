# Kscan 0.1.1 · Linux

解压后保留整个目录；无需安装 .NET。CLI：`./cli/kscan`；GUI：`./gui/kscan-gui`。
GUI 需要 fontconfig、可用字体、X11/XWayland 及 libICE/libSM；Arch 可安装 `fontconfig ttf-dejavu libice libsm libx11 libxext libxrandr libxcursor libxi`。
截图可选依赖：`grim slurp`；通知：`libnotify`；剪贴板管道：`wl-clipboard`。

```bash
./cli/kscan version
./cli/kscan doctor
./cli/kscan gen 'Linux 扫码验证' -o /tmp/kscan-test.png
./cli/kscan decode /tmp/kscan-test.png --json
./cli/kscan capture --region --scan
./cli/kscan capture --desktop -o /tmp/kscan-desktop.png
```

`doctor` 输出实际能力、配置路径、IPC 端点。缺少截图工具会明确报告；取消 slurp 不再触发 grim。默认配置在 `$XDG_CONFIG_HOME/kscan/settings.json` 或 `~/.config/kscan/settings.json`。

## niri binds

使用真实安装目录替换 `/opt/kscan`。GUI 已运行时转发命令，首次启动也会执行 `--capture`。

```kdl
binds {
    Mod+Shift+Q { spawn "/opt/kscan/gui/kscan-gui"; }
    Mod+Shift+S { spawn "/opt/kscan/gui/kscan-gui" "--capture"; }
}
```

也可先运行 GUI，再执行 `./cli/kscan toggle --capture`（无 GUI 时退出码 3）。CLI 的 `capture --scan` 在终端输出识别文本；GUI「截图并识别」在单结果时复制识别文本。

## 验证范围

Arch WSL 已验证 CLI 生成/识别、Unix socket、截图取消与子进程取消。WSLg 可用于 GUI 启动测试，不能代替 niri 验收。
**原生 layer-shell OVERLAY 贴图尚未实现**；现为 Avalonia 普通窗口降级，在 Wayland 上不保证绝对定位、全屏上方置顶或鼠标穿透。不能将这些能力视为已完成。

在真实 niri 桌面验证：启动 GUI；执行两次 `./gui/kscan-gui --capture`，分别框选和 Esc 取消；核对只存在一个 GUI 进程、取消不截图、识别文本进入剪贴板。再测试多屏、缩放与全屏窗口前贴图，并记录未满足的项。
