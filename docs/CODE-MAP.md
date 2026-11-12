# Kscan 代码地图

面向准备开分支加功能、做优化或修 Bug 的开发者。先读 [`DESIGN.md`](DESIGN.md) 了解整体设计，再用本文定位“改哪个文件、从哪个函数进”。

```text
dotnet build Kscan.slnx -c Release
dotnet test  Kscan.slnx -c Release
```

---

## 1. 仓库结构

```text
Kscan/
├─ Kscan.slnx                    解决方案入口
├─ Directory.Build.props         全解决方案版本号、PDB 裁剪
├─ CLI/
│  └─ Program.cs                 命令行入口（kscan）
├─ GUI/
│  ├─ Program.cs                 Avalonia 启动与单实例入口
│  ├─ App.axaml / App.axaml.cs   生命周期、托盘、主题、命令处理
│  ├─ Assets/                    图标、i18n、便携默认配置
│  ├─ Controls/                  可复用控件（结果浮层）
│  ├─ Services/                  GUI 应用服务（截图、贴图、录制、剪贴板…）
│  ├─ ViewModels/                页面状态与命令
│  └─ Views/                     Avalonia 窗口与页面
├─ src/
│  ├─ Kscan.Core/                与平台/UI 无关的核心逻辑
│  │  ├─ Abstractions/           引擎契约
│  │  ├─ Configuration/          设置、热键、命名、主题
│  │  ├─ Engines/                识别引擎实现
│  │  ├─ Imaging/                图像、生成、滤镜、拼接
│  │  ├─ Model/                  值对象与结果模型
│  │  ├─ Net/                    网络地址安全策略
│  │  ├─ Pins/                   贴图分组模型
│  │  └─ Pipeline/               识别管线、预处理、方法库
│  └─ Kscan.Platform/            平台适配
│     ├─ Abstractions/           平台能力接口
│     ├─ Windows/                Win32 实现
│     └─ Linux/                  Linux / Wayland 实现
├─ tests/
│  ├─ Kscan.Core.Tests/          核心逻辑
│  ├─ Kscan.Platform.Tests/      平台逻辑
│  ├─ Kscan.App.Tests/           Headless UI + 真实 Skia + 录制
│  ├─ Kscan.Pin.NativeChecks/    Windows 原生贴图冒烟
│  └─ Kscan.Record.NativeChecks/ Windows 原生录制/窗口冒烟
├─ docs/
│  ├─ manual/                    用户手册（Markdown + 生成 HTML）
│  ├─ examples/                  CLI / 归档 / 主题示例
│  └─ CODE-MAP.md                本文
├─ packaging/                    Windows / Linux / Arch 发布
└─ scripts/                      帮助与图标生成
```

## 2. 项目依赖

```text
CLI ─┐
     ├─→ Kscan.Core ←─ Kscan.Platform
GUI ─┘
```

- `Kscan.Core` 不引用 Avalonia、不直接调用 Win32。
- `Kscan.Platform` 只做平台适配，不弹 UI 对话框。
- `GUI` 负责编排：读设置、调平台能力、更新 ViewModel、显示窗口。
- `CLI` 与 GUI 共用 Core；不要把业务逻辑写进 `CLI/Program.cs`。

## 3. 启动与主要调用链

### GUI 启动

```text
GUI/Program.Main
  → AppSettingsService.Load
  → 单实例锁 / 命令通道
  → App.Initialize / MainWindow
  → MainViewModel + ScanViewModel / GenerateViewModel / SettingsViewModel
```

### 截图扫码

```text
GlobalHotkeyService
  → MainViewModel / ScanViewModel 命令
  → RegionSelector.SelectAsync
  → IScreenCapture.CaptureDesktopAsync
  → ScanPipeline.Scan
  → ResultActions（复制 / 打开 / 贴图 / 标注）
```

### 生成二维码

```text
GenerateViewModel.BuildPayload
  → BarcodeEncoder.Encode
  → BarcodeRenderer.ToPng / ToSvg / ToAscii
```

### 贴图

```text
PinManager.Pin / PinFromClipboardAsync
  → PinWindow
  → PinLibraryService（分组、库文件、窗口状态）
```

