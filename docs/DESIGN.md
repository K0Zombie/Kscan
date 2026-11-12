# Kscan 设计文档

本文记录 Kscan 的模块划分、关键数据流和平台取舍。具体版本状态以 `Directory.Build.props`、README 和用户手册为准。

## 1. 目标

Kscan 把“看到码 → 读出来 → 处理结果”和“把内容变成码 → 导出”放在同一个桌面工具里，并尽量复用截图、剪贴板和贴图能力：

1. 识别二维码与常见一维/二维条码，结果可复制、打开或继续处理。
2. 生成条码，支持 PNG、SVG 和终端字符画。
3. 框选截图、窗口/元素识别、多显示器与滚动截图。
4. 把截图、图片、GIF 和 Markdown 钉在屏幕上。
5. Windows 下录制 GIF、视频和音频。
6. CLI 与 GUI 共用 Core，便于脚本和自动化。

不做：云同步、账号体系、图片库管理、摄像头扫码、扫描枪键盘模拟。

## 2. 解决方案结构

```text
Kscan.slnx
├─ CLI/                 命令行入口，AssemblyName = kscan
├─ GUI/                 Avalonia 桌面端，AssemblyName = kscan-gui
├─ src/Kscan.Core/      与平台无关的识别、生成、图像、配置、管线
├─ src/Kscan.Platform/  Windows / Linux 平台适配与单实例 IPC
├─ tests/               单元测试、Headless UI 测试、原生冒烟检查
├─ docs/                设计文档、用户手册、示例
├─ packaging/           Windows / Linux 发布与 Arch 打包
└─ scripts/             帮助与图标生成
```

依赖方向固定为：

```text
CLI ─┐
     ├─→ Kscan.Core ←─ Kscan.Platform
GUI ─┘
```

`Kscan.Core` 不引用 Avalonia，也不直接调用 Win32 / X11；平台差异通过 `Kscan.Platform` 的接口注入。

## 3. 运行时分层

### 3.1 GUI

- `App.axaml.cs` 负责生命周期、托盘、单实例命令和主题初始化。
- `ViewModels/` 保存页面状态，命令通过 `CommunityToolkit.Mvvm` 暴露。
- `Views/` 只做布局与输入；复杂交互（框选、标注、贴图、录制选择器）放在独立窗口/控件中。
- `Services/` 是 GUI 侧的应用服务：剪贴板、贴图管理、热键、录制、命名、主题、日志等。

### 3.2 Core

- `Model/`：`ImageFrame`、`ScanResult`、`BarcodeContent` 等值对象。
- `Imaging/`：Skia 编解码、颜色、缩放、滤镜、滚动拼接、Logo 叠加、字符画。
- `Pipeline/`：识别管线、预处理配方、方法存储。
- `Configuration/`：`AppSettings`、命名/录制/热键配置与 JSON 存储。
- `Engines/`：条码引擎适配；当前默认是纯托管的 ZXing.Net。

### 3.3 Platform

- Windows：BitBlt / PrintWindow 截图、窗口几何、UIA、全局热键、托盘、注册表自启、具名管道。
- Linux：`grim` + `slurp` 截图、通知、Unix domain socket、自启动 `.desktop`。
- 平台能力通过 `PlatformServices.Detect()` 暴露给界面，缺失能力在设置页显示原因。

## 4. 识别管线

```text
输入 ImageFrame
  → 预处理候选（原图 / 灰度 / 对比度 / 去噪 / 自适应阈值 …）
  → 引擎链（当前为 ZXing）
  → 第一个有结果的层立即返回
  → ScanResult[]（文本、码制、点位、耗时、尝试记录）
```

关键点：

- `ScanPipeline` 是回退链：引擎按注册顺序尝试，某个引擎成功即返回。
- `PreprocessRunner` 流式生成候选层，不保留整条历史，降低大图峰值内存。
- `TryDenoise=false` 时只尝试原图；默认会逐步尝试更“重”的预处理。
- 识别结果包含多个码时全部返回；界面负责高亮和结果操作。
- `MethodStore` 把用户调好的预处理步骤保存为 JSON，可复用和分享。

## 5. 生成管线

```text
内容（文本 / WiFi / vCard / URL / 邮件 / 电话 / SMS / 地理坐标）
  → BarcodeEncoder（ZXing）
  → BarcodeMatrix（模块矩阵）
  → PNG / SVG / ASCII 渲染
```

- 2D 与 1D 使用不同的尺寸/行高规则；1D 字符画默认重复多行，保证可扫。
- `BarcodeEncoder.ValidateContent` 在生成前检查固定长度、数字约束和校验位。
- Logo 叠加使用自写双线性缩放和白色衬底；建议使用高纠错级别并实际扫码验证。
- SVG 输出矢量模块，不包含位图 Logo。

