# Kscan

跨平台的二维码 / 条码识别、生成与桌面截图工具。Windows 10/11 功能完整；Linux（Wayland / X11）提供截图、识别、生成、滚动截图和贴图，受桌面协议限制。
<img src="docs/assets/icon.png" width="112" alt="Kscan icon">

## 🔴 AI生成

内容由deepseek-flashV4.1与codex协作完成

## 功能

- **识别**：二维码、Data Matrix、Aztec、PDF417 与常见一维码；支持图片、剪贴板、拖放和框选截图。
- **生成**：15 种码制，支持 PNG / SVG / 终端字符画、颜色、纠错级别、静区和中心 Logo。
- **截图**：窗口 / 元素识别、多显示器与跨屏框选、光标标记、滚动截图与双向拼接。
- **标注**：矩形、箭头、文字、马赛克、画笔、序号、撤销与导出，可插入时间文字。
- **贴图**：把截图、图片、GIF / 动画 WebP、Markdown 钉在屏幕上；缩放、旋转、分组、鼠标穿透。
- **录制（Windows）**：GIF、MP4、M4A，系统声音 / 麦克风 / 混合音源，FFmpeg 由用户自行安装。
- **自动化**：全局热键、鼠标手势、自定义命令、CLI、单实例 IPC 和文件命名模板。
- **快捷键**：绝大部分功能都需要快捷键打开，根据自己的需要设置快捷键符合自己的操作习惯
所有识别与图像处理都在本地完成，不上传图片，不包含遥测。剪贴板监听、自动打开链接、远程图片贴图等敏感能力默认关闭。

## 实用
- **录制gif**：可以随时随地录制动图，制作朋友的表情包，游戏里面的破绷时刻

- **贴图管理**：可以持久化需要反复使用的贴图，可以是一周的课表，可以是一些喜欢的图片/动图

<img width="775" height="343" alt="gif_20261112_104517774_003" src="https://github.com/user-attachments/assets/bc72e69a-958a-42c3-8255-b4ec51b313b9" />


- **多屏截图**：跨屏框选功能，按照Windows的布局拼接

<img width="4480" height="1445" alt="screenshot_20261112_105008656_007" src="https://github.com/user-attachments/assets/2b56ad0e-e375-4e9d-8345-02c010fc50d9" />
 
## 快速开始

### 使用发布包

从 Releases 下载 Windows 便携包，完整解压后运行 `kscan-gui.exe`。不要只复制 exe，旁边的 DLL、原生库和资源都需要保留。录制需要另外安装 FFmpeg 并加入 `PATH`，或在设置中填写 `ffmpeg.exe` 的完整路径。

### 从源码构建

需要 .NET 10 SDK。

```powershell
dotnet build Kscan.slnx -c Release
dotnet test  Kscan.slnx -c Release
dotnet run --project GUI/Kscan.App.csproj -c Release
```

Windows 便携发布：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File packaging/publish-windows.ps1
```

Linux 发布与 Arch 打包见 [`packaging/`](packaging/)。

### 命令行

```powershell
kscan decode shot.png
kscan gen "https://example.com" -o qr.png
kscan build wifi --ssid MyWiFi --password 12345678 -o wifi.png
kscan capture --region --scan
kscan doctor
```

完整选项见 [`docs/manual/命令行参考.md`](docs/manual/命令行参考.md)。

## 平台支持

| 能力 | Windows 10/11 | Linux Wayland / X11（有很多问题，目前不打算修，linux上有更好的替代） |
| --- | --- | --- |
| 识别 / 生成 / CLI | ✅ | ✅ |
| 框选截图 | ✅ 原生 | ✅ 通过 `grim` + `slurp` 等外部工具 |
| 滚动截图 / 标注 | ✅ | ✅ 基础能力 |
| 贴图置顶 / 穿透 | ✅ | ⚠️ 取决于合成器（niri / Sway / Hyprland 较好） |
| 全局热键 | ✅ 应用内注册 | ⚠️ 由桌面环境绑定 CLI |
| 托盘 | ✅ | ⚠️ Wayland 无统一托盘协议 |
| 录制 | ✅ | — |

Linux 需要 `grim`、`slurp`、D-Bus 通知；可选 `wl-clipboard`。细节与限制见 [`docs/manual/版本说明.md`](docs/manual/版本说明.md)。

## 目录结构

```text
CLI/                命令行入口（kscan）
GUI/                Avalonia 桌面端（kscan-gui）
src/Kscan.Core/     识别、生成、图像处理、配置与管线
src/Kscan.Platform/ Windows / Linux 平台适配
tests/              单元测试、界面测试与原生冒烟检查
docs/manual/        用户手册（Markdown + 离线 HTML）
docs/examples/      CLI、截图归档与主题示例
packaging/          Windows / Linux 发布与 Arch 打包
scripts/            帮助与图标生成脚本
```

## 文档

- [用户手册](docs/manual/index.md)（[离线 HTML](docs/manual/index.html)）
- [设计文档](docs/DESIGN.md)
- [示例与集成](docs/examples/README.md)
- [版本与支持范围](docs/manual/版本说明.md)

## 隐私

Kscan 不包含账号系统、云同步或遥测。配置、贴图库和录制文件都保存在本机；便携模式使用程序目录下的 `data/`。剪贴板监听、自动打开 URL、下载剪贴板中的图片地址、开机自启等行为都需要用户显式开启。

## 许可证

[MIT](LICENSE)