### 录制（Windows）

```text
RecordingManager.StartAsync
  → RecordingRegionSelector.SelectAsync
  → RecordingSession.StartAsync
  → ffmpeg 子进程
  → RecordingControlWindow
```

### 配置保存

```text
SettingsViewModel / 任何设置面板
  → AppSettingsService.UpdateAsync
  → JsonSettingsStore.SaveAsync
  → AppSettings.Migrate（读取时）
```

---

## 4. 核心函数树

```text
ScanPipeline.Scan
├─ LevelsFor / PreprocessRunner.Stream
│  ├─ PreprocessRunner.Apply
│  │  └─ ImageFilters.*
│  └─ ZxingEngine.Decode
└─ ScanOutcome（Results + Attempts）

BarcodeEncoder.Encode
├─ TryParseFormat
├─ ValidateContent
└─ BarcodeMatrix
   └─ BarcodeRenderer.ToPng / ToSvg / ToAscii

PinManager.Pin
├─ PinWindow.SetImage
├─ PinLibraryService.Attach
└─ PinLibraryService.SaveWindowState

RecordingSession.StartAsync
├─ BuildArguments
├─ RecordingAudio / RecordingCursor
└─ ffmpeg 子进程

AppSettingsService.UpdateAsync
├─ AppSettings.Migrate
└─ JsonSettingsStore.SaveAsync
```

## 5. Core 文件与关键函数

| 文件 | 类型 / 入口 | 关键函数 | 什么时候改 |
| --- | --- | --- | --- |
| `Abstractions/IBarcodeEngine.cs` | `IBarcodeEngine` | `Id`、`IsAvailable`、`Capabilities`、`Decode` | 新增识别引擎 |
| `Engines/ZxingEngine.cs` | `ZxingEngine` | `Decode` | 调整默认引擎行为 |
| `Pipeline/ScanPipeline.cs` | `ScanPipeline`、`ScanOutcome` | `CreateDefault`、`Scan` | 改引擎回退链、诊断记录 |
| `Pipeline/PreprocessRunner.cs` | `PreprocessRunner`、`PreprocessLevel` | `Levels`、`Stream`、`Apply` | 新增预处理步骤 / 改渲染顺序 |
| `Pipeline/PreprocessRecipe.cs` | `PreprocessKind`、`PreprocessStep`、`PreprocessRecipe` | `Signature`、`HasSameStepsAs` | 新增步骤参数、方法文件格式 |
| `Pipeline/PreprocessSession.cs` | `PreprocessSession` | `AddStep`、`MoveLevel`、`AdjustParameter`、`UseRecipe` | 识别页“手动处理图像”交互 |
| `Pipeline/MethodStore.cs` | `MethodStore` | `LoadAll`、`Save`、`SuggestName` | 方法库读写 |
| `Imaging/ImageFrame.cs` | `ImageFrame` | `Crop`、`ToGray8`、`Pixels` | 像素格式、裁剪、内存布局 |
| `Imaging/ImageFilters.cs` | `ImageFilters` | `MedianBlur`、`StretchContrast`、`AdaptiveThreshold`、`RemoveColoredOverlay` | 图像增强算法 |
| `Imaging/SkiaImageCodec.cs` | `SkiaImageCodec` | `Load`、`FromEncoded`、`EncodePng`、`EncodePngFast` | 图片编解码 |
| `Imaging/BarcodeEncoder.cs` | `BarcodeEncoder` | `TryParseFormat`、`Encode`、`ValidateContent` | 新增码制、内容校验 |
| `Imaging/BarcodeRenderer.cs` | `BarcodeRenderer` | `ToImageFrame`、`ToPng`、`ToSvg`、`ToAscii` | 改导出格式 |
| `Imaging/AsciiBarcodeRenderer.cs` | `AsciiBarcodeRenderer` | `Render` 及 `AsciiRenderOptions` | 终端字符画 |
| `Imaging/ScrollStitcher.cs` | `ScrollStitcher` | `TryAppend`、`Undo`、`Build`、`Preview` | 滚动截图拼接 |
| `Imaging/LogoOverlay.cs` | `LogoOverlay` | `Overlay` | 中心 Logo 叠加 |
| `Model/BarcodeContent.cs` | `BarcodeContent` | `Wifi`、`VCard`、`Url`、`Email`、`Tel`、`Sms`、`Geo` | 内容模板 |
| `Model/BarcodeFormats.cs` | `BarcodeFormats` | `Writable`、`ReaderUnsupported` | 码制清单 |
| `Model/ScanOptions.cs` | `ScanOptions` | `MultiCode`、`TryHarder`、`TryDenoise` | 识别策略 |
| `Configuration/AppSettings.cs` | `AppSettings` | `Default`、`Migrate`、`MigrateHotkeys` | 新增设置、默认值迁移 |
| `Configuration/JsonSettingsStore.cs` | `JsonSettingsStore` | `Load`、`SaveAsync` | 配置持久化 |
| `Configuration/HotkeyActionIds.cs` | `HotkeyActionIds` | 动作 id 常量 | 新增热键动作 |
| `Configuration/HotkeyBinding.cs` | `HotkeyBinding`、`HotkeyBindings` | 内容相等、JSON 转换 | 热键列表格式 |
| `Configuration/NamingSettings.cs` | `NamingSettings` | `Templates`、`Equals` | 文件命名规则 |
| `Configuration/NameTemplate.cs` | `NameTemplate` | `Render`、`Validate` | 模板语法 |
| `Configuration/RecordingSettings.cs` | `RecordingSettings` | FPS、目录、音源、编码器 | 录制设置 |
| `Configuration/ThemeDefinition.cs` | `ThemeDefinition` | `Dark`、`Light`、`TryParse` | 主题格式 |
| `Net/ImageUrlPolicy.cs` | `ImageUrlPolicy` | `LooksLikeImageUrl`、`IsPublicHttpUrl` | 远程图片地址安全策略 |
| `Pins/PinGroup.cs` | `PinGroup` | — | 贴图分组模型 |