## 6. 截图、标注与贴图

### 6.1 框选

1. 抓取光标所在屏幕或整个虚拟桌面。
2. 全屏遮罩显示定格画面，支持窗口/元素高亮。
3. 鼠标拖动自由框选，单击窗口截整窗，Esc 取消。
4. 裁剪为 `ImageFrame`，交给识别、复制、贴图或标注。

跨屏模式按物理像素拼接；不同 DPI 的屏幕不统一压成某个屏的尺寸。屏幕布局在框选过程中变化时，本次操作取消。

### 6.2 标注

`AnnotationDocument` 保存图元列表（矩形、箭头、文字、马赛克、画笔、序号），`AnnotationCanvas` 负责绘制与命中测试。导出时把图元渲染回位图；时间文字是普通文字图元，可再次编辑。

### 6.3 贴图

- Windows：无边框置顶窗口，支持拖动、滚轮缩放、旋转、透明度、双击关闭、右键菜单、鼠标穿透。
- Linux：优先 layer-shell `OVERLAY`，按合成器能力降级为普通窗口。
- `PinManager` 维护屏幕上的贴图；`PinLibraryService` 维护分组与库文件。
- 剪贴板贴图支持图片、GIF / 动画 WebP、文件项和 Markdown 文本。

## 7. 录制（Windows）

```text
设置（类型 / 帧率 / 音源 / 目录）
  → 实时框选（不冻结桌面）
  → FFmpeg 子进程（gdigrab / dshow / wasapi）
  → .partial 临时文件
  → 正常停止后改名为最终文件
```

- 视频 MP4/H.264，音频 M4A/AAC，GIF 无声音。
- 编码器输出首帧后才显示控制窗和计时，不用固定倒计时冒充“正在录制”。
- 画面时间文字是独立透明置顶图层，不会被录制控件/边框捕获，但可能被重叠选区捕获。
- 多选区各自一个会话；停止全部会逐个收尾。
- FFmpeg 不随包分发，由用户安装或在设置中指定路径。

## 8. 配置与数据

- 普通模式：Windows `%APPDATA%/kscan`，Linux `$XDG_CONFIG_HOME/kscan`。
- 便携模式：程序目录下的 `data/`，由同目录的 `portable` 标记启用。
- `settings.json` 使用 `System.Text.Json`，未知字段忽略、缺失字段回退默认值。
- 写盘使用同目录临时文件 + 原子替换，避免半截 JSON。
- 配置结构带 `version`，读取时执行一次性迁移（旧标量热键、默认新动作、光标标记默认值等）。
- 用户主题、命令、识别方法、贴图库都在 `user/` 下。

## 9. 平台取舍

| 能力 | Windows | Linux |
| --- | --- | --- |
| 截图 | BitBlt / PrintWindow | 外部工具（默认 grim + slurp） |
| 全局热键 | `RegisterHotKey` 消息线程 | 桌面环境绑定 CLI，应用内不注册 |
| 单实例 | Mutex + 具名管道 | 锁文件 + Unix domain socket |
| 托盘 | Shell 托盘 | Wayland 无统一协议，不依赖托盘 |
| 贴图置顶/穿透 | 原生窗口样式 | layer-shell，能力探测失败则降级 |
| 录制 | gdigrab / WASAPI | 不支持 |

Wayland 的 core 协议不提供窗口绝对定位和 z-order；因此 Linux 贴图不承诺与 Windows 等价。GNOME 等不支持 layer-shell 的环境会降级。

## 10. 构建与测试

```text
dotnet build Kscan.slnx -c Release
dotnet test  Kscan.slnx -c Release
```

- `tests/Kscan.Core.Tests`：识别、生成、图像、配置、管线。
- `tests/Kscan.Platform.Tests`：热键解析、窗口几何、单实例通道、命令启动。
- `tests/Kscan.App.Tests`：Avalonia Headless + 真实 Skia 绘制，覆盖标注、贴图、录制、主题。
- `tests/Kscan.*.NativeChecks`：Windows 原生窗口/录制冒烟，需交互桌面。

发布：`packaging/publish-windows.ps1`、`packaging/publish-linux.sh`、`packaging/arch/`。详细步骤见 `packaging/` 内 README。

## 11. 已知限制

- 摄像头扫码、扫描枪键盘模拟、历史记录/批量识别不在当前范围。
- 滚动拼接依赖画面重叠；动态页面、固定悬浮元素和极端缩放会影响拼接。
- 跨屏/高 DPI 录制的帧率与同步取决于硬件和编码器。
- 受保护内容、独占全屏、DRM 画面可能无法捕获。
- Linux 的全局热键、托盘、置顶/穿透能力由桌面环境决定。
- 识别二维码不等于绕过目标服务的登录或授权流程。