## 6. Platform 文件与关键函数

| 文件 | 关键类型 / 函数 | 用途 |
| --- | --- | --- |
| `PlatformServices.cs` | `PlatformServices.Detect`、各懒加载属性 | 平台能力组合根；新增能力先看这里 |
| `PlatformPaths.cs` | `IsPortable`、`SettingsFile`、`UserDirectory` | 配置目录 |
| `CommandLauncher.cs` | `Start` | 执行用户自定义命令 |
| `SingleInstanceChannel.cs` | `TryAcquire`、`SendAsync`、`Release` | 单实例命令转发 |
| `SingleInstanceLock.cs` | `TryAcquire` | 内核级单实例锁 |
| `Abstractions/IScreenCapture.cs` | `CaptureDesktopAsync`、`CaptureRegionAsync` | 截图 |
| `Abstractions/IWindowCapture.cs` | `Capture`、`CaptureWhole` | 整窗离屏渲染 |
| `Abstractions/ICursorCapture.cs` | `Capture` | 光标快照 |
| `Abstractions/IGlobalHotkey.cs` | `Apply`、`UnregisterAll`、`Pressed` | 全局热键 |
| `Abstractions/IForegroundApp.cs` | `GetForeground`、`GetRectAt`、`GetElementChainAt` | 前台窗口 / 元素识别 |
| `Abstractions/IWindowPlacement.cs` | `ForceTopmost`、`SetTopmost`、`SetBounds` | 窗口层级控制 |
| `Abstractions/IClickThrough.cs` | `TrySetEnabled` | 鼠标穿透 |
| `Windows/WindowsScreenCapture.cs` | `CaptureDesktopAsync`、`CaptureRegionAsync` | Windows BitBlt 截图 |
| `Windows/WindowsWindowCapture.cs` | `CaptureWindowAsync` | PrintWindow 整窗 |
| `Windows/WindowsGlobalHotkey.cs` | 消息线程、注册/注销 | RegisterHotKey |
| `Windows/WindowsForegroundApp.cs` | 前台窗口、窗口矩形、UIA | 窗口/元素识别 |
| `Windows/WindowsElevation.cs` | `IsElevated`、`RestartElevatedAsync` | 按需提权 |
| `WindowsUnelevatedLauncher.cs` | `CanLaunchUnelevated`、`Start` | 从提权进程安全启动普通命令 |
| `Linux/LinuxScreenCapture.cs` | `CaptureRegionAsync` | grim + slurp 等外部工具 |
| `Linux/LinuxNotification.cs` | `ShowAsync` | notify-send / D-Bus |

新增平台能力时，按这个顺序改：

1. `Abstractions/I*.cs` 定义契约。
2. `Windows/`、`Linux/` 各写实现；不支持的平台写 `Unsupported*` 降级。
3. `PlatformServices` 加懒加载属性 + `Detect()` 能力字段。
4. 设置页显示能力/降级原因。
5. 在 `tests/Kscan.Platform.Tests` 加可离线运行的测试。

## 7. GUI Services 文件与关键函数

| 文件 | 关键函数 | 用途 |
| --- | --- | --- |
| `Services/AppSettingsService.cs` | `Load`、`UpdateAsync`、`SelectStartupStore` | 设置读写与便携/普通目录选择 |
| `Services/GlobalHotkeyService.cs` | `ApplyFromSettings`、`Suspend`、`ProbeAvailability` | 热键注册、录制按键时暂停 |
| `Services/RegionSelector.cs` | `SelectAsync`、`IsNearlyBlank`、`NearlySameWindow` | 全屏遮罩框选、窗口/元素高亮 |
| `Services/ResultActions.cs` | `CopyAsync`、`PickImageAsync`、`Scan`、`Parse` | 结果复制、文件选择、二次识别 |
| `Services/ClipboardWriter.cs` | `CopyAsync`、`CopyFileAsync`、`PreparePngAsync` | 位图 + 临时 PNG 写入剪贴板 |
| `Services/ClipboardImageReader.cs` | `ReadAsync` | 读剪贴板位图（含 alpha 处理） |
| `Services/ClipboardSaver.cs` | 保存剪贴板内容 | F3 / 保存目录 |
| `Services/PinManager.cs` | `Pin`、`PinFromClipboardAsync`、`FindAt`、`CloseAll` | 屏幕贴图生命周期 |
| `Services/PinLibraryService.cs` | `Load`、`Sync`、`SaveWindowState`、`MoveItem` | 贴图分组与库 |
| `Services/AnnotationDocument.cs` | 图元增删、撤销、导出 | 标注文档模型 |
| `Services/Annotation.cs` | 图元渲染 | 标注绘制 |
| `Services/QuickAnnotationLayout.cs` | 布局计算 | 快速标注窗口位置 |
| `Services/RecordingManager.cs` | `StartAsync`、`ToggleAsync`、`StopAllAsync` | 录制会话编排 |
| `Services/RecordingSession.cs` | `BuildArguments`、`StartAsync`、`StopAsync` | FFmpeg 参数与进程 |
| `Services/RecordingAudio.cs` | 音源采集 | 系统声音/麦克风/混合 |
| `Services/RecordingCursor.cs` | 光标绘制 | 录制指针合成 |
| `Services/RecordingStamp.cs` | 时间文字 | 画面标注 |
| `Services/ThemeService.cs` | `Initialize`、`Reload`、`Apply` | 主题加载与应用 |
| `Services/FileNaming.cs` | `Context`、`Render`、`Suggested`、`RegisterToken` | 文件名模板 |
| `Services/RemoteImageFetcher.cs` | `TryFetchAsync`、`GetWithRedirectsAsync` | 远程图片下载（逐跳校验） |
| `Services/I18n.cs` | 语言资源加载 | 中英切换 |
| `Services/CrashLog.cs` / `GuiLog.cs` | 写日志 | 崩溃与诊断 |

## 8. ViewModels / Views

| 文件 | 职责 |
| --- | --- |
| `ViewModels/MainViewModel.cs` | 主窗口导航、热键命令分发、剪贴板保存、排除应用 |
| `ViewModels/ScanViewModel.cs` | 识别页：加载图片、框选、扫描、预处理层、结果操作 |
| `ViewModels/GenerateViewModel.cs` | 生成页：内容模板、码制、尺寸、颜色、Logo、保存/复制 |
| `ViewModels/SettingsViewModel.cs` | 设置页主体：常规、外观、热键、命令、录制 |
| `ViewModels/SettingsViewModel.Input.cs` | 输入设置 partial：热键录制、冲突、强制修饰键 |
| `ViewModels/HotkeySettingsViewModel.cs` | 热键列表编辑 |
| `Views/MainWindow.axaml.cs` | 主窗口生命周期、托盘、鼠标手势 |
| `Views/ScanPage.axaml.cs` | 识别页视图 |
| `Views/GeneratePage.axaml.cs` | 生成页视图 |
| `Views/SettingsPage.axaml.cs` | 设置页视图、打开手册 |
| `Views/AnnotationWindow.cs` | 标注窗口；`EditAsync` / `EditCaptureAsync` 是入口 |
| `Views/AnnotationCanvas.cs` | 标注画布输入与绘制 |
| `Views/PinWindow.axaml.cs` | 单张贴图窗口；`SetImage`、`SetTransform`、`SetClickThrough` |
| `Views/PinManagerWindow.cs` | 贴图分组管理 |
| `Views/ScrollCaptureWindow.cs` | 滚动截图主流程 |
| `Views/ScrollLivePreviewWindow.cs` / `ScrollPreviewWindow.cs` | 滚动预览 |
| `Views/RecordingWindow.cs` | 录制设置页 |
| `Views/RecordingRegionSelector.cs` | 实时录制框选 |
| `Views/RecordingControlWindow.cs` | 录制控制条/计时 |
| `Views/HotkeyCaptureWindow.cs` | 按键录制 |
| `Views/NamingSettingsPanel.cs` / `StampSettingsPanel.cs` | 命名与画面时间文字面板 |

## 9. CLI 入口

`CLI/Program.cs`：

```text
Run(args)
├─ decode   识别图片（文件/stdin）
├─ gen      生成码
├─ build    用模板拼内容再生成
├─ formats  列出可生成码制
├─ capture  截图（Linux 外部工具）
├─ toggle   把命令转给已运行 GUI
├─ version
├─ doctor   输出配置、单实例端点、平台能力
└─ help
```

新增命令时同时改：

1. `Run` 的 `switch`。
2. `Usage` 文本。
3. 对应处理函数。
4. `docs/manual/命令行参考.md`，再跑 `scripts/Build-Help.ps1` 生成 HTML。
5. `tests/Kscan.Core.Tests` 或 `tests/Kscan.Platform.Tests`。

## 10. 常见扩展路线

### 9.1 新增识别引擎

1. 在 `src/Kscan.Core/Engines/` 实现 `IBarcodeEngine`。
2. 在 `ScanPipeline.CreateDefault` 注册（或按平台条件注册）。
3. `IsAvailable` 对缺失 native 库返回 `false`。
4. 在 `tests/Kscan.Core.Tests` 覆盖成功、空结果、异常三条路径。
5. 不需要改 GUI；`ScanPipeline` 会自动回退。

### 9.2 新增预处理步骤

1. `PreprocessKind` 加枚举。
2. `PreprocessStep` 加参数与 `Label`。
3. `PreprocessRunner.Apply` 实现图像变换；必要时改 `Levels/Stream`。
4. `PreprocessSession` 支持参数调整（如果界面要调）。
5. `ScanViewModel` 的层级/参数命令与 i18n 文案。
6. 更新 `PreprocessRecipe.Default` 前要评估老用户行为，必要时加迁移。

### 9.3 新增码制 / 生成格式

1. `BarcodeFormats` 加入可生成列表；读不回来的放 `ReaderUnsupported`。
2. `BarcodeEncoder.TryParseFormat` / `Encode` 补映射。
3. `BarcodeRenderer` 处理 1D/2D 尺寸差异。
4. GUI 生成页的码制下拉与本地化。
5. 测试：生成后可解码、SVG 结构、字符画行高。

### 9.4 新增热键动作

1. `HotkeyActionIds` 加常量。
2. 如需默认绑定，在 `AppSettings.Migrate` 按版本补一次，不能覆盖用户已有绑定。
3. `MainViewModel` 或对应 ViewModel 处理动作。
4. `GlobalHotkeyService` 注册/路由。
5. 设置页热键列表与 i18n。
6. 测试：迁移只补一次、用户删掉后不复活。

### 9.5 新增设置项

1. `AppSettings` 加属性与默认值。
2. 需要改老用户默认行为时，升 `CurrentVersion` 并在 `Migrate` 里一次性迁移。
3. `SettingsViewModel` 绑定、保存、重置。
4. `SettingsPage.axaml` 加控件；i18n 两份资源都要加。
5. 测试：缺省、旧配置、往返保存。

### 9.6 新增 CLI 命令

见第 9 节。CLI 不应该依赖 GUI；需要窗口交互时用 `toggle` 转给 GUI。

### 9.7 新增平台能力

见第 6 节。不要在其他平台直接 `throw`；返回 `Unsupported*` 并在 UI 显示原因。

### 9.8 新增录制类型

1. `RecordingKind` 加类型。
2. `RecordingSession.BuildArguments` 生成 FFmpeg 参数。
3. `RecordingManager.StartAsync` 处理目录、框选、FPS。
4. `RecordingSettings`、`RecordingSettingsPanel`、`RecordingWindow`。
5. 测试：参数拼接、首帧就绪、停止收尾、缺 FFmpeg 时跳过集成测试。

### 9.9 新增 UI 页面

1. `Views/` 加 `.axaml` + `.axaml.cs`。
2. `ViewModels/` 加页面模型。
3. `MainWindow.axaml` 加导航入口，`MainViewModel` 加切换逻辑。
4. i18n 两份资源。
5. `Kscan.App.Tests` 用 Avalonia Headless 加一个可打开/关闭的测试。

## 11. 测试地图

| 项目 | 覆盖 |
| --- | --- |
| `Kscan.Core.Tests` | 识别、生成、图像、配置、预处理、滚动拼接、URL 策略 |
| `Kscan.Platform.Tests` | 热键解析、窗口几何、单实例、命令启动、Linux shell |
| `Kscan.App.Tests` | Headless UI、标注、贴图、滚动、录制、主题、剪贴板 |
| `Kscan.Pin.NativeChecks` | Windows 原生贴图尺寸/DPI 冒烟 |
| `Kscan.Record.NativeChecks` | Windows 原生录制/窗口材质冒烟 |

常用过滤：

```powershell
dotnet test tests/Kscan.Core.Tests/Kscan.Core.Tests.csproj -c Release --filter "FullyQualifiedName~ScanPipeline"
dotnet test tests/Kscan.App.Tests/Kscan.App.Tests.csproj -c Release --filter "FullyQualifiedName~Recording"
```

录制集成测试需要本机 `ffmpeg` / `ffprobe`；没有时测试会标记为 skipped，不会判失败。

## 12. 约定与边界

- 新功能先判断属于 Core、Platform 还是 GUI；不要把 Win32 调用写进 Core。
- 平台差异用接口 + `Unsupported*` 降级，不要让整个程序起不来。
- 用户可见的设置必须有默认值、迁移和关闭入口。
- 全局快捷键/鼠标手势默认关闭；新增触发方式必须在设置里可见、可改、可关。
- 所有写盘先写临时文件再原子替换。
- 不要把本机路径、用户名、令牌、测试截图提交进仓库。
- 发布前只改 `Directory.Build.props` 的 `<Version>` 和 `packaging/arch/PKGBUILD` 的 `pkgver`。
- 用户手册改动后运行 `scripts/Build-Help.ps1`，提交生成的 HTML。

## 13. 调试入口

- GUI 日志：`GuiLog`、`CrashLog`。
- CLI 诊断：`kscan doctor` 输出配置路径、单实例端点、平台能力。
- 单实例问题：`SingleInstanceChannel` / `SingleInstanceLock`。
- 截图问题：`RegionSelector` + `IScreenCapture` 实现。
- 贴图问题：`PinManager` + `PinWindow` + `PinNativeSizeLimit`。
- 录制问题：`RecordingSession` 的 FFmpeg 参数与 stderr。

---

有新的扩展点或目录变化时，请同步更新本文和 [`DESIGN.md`](DESIGN.md)。
