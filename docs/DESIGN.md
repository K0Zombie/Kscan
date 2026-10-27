# Kscan — 跨平台二维码扫描器设计文档

- 状态：**M0 / M1 / M1.5 完成，M2 全部收尾完成，M3 贴图 Windows 侧完成并实测，全局热键（4 个动作 + 修饰符可选 + 可自定义 + 可增删条目 + 同一动作可绑多个键）完成并实测**（识别 + 生成双线可用、Avalonia GUI 三页可用、识别页多码高亮与悬停浮窗实测通过、**点高亮框即复制**、界面中英切换实测通过、剪贴板图片监听实测通过（Windows）、**单实例 IPC 实测通过（Windows：`kscan toggle` 激活窗口 / 连起多次只起 1 个实例 / `toggle --capture` 转发框选 / 无实例退出码 3）**、**贴图实测通过（Windows：拖动 / 滚轮缩放（精确 1.1 倍、以光标为不动点）/ 双击关闭 / 多贴图 / 关闭全部 / 鼠标穿透 + 恢复交互；4K 贴图 228 ms）**、**全局热键实测通过（Windows：框选截图 / 识别剪贴板图片 / 剪贴板图片贴到屏幕 / 唤起主窗口 4 个动作各自可绑；`F1` 这类不带修饰键的写法也支持；条目可自己增删；同一动作可绑多个键且**逐条独立注册**（一条被占用不影响另一条）；被占用逐条报可读原因）**、用户配置可持久化（语言 / 自动复制 / 单链接自动打开且默认关闭 / 剪贴板监听 / 底部信息显隐 / 全局热键（有序条目列表）），`dotnet test` **176 项全绿**（Core 127 + Platform 49），构建 0 警告）；下一步 M3 的 Linux 侧（layer-shell `OVERLAY` 贴图）
- 最新补充（2026-10-16）：Markdown 文本贴图与任意命令热键已实现，详见 §7.0；图像预处理优化详见 §5.1.1；当前构建 0 警告 0 错误，206 项测试通过（Core 151 + Platform 51 + App 4）。上方里程碑叙述保留此前验收记录。
- 最新补充（2026-10-18，**发版 0.1.7**）：识别页新增「手动处理图像」——逐层查看预处理、自己增删步骤 / 调参 / 调顺序，可把调通的一套存成「方法」并从 `user/method/*.json` 载入或分享，详见 §5.1.2。同期变更：子元素识别改为**默认关闭**（设置页可开）、SVG 图标复制链路**已按用户要求取消**、内存优化（预览位图显式释放 / 识别管线流式预处理 / 识别后后台回收）。当前构建 0 警告 0 错误，**307 项测试通过**（Core 208 + Platform 78 + App 21）。
- 日期：2026-10-16
- 目标平台：Windows 10/11 (x64) + Linux Wayland（首要环境 niri）/ X11
- 技术栈：.NET 10 (LTS，SDK **10.0.402**) + Avalonia UI **12.1.4**（已确认提供 `net10.0` 资产）+ ZXing.Net 0.16.11 + SkiaSharp 4.153.1（+ Linux native 包）
- 旧项目参考：`../QrCodeScanner-1.52`（WPF / net472 / Windows-only），**只作参考，不修改**

---

## 1. 目标与范围

### 1.1 目标
1. **扫码**：把二维码/条码识别做得干净、快、准。
2. **生成**：把文本编码成条码并导出图片/矢量/终端字符画（D9 纳入）。
3. **一份代码跑 Windows 与 Linux**（单一 Avalonia 解决方案）。
4. **扩展性优先**：引擎、输入源、结果动作、码制全部插件化，新增不触及核心。
5. **直接截图扫码**：热键/命令行触发 → 框选 → 立即识别 → 结果进剪贴板（可选自动打开）。
6. **贴图（Pin）**：把截图/剪贴板图片钉在屏幕上（Snipaste 式基础版），且能直接对贴图内容识别二维码。

### 1.2 明确不做（本期范围外）
- 历史记录、批量识别、图片库管理
- 老版的**扫描枪模式**（`InputSimulator` 模拟键盘输入到别的程序）—— Linux 上需要 XTest/uinput，暂不做；若日后要做，作为 `IResultAction` 插件加入
- 摄像头扫码：**M2 之后再评估**。老版依赖 `Teru.Code.Webcam`（MediaFoundation，Windows-only），跨平台需改用 OpenCvSharp `VideoCapture`。若做，作为独立 `IImageSource` 实现。

> 注：**「二维码生成」已从"不做"改为"做"**（D9），详见 §6。

---

## 2. 已确认的决策（来自用户）

| # | 决策点 | 结论 |
|---|---|---|
| D1 | 跨平台形态 | **Avalonia 重写**，单代码库双平台 GUI |
| D2 | Linux 会话 | **Wayland**，桌面环境 **niri** |
| D3 | Wayland 能力缺口 | **接受降级**（不追求 Wayland 下与 Windows 等价） |
| D4 | Linux 截图实现 | **委托系统截图工具**（niri 上即 `grim` + `slurp`） |
| D5 | Linux 贴图实现 | **`zwlr_layer_shell_v1` 的 OVERLAY 层**（niri/Sway/Hyprland/KWin 支持；GNOME 降级） |
| D6 | Linux 常驻/触发 | **CLI + D-Bus 通知为主**，不做托盘 |
| D7 | 结果动作 | 复制 / 自动浏览器打开 URL / 默认程序打开；**自动打开默认关闭** |
| D8 | 开工方式 | 先出设计文档，逐里程碑推进，每次结束记录任务点 |
| **D9** | **生成功能范围** | **纳入产品功能**：4 种二维 + 11 种一维码制、结构化内容模板、外观与导出（SVG/尺寸/纠错/颜色/Logo）、**等宽字符画二维码** |

---

## 3. 平台能力矩阵（含调研依据）

调研结论均为已查证事实，**M2-M4 的实现以本表为准**，避免重复踩坑。

| 能力 | Windows | Linux + X11 | **Linux + Wayland (niri)** | 本项目的做法 |
|---|---|---|---|---|
| 窗口绝对定位 | ✅ | ✅ | ❌ 协议无绝对定位；Avalonia 的 Wayland backend 中 `Move(PixelPoint)` 是 **no-op**（设计如此） | 用 layer-shell 的 `anchor` + `margin` 实现定位 |
| 置顶 (Topmost) | ✅ | ✅ | ⚠️ core Wayland 不给客户端控制 z-order；但 **niri 支持 layer-shell**，且**只有 `OVERLAY` 层在全屏窗口之上**（`TOP` 层会被全屏窗口盖住） | 贴图使用 **OVERLAY** 层 |
| 鼠标穿透 | ✅ `WS_EX_TRANSPARENT` | ✅ `XShapeCombineRegion` | ⚠️ Avalonia 无内置 API，需在 `wl_surface` 上调 `set_input_region`；且"穿透 + 置顶"在普通 toplevel 下互相冲突 | layer-shell + `set_input_region`（空区域=全穿透） |
| 框选截图 | ✅ `BitBlt` | ✅ `XGetImage` | ⚠️ niri **不支持 portal 的 `InteractiveScreenshot`**；`grim`+`slurp` 可用（wlr-screencopy / ext-image-copy-capture，niri 默认配置即此管线） | **委托外部命令**（可配置模板） |
| 全局热键 | ✅ `RegisterHotKey`（**已实现**：专属消息循环线程 + 条目列表整体注册 + 设置页可增删条目、同一个动作可绑多个键 + 修饰符可选） | ✅ `XGrabKey` | ❌ `org.freedesktop.portal.GlobalShortcuts` 在 niri **未实现**（`xdg-desktop-portal-wlr` 也没有，gnome 的需 gnome-shell）；`ext-hotkey-v1` niri PR #4145 未合并 | Windows 原生（配置项 `hotkeys`：有序的 `{ actionId, keys }` 列表，条目可增删、同一动作可绑多个键）；Linux 由 **niri `binds` 绑 CLI** + 单实例 IPC，界面禁用并说明 |
| 托盘 | ✅ | ✅ | ❌ Wayland 无托盘协议；niri 无系统托盘（需 waybar tray + StatusNotifierItem） | 不做；改用 **D-Bus Notifications** |
| 开机自启 | ✅ `HKCU\...\Run` | ✅ | ✅ `~/.config/autostart/*.desktop` | 分平台实现（**不再写 HKLM**，避免管理员权限问题） |
| 剪贴板图片 | ✅ | ✅ | ✅ | Avalonia `Clipboard` + 平台兜底 |

> 参考来源：Avalonia Discussion #13827 / #18404、aaddrick/claude-desktop-debian #862、niri wiki（Key Bindings / Essential Software）、niri #1983（portal InteractiveScreenshot）、niri #4145（ext-hotkey-v1）、DeepWiki niri 协议支持、Phoronix（Mutter 合并 `xdg_toplevel_tag_v1`）。

### 3.1 为什么不用 XWayland 绕过
niri 是 Wayland-only 合成器（无 X11 会话）。且 XWayland 客户端拿不到合成器拥有的真实屏幕像素，`XGrabKey` / `XGetImage` 均无效。**不存在"假装 X11"的捷径。**

### 3.2 未来可能改善的协议
- `ext-hotkey-v1`（niri PR #4145）：若合并，全局热键可回归应用内注册。
- `xdg_toplevel_tag_v1`（Wayland Protocols 1.43，Mutter/GNOME 49 已合）：可让普通 toplevel 获得置顶/定位语义。
→ 平台层用**能力探测**而非硬编码，这两项落地后无需改核心。

### 3.3 Linux 截图外部工具选型（已查证）

`kscan` 通过 `kscan decode -` 从 **stdin 读原始 PNG/JPEG**，因此**任何**能把 PNG 交出来的截图工具都能接入：

| 工具 | 选区命令 | 输出方式 | 在 niri 上的实际表现 |
|---|---|---|---|
| **`grim` + `slurp`** | `grim -g "$(slurp)" -` | PNG → stdout | ✅ niri 默认配置就是这条管线；**本项目默认** |
| `flameshot` | `flameshot gui -r`（man page：`-r, --raw  Send raw PNG to stdout`，对 `gui`/`full`/`screen` 有效） | PNG → stdout | ⚠️ **多显示器下有硬伤**：flameshot 开一个覆盖整个虚拟桌面的窗口，而 niri 不允许窗口跨屏（niri #2309），于是只有最左屏幕被渲染、工具栏不显示、实际只能截主屏（niri #1737）。单显示器 + flameshot 13.0+（`useGrimAdapter=true`）或 14.x（portal-first）可用 |
| `spectacle` | `spectacle -r -b -n -o <file>` | 文件 | KDE 场景 |
| `gnome-screenshot` | `gnome-screenshot -a -f <file>` | 文件 | GNOME 场景 |

**设计结论**：把选区命令做成**可配置模板**（`IScreenCapture` 的配置项），默认 `grim`+`slurp`；flameshot 作为可选项，用户可按显示器情况自行切换。niri 社区对"需要标注的截图"同样推荐 `grim + slurp + satty`，而非 flameshot。

> 参考：flameshot man page（Debian/Arch/OpenSUSE 一致）、niri Discussion #1737、niri #2309、flameshot 13.0 / 14.0 release notes、copyparty 的 `contrib/flameshot.sh`（`flameshot gui -s -r | curl -T- ...` 真实管道用例）。

---

## 4. 解决方案结构（已落地部分标注 ✔）

```
qrcode/                          工作区根
├─ AGENTS.md                     工作约定
├─ PROGRESS.md                   任务点/进度记录（每次结束更新）
├─ .gitignore
├─ QrCodeScanner-1.52/           旧项目（只读参考，不入版本控制）
└─ Kscan/
    ├─ Kscan.slnx               .NET 10 默认的解决方案格式（dotnet new sln 生成 .slnx）
    ├─ Directory.Build.props     通用构建属性 + 剔除 SkiaSharp native PDB（省 ~550MB/次构建）
    ├─ docs/DESIGN.md            本文档
    ├─ CLI/                       ✔ kscan 命令行端（niri 绑键入口）
    │   └─ Program.cs             ✔ decode / gen / formats / version / help
    │                             ✔ capture（--region / --desktop / --scan / --notify）/ toggle + SingleInstance IPC
    │                             ⬜ capture --clipboard / pin
    ├─ src/
    │   ├─ Kscan.Core/          net10.0，零 UI 依赖，可单测
    │   │   ├─ Abstractions/     ✔ IBarcodeEngine / EngineCapabilities
    │   │   ├─ Engines/          ✔ ZxingEngine（主力，纯托管，双平台）
    │   │   ├─ Model/            ✔ ScanResult / ScanOptions / PointD
    │   │   │                    ✔ BarcodeFormats / BarcodeGenerationRequest
    │   │   ├─ Imaging/          ✔ ImageFrame / SkiaImageCodec（含 EXIF 方向）/ Colors
    │   │   │                    ✔ BarcodeMatrix / BarcodeEncoder / BarcodeRenderer
    │   │   │                    ✔ AsciiBarcodeRenderer / BarcodeImageFactory（门面）
    │   │   ├─ Config/           ⬜ JSON 配置模型 + 读写 + 旧 INI 迁移
    │   │   └─ Pipeline/         ✔ ScanPipeline（引擎回退链）+ ScanOutcome / EngineAttempt
    │   ├─ Kscan.Platform/      ✔ 平台能力实现 + 能力探测
    │   │   ├─ Abstractions/     ✔ IScreenCapture / INotification / IAutoStart / IMouseGesture
    │   │   │                    ✔ IClipboardWatcher / ISingleInstanceChannel / IClickThrough / IGlobalHotkey（含 HotkeyDefinition）
    │   │   │                    ⬜ IWindowEffects（Linux 贴图用，M3 Linux 侧）
    │   │   ├─ Windows/          ✔ BitBlt（PerMonitorV2）、HKCU 自启、气泡通知、鼠标手势、剪贴板序号轮询
    │   │   │                    ✔ RegisterHotKey（专属消息循环线程）、SetWindowLong（WS_EX_TRANSPARENT 穿透）
    │   │   ├─ Linux/            ✔ grim+slurp 委托、notify-send、.desktop 自启
    │   │   │                    ⬜ Linux/Wayland/ layer-shell 协议绑定与 PinSurface（D5，M3）
    │   │   └─ PlatformServices.cs
    │   │                        ✔ PlatformPaths（配置目录：%APPDATA%\kscan / $XDG_CONFIG_HOME/kscan）
    │   ├─ GUI/（= Kscan.App）  ✔ Avalonia UI
    │   │   ├─ Views/            ✔ MainWindow / ScanPage / GeneratePage / SettingsPage / PinWindow（贴图，M3）
    │   │   │                    ⬜ RegionSelectWindow（选区窗口目前在 Services/RegionSelector.cs）
    │   │   ├─ ViewModels/       ✔ Main / Scan / Generate / Settings
    │   │   ├─ Services/         ✔ RegionSelector / ResultActions / AppSettingsService（配置唯一入口）/ PinManager（贴图生命周期）
    │   │   ├─ Controls/         ✔ ResultOverlay（预览图 + 多码高亮层）
    │   │   ├─ Assets/i18n/      ✔ zh-cn / en-us 资源字典（D10：运行时替换 MergedDictionaries）
    │   │   └─ App.axaml
    └─ tests/
        ├─ Kscan.Core.Tests/    ✔ xunit（125 项）
        │   └─ assets/           ✔ qr-hello.png / qr-chinese.png / qr-filepath.png（由 kscan gen 生成）
        └─ Kscan.Platform.Tests/ ✔ xunit（7 项：单实例通道）
```

**配置存储**：用户设置写在 `<配置目录>/settings.json`（模型在 `Core/Configuration/AppSettings`，读写 `JsonSettingsStore` 带原子写与损坏回退；界面侧只有 `AppSettingsService` 一个入口）。目前已落 `language`（D10 的 i18n 用）与 `autoCopySingleResult`，后续新设置只需往记录里加字段 —— 老配置缺字段、多字段都能正常读。

**分层规则**：`Core` 不引用 Avalonia；`Platform` 可引 Avalonia（贴图需要窗口），但不含业务；`App`/`Cli` 只做组合与呈现。这条规则保证 CLI 与测试能在无 GUI 环境运行。

---

## 5. 核心接口

### 5.1 识别（已实现）

```csharp
public interface IBarcodeEngine
{
    string Id { get; }                        // "zxing"
    string DisplayName { get; }
    bool IsAvailable { get; }                 // native 缺失时 false → 配置页隐藏
    EngineCapabilities Capabilities { get; }   // SupportsMultiCode / SupportsLocation / RequiresNativeLibrary
    IReadOnlyList<ScanResult> Decode(ImageFrame frame, ScanOptions options);   // 无码返回空集合，不返回 null
}
```

```csharp
public readonly record struct PointD(double X, double Y);   // 不用 System.Drawing.PointF

public sealed record ScanResult
{
    public required string Text { get; init; }
    public required string Format { get; init; }          // "QR_CODE" / "CODE_128" ...
    public IReadOnlyList<PointD> Points { get; init; }
    public bool HasLocation { get; }
    public PointD? Center { get; }                        // 角点几何中心
    public string EngineId { get; init; }
    public long ElapsedMs { get; init; }
}

public sealed record ScanOutcome(IReadOnlyList<ScanResult> Results, IReadOnlyList<EngineAttempt> Attempts)
{ public bool Success { get; } public long TotalMs { get; init; } public string? FirstText { get; } }

public sealed record EngineAttempt(string EngineId, bool Succeeded, long ElapsedMs, string? Error);
```

### 5.1.1 识别预处理优化（2026-10-16）

`ScanPipeline` 保持“原图成功即返回”，仅在失败后渐进尝试：

```text
原图 → 去彩色叠加 → 低对比度拉伸 → 3×3 中值 → 局部均值二值化
```

- `ScanOptions.TryDenoise` 控制整个预处理回退链，关闭后仅识别原图；滤镜返回自身时跳过重复解码。无可用引擎时不预处理。
- `ImageFilters.MedianBlur(radius: 1)` 使用固定 19 次比较交换的九点中值选择；灰度图只计算一个通道再复制 BGR，保留 alpha 与边界。其他半径保留原通用实现。
- 彩色叠加修复一次采集邻域 BGR，避免每通道重复搜索/清空 256 桶直方图；无彩色像素时不分配掩码，缺少干净邻居时保留原像素，不凭空涂白。
- `StretchContrast` 按灰度直方图 0.5% / 99.5% 分位拉伸，平坦或高对比度图直接跳过。灰度转换将 straight alpha 合成到白底。
- `AdaptiveThreshold` 使用滚动列和计算局部均值；时间 O(像素数)，除灰度和输出帧外仅需 O(宽度) 工作内存。窗口半径随尺寸变化（8～64），只在前述步骤全部失败后使用。
- 所有变体保持原始尺寸与坐标，不缩放、不覆盖输入；后一步沿用前一步，支持组合退化。诊断标记新增 `+contrast` / `+adaptive`，`TotalMs` 包含预处理时间。
- 固定种子合成语料 `tests/Kscan.Core.Tests/PreprocessingSamples.cs`：原图、三档低对比度、三档渐变阴影、三档脉冲噪点、透明底，共 11 张；优化前 5/11，优化后 11/11。该结果仅代表这批合成样本，不是任意真实照片的识别率。
- 同进程预热后、1024×1024 固定种子随机像素、各测 5 次平均：旧/新 3×3 中值在灰度图为 228.20 / 11.87 ms，在彩色图为 231.38 / 30.21 ms。仅是滤镜耗时，不是整条识别链路加速比；基准脚本留在系统临时目录。
- 回归包括随机 BGRA 与慢速中值参考对比、全部 512 种二值 3×3 邻域、局部阈值与朴素窗口均值对比、边缘/透明度/不改输入、旧彩色识别框救回测试。Windows CLI 实际 PNG 解码确认分别命中 `zxing+contrast` 和 `zxing+adaptive`。

验证命令：`dotnet build Kscan/Kscan.slnx -c Release --no-restore`（0 警告 / 0 错误）；`dotnet test Kscan/Kscan.slnx -c Release --no-build --no-restore`（Core 151 + Platform 51 + App 4 = 206 项全过）。Linux 上同样的构建测试命令仍需实际运行；未新增平台依赖。

### 5.1.2 手动处理图像与「方法」（2026-10-18）

预处理从"写死的五级回退"升级成**可配置、可逐层查看、可保存分享**的数据结构，全部在 `Kscan.Core/Pipeline/`：

| 类型 | 作用 |
|---|---|
| `PreprocessKind` / `PreprocessStep` | 一步预处理 + 它的参数（`Decolor` / `Contrast` / `Median` / `Adaptive`，各有一组字段，默认值 = 历史行为） |
| `PreprocessRecipe` | **有序**步骤列表 = 一个「方法」（`Name` + `Steps`）。`Signature()` 做内容指纹，用于去重（`record` 自带的相等性对 `IReadOnlyList` 是引用相等，不能用） |
| `PreprocessRunner` | `Levels(...)` 返回**原图 + 每个真正改变了图像的步骤**（供界面逐层看，保留全部层）；`Stream(...)` 是**不保留历史层**的流式版本，供识别管线用（峰值从"整条链"降到"原图 + 当前层"）；`Apply(...)` 套用一步 |
| `PreprocessSession` | 界面要的操作：`MoveLevel` / `JumpToEdge` / `AdjustParameter` / `AddStep` / `RemoveCurrentStep` / `MoveCurrentStep` / `UseRecipe`。纯逻辑、不碰 UI，键位由 GUI 翻译成方法调用 |
| `MethodStore` | `user/method/*.json` 的读写：`LoadAll` 读目录下所有 `*.json`（接受 `{methods:[…]}` 与裸数组两种格式，坏文件跳过）；`Save` 追加到 `methods.json` 并按 `Signature()` 去重；`EnsureReadme` 首次生成 `README.md` 分享说明；`SuggestName` 默认命名 |

关键设计点：

- **层序号 ≠ 步骤序号**：没变化的步骤不占层，所以 `PreprocessLevel` 带 `StepIndex`（原图为 `-1`）；`H`/`L` 回写参数靠它。
- **界面契约**（D21）：`J`/`K`（=`↑`/`↓`）切层、`H`/`L`（=`←`/`→`）调当前步骤参数、`PgUp`/`PgDn` 跳首尾。步进与范围写死在 `PreprocessSession.Adjust`（饱和度 ±8、对比分位 ±5‰、中值半径 ±1、阈值窗口除数 ±2）。
- **加步骤的位置**：停在原图层插到**链首**，否则插在当前步骤**之后**；加完切到新步骤。停在原图层时若插到链尾，前面的 `adaptive` 已把图二值化，再加减噪往往毫无变化 —— 用户会觉得"加了没反应"。
- **内存**：界面逐层视图保留全部层（全屏一层约 15 MB），只对**手动处理**生效；常规识别走 `Stream`，不保留历史层。

回归：`PreprocessRecipeTests`（10 项）、`PreprocessSessionTests`（12 项）、`MethodStoreTests`（8 项）。

### 5.2 生成（已实现，详见 §6）

```csharp
public static class BarcodeFormats          // 码制清单 / 分类 / 显示名 / ReaderUnsupported
public sealed record BarcodeGenerationRequest  // Text / Format / Width / Height / Margin / ErrorCorrection
                                               // / PureBarcode / ForegroundArgb / BackgroundArgb

public static class BarcodeEncoder
{
    public static bool TryParseFormat(string? name, out BarcodeFormat format);
    public static BarcodeMatrix Encode(BarcodeGenerationRequest request);         // 像素级矩阵（图片用）
    public static BarcodeMatrix EncodeModules(BarcodeGenerationRequest request);  // 模块级矩阵（字符画用）
    public static string? ValidateContent(string format, string text);            // 中文预检提示
}

public static class BarcodeRenderer
{
    public static ImageFrame ToImageFrame(BarcodeMatrix matrix, BarcodeGenerationRequest request);
    public static byte[] ToPng(BarcodeMatrix matrix, BarcodeGenerationRequest request);
    public static string ToSvg(BarcodeMatrix matrix, BarcodeGenerationRequest request);
    public static string ToAscii(BarcodeMatrix matrix, AsciiRenderOptions? options = null);
}

public static class AsciiBarcodeRenderer   // HalfBlocks / Blocks / Hash / Custom + Invert + Margin
public static class Colors                 // #RGB / #RRGGBB / #AARRGGBB / 命名色
public static class BarcodeImageFactory    // 门面：CreatePng / CreateSvg / CreateAscii / CreateQrCodePng
```

### 5.3 后续里程碑（M2+）

```csharp
public interface IImageSource      { string Id { get; } ValueTask<ImageFrame?> AcquireAsync(CancellationToken ct); }
public interface IResultAction     { string Id { get; } int Order { get; } bool CanHandle(...); Task ExecuteAsync(...); }
public interface IScreenCapture    { bool IsSupported { get; } Task<ImageFrame?> CaptureRegionAsync(CancellationToken ct); ... }
public interface IWindowEffects    { PinCapabilities Capabilities { get; } Task<IPinSurface> CreatePinAsync(...); }
public interface IGlobalHotkey     { bool IsSupported { get; } }   // Windows 实现；Linux 返回 false
public interface INotification     { Task ShowAsync(string title, string body); }
public interface IAutoStart        { bool IsEnabled(); Task SetEnabledAsync(bool on); }
```

**内置 `IResultAction`**（D7）：
1. `CopyAction`（默认启用）
2. `OpenUrlAction`：结果是 URL → 默认浏览器；`AutoOpen=false` 时弹确认
3. `OpenWithDefaultAppAction`：文件路径/文本 → `xdg-open` / `UseShellExecute`
4. `PinImageAction`：把当前截图贴上屏幕

**引擎策略**：主力 `ZxingEngine`（纯托管，双平台，支持多码与定位）。zbar / OpenCV WeChatQRCode 作为**可选 native 插件**，`IsAvailable=false` 时自动不出现在设置里。

### 5.4 外观 / 主题（2026-10-18）

任务书 §2「主题包括所有外观设置」落成三层：

| 层 | 位置 | 职责 |
|---|---|---|
| 模型 | `Kscan.Core/Configuration/ThemeDefinition.cs` | `ThemeDefinition`（`Id` / `Name` / `Base` / `Colors`(11 色) / `Mask` / `CornerRadius` / `PreviewCornerRadius` / `FontFamily`）、`ThemeColors`、`ThemeMask`、`ThemeMaskStyles`。纯数据，不依赖 Avalonia |
| 服务 | `Kscan.App/Services/ThemeService.cs` | 扫描 `user/themes/*.json`、写 Avalonia 资源、监听文件变化并自动重载、导出 |
| 界面 | `SettingsViewModel` + `SettingsPage.axaml` | 主题下拉、11 色 + 两个圆角滑杆 + 字体 + 遮罩样式（改即预览）、新建 / 覆盖保存 / 导出 / 编辑文件 |

资源契约（界面一律 `DynamicResource`，换主题不需要重建窗口）：

| 资源 | 来源 | 消费者 |
|---|---|---|
| `Kscan*Brush`（Background / Foreground / Muted / Accent / Panel / Border / Warning / Success / Danger / Selection） | `colors.*` | 各页面、窗口背景、主题预览卡 |
| `KscanCornerRadius` | `cornerRadius` | 设置页卡片、识别页与生成页的面板 / 结果卡 |
| `KscanPreviewCornerRadius` | `previewCornerRadius` | 主题预览卡片 |
| `KscanFontFamily` | `fontFamily`（空 = **不设该资源**，用默认字体） | 主窗口与贴图窗口的 `FontFamily`（继承到全部子控件）；设置页的字体会从 `FontManager.SystemFonts` 生成下拉候选，同时保留手填 |
| `KscanMaskBrush` / `KscanMaskEnabled` | `mask.color` / `mask.style` | `RegionSelector` 在桌面图与选框之间插的那层 `Border` |

关键设计点：

- **不能有"死字段"**：`mask.color` / `cornerRadius` / `fontFamily` 以前只解析不生效（资源写了没人用），
  改主题等于没改。现在四项都有真实消费者：框选遮罩层、卡片与预览卡圆角、两个窗口的字体。
- **解析即收敛**（`ThemeDefinition.Normalized()`）：空 id / 空名字有兜底；`base` 只认 `light`；
  圆角夹到 `0…32`（负数 / 超大 / `NaN` 都不会进 `CornerRadius`）；遮罩样式只认 `dim` / `none`（**默认 `none`**：框选先看原图，想要变暗再自己选）。
  这些文件是用户手写的，非法值不能让界面起不来。
- **id 来源**：文件里没写 `id` 时用文件名（`TryParse(json, fallbackId)`）；`system` 是保留字；
  同一个 id 出现多次时**后来者覆盖**，所以放一份 `dark.json` 就能盖过内置深色。
- **三种保存语义**（对应任务书"覆盖到主题 / 只放着 / 导出"）：改动只进内存预览；
  「保存到当前主题」写回当前文件（内置或跟随系统 → `custom.json`）；
  「另存为新主题」用 `SuggestId` 生成 ASCII id（中文被吃掉 → `theme`，重名 → `theme-2`）并切过去。
- **实时编辑预览**：`FileSystemWatcher` 监听 `user/themes/*.json`（300ms 防抖），外部编辑器保存后自动
  `Reload + ApplyCurrent`；「重新加载」按钮保留作兜底。
- **字体留空时不能塞 `FontFamily.Default`**：`$Default` 显式设到窗口上会覆盖整棵可视树的字体，
  而它在 Avalonia 12 + 本机环境下建立不了 glyphTypeface —— 界面直接崩
  （实测：`Could not create glyphTypeface. Font family: $Default`）。留空时必须**移除**
  `KscanFontFamily` 资源，让窗口的 `FontFamily` 退回"未设置"，由 Fluent / `FontManagerOptions` 决定默认字体。
- **对比度保护**仍然生效：前景 / 背景亮度差 < 0.30、次要文字差 < 0.20 时自动改成黑 / 白或退化成半透明前景色。

回归：`ThemeDefinitionTests`（30 项：默认值 / 遮罩样式 / 圆角收敛 / id 兜底与去重 / 往返 / 复制）、
`ThemeServiceTests`（9 项：资源落地 / 空字体不设资源 / 遮罩开关 / 内置保留 / 用户主题覆盖内置 / 非法圆角 /
导出收敛 / 说明文档生成 / 旧版说明升级且不覆盖用户文档）。

`user/themes/README.md` 带版本标记（`<!-- kscan:themes-readme v3 -->`）：只有"本程序生成的旧版"
（无标记且以固定标题开头）会被升级，用户自己改过的文档不动 —— 说明里的字段会随版本变化，
既不能永远停留在旧版，也不能覆盖用户笔记。

---

## 6. 生成能力（D9，已实现）

### 6.1 支持的码制

清单来自 ZXing.Net 0.16.11 中**确实存在 writer 实现**的格式（用 `kscan formats` 可查看）：

| 类别 | 码制 |
|---|---|
| 二维（4） | `QR_CODE`、`DATA_MATRIX`、`PDF_417`、`AZTEC` |
| 一维（11） | `CODE_128`、`CODE_39`、`CODE_93`、`CODABAR`、`EAN_8`、`EAN_13`、`UPC_A`、`UPC_E`、`ITF`、`MSI`、`PLESSEY` |

> 读取端支持的格式比这里多（例如 `MAXICODE`、`RSS_*` 只能读不能生成，见 §13.2）。

### 6.2 管线与两套矩阵语义

```
文本 ──BarcodeEncoder──▶ BarcodeMatrix ──BarcodeRenderer──▶ PNG / SVG / 等宽字符画
```

- **`Encode`** 返回**像素级**矩阵：ZXing 已按请求尺寸渲染过，适合再整数倍放大成图片。
- **`EncodeModules`** 返回**模块级**矩阵（调用 ZXing 时把 width/height 传 1，它就不再放大）：供字符画使用。
  两者混用会让字符画变成"一屏放不下"（320×320 而非 29×29）——这个坑已经踩过一次。
- **缩放一律取整数倍**：非整数倍会让模块宽窄不一，直接扫不出来；因此实际输出可能略大于请求尺寸。
- **SVG** 把同一行里连续的深色模块合并成一条 subpath，体积远小于逐模块 `rect`；行尾统一 `\n`（跨平台产物不该随宿主变成 `\r\n`）。

### 6.3 等宽字符画（"用等宽字体构成的二维码"）

`AsciiBarcodeRenderer` 提供 4 种风格，默认 `HalfBlocks`：

| 风格 | 字符 | 特点 |
|---|---|---|
| `HalfBlocks`（默认） | `█ ▀ ▄` + 空格 | 一行承载两行模块，终端字符宽高比正好 ≈ 2:1，扫描器可直接读；29×29 的 QR → 15 行 × 29 字符 |
| `Blocks` | `██` / 空格 | 每模块 2 字符宽，兼容性最好 |
| `Hash` | `##` / 空格 | 纯 ASCII 环境 |
| `Custom` | 自选字符（`--chars`） | 例如用 `十`/`・` 做"汉字风"二维码 |

支持 `--invert`（浅色终端）与静区 `Margin`。输出可直接打到终端，或写进 `.txt`。

### 6.4 外观、校验与导出

- **尺寸/静区**：`--size`、`--height`、`--margin`（模块数）
- **颜色**：`--fg` / `--bg`，支持 `#RGB`、`#RRGGBB`、`#AARRGGBB` 与命名色
- **纠错级别**（QR）：`--ecc L|M|Q|H`
- **1D 码**：`PureBarcode` 控制是否渲染下方可读文本
- **导出**：PNG（位图）、SVG（矢量，印刷缩放不失真）
- **内容预检**：`BarcodeEncoder.ValidateContent` 在生成前给出中文提示（EAN-8/13、UPC-A/E 只允许数字与固定长度、ITF 需偶数位）；校验位由 ZXing 自动补算（`590123412345` → 读回 `5901234123457`）

### 6.5 已完成（2026-10-15 补齐）

- ✅ **结构化内容模板** —— `Model/BarcodeContent` 七类：`wifi` / `vcard` / `url` / `email` / `tel` / `sms` / `geo`；含 WiFi 与 vCard 的转义规则、URL 自动补 `https://`、号码归一化、坐标范围校验。CLI 为 `kscan build <类型> [字段...] [生成选项]`，不带输出选项时只打印 payload。
- ✅ **居中 Logo 叠加** —— `Imaging/LogoOverlay`（自写双线性缩放 + 白色衬底，默认占宽度 22%）；CLI 为 `--logo <图片> [--logo-scale 0.22]`。测试里有一条专门的"叠 Logo 后仍能读回"用例（配 `--ecc H`）。
- ✅ **1D 码字符画行高** —— `AsciiRenderOptions.RowRepeat`；CLI 对 1D 码默认重复 8 行（`--ascii-rows N` 可覆盖），否则只有 1 行高、扫描器读不到。
- ✅ **GUI 生成页**（M2，2026-10-16）—— `GUI/Views/GeneratePage.axaml` + `ViewModels/GenerateViewModel`：内容模板（文本 / WiFi / 网址 / 联系人 / 位置）、15 种码制下拉、尺寸与静区滑杆、纠错级别、**中心 Logo 叠加（选择 / 清除 / 宽度占比滑杆，复用 `LogoOverlay`）**、实时预览、保存（PNG 叠加 Logo；SVG 为矢量输出、忽略 Logo 并提示）、复制图片 / 复制内容。
- ⬜ **GUI 生成页待补**：前景/背景颜色选择器、「直接贴图」（属 M3 的 `IWindowEffects`）。

**交付物**：`D:\kscan\`（Windows + Linux 目录式发布 + `linux-x64.tar.gz` + 使用说明 README），供先行自用。

---

## 7. 关键流程

### 后台运行与托盘（2026-10-16）

- Windows 增加三个持久化开关：`startSilently=false`、`minimizeToTray=false`、`closeToTray=true`。X 默认隐藏到托盘；关闭该开关则退出整个进程，贴图也随应用退出。静默启动不先显示再隐藏窗口，开机自启同样遵循此设置；显式 `--capture` 优先展示操作界面。
- 托盘点击/“显示主窗口”恢复原窗口，“退出程序”强制退出；设置页也有退出入口。隐藏时热键/手势/单实例 IPC 继续工作，系统关机和应用退出不被 CloseToTray 拦截。
- `PlatformServices.TrayAvailable` 运行时探测 Windows shell 托盘，GUI 再核对实际 native tray；缺失时不隐藏、X 直接退出。Linux/niri 保持 D6 的 CLI/IPC 路线，不依赖托盘。
- Windows portable 从独立的 `portable-defaults.json` 初始化快捷键：F1 纯截图、F2 截图识别、F3 剪贴板图片/Markdown 贴图、F4 唤起、Ctrl+F1 截图识别备用。只在用户配置文件不存在时使用；已有配置（包括空热键列表或损坏文件）不被默认快捷键覆盖。普通非 portable 配置仍默认不占用键盘快捷键。

### 7.0 剪贴板文本贴图与命令热键（2026-10-16）

- `pinClipboard` 保持原动作 id：优先读取图片；没有图片时读取纯文本，以 `Markdown.Avalonia 12.0.0-a3`（含 HTML / SVG / 代码高亮插件）渲染 Markdown，再交给原 `PinWindow`。文本贴图居中，图片仍沿用截图原位还原规则。
- `GUI/Services/MarkdownPinRenderer` 直接渲染完整文档树；先等待图片任务（15 秒超时），再接入不可见的样式宿主完成字体测量。输出 2 倍分辨率位图，复用图片贴图的移动、缩放、透明度、复制、保存与关闭。文档逻辑高度超过 32768 时明确报错，不静默裁剪。
- 标题、粗斜体、引用、列表、表格、代码高亮、长文及本地引用图片已通过真实 Skia 离屏测试；Windows F3 文本贴图已实际触发。远程图片与 Linux/niri 桌面行为未实测。
- `runCommand` 是新增热键动作；`HotkeyBinding` 新增可选 `command` / `arguments` / `workingDirectory`，老配置继续兼容，record 内容相等仍涵盖全部字段。
- 设置页“执行命令”一行显示程序、参数、工作目录；程序路径不加外层引号，含空格的参数路径自行加引号。`Platform/CommandLauncher` 用 `ProcessStartInfo` 直接启动，不隐式调用 shell；需要管道/重定向时显式指定 `cmd.exe /c` 或 `sh -c`。
- **每条命令可以单独勾「以管理员身份运行」**（2026-10-18，`UserCommand.runAsAdmin` → `HotkeyBinding.RunAsAdmin` →
  `CommandLauncher`）：Windows 走 `UseShellExecute + Verb=runas`（每次触发弹 UAC，`ERROR_CANCELLED(1223)`
  翻译成「已取消管理员授权（UAC）」），Linux 套 `pkexec sh -c`。这只影响**子进程**的权限，
  与 kscan 自己是否提权（见 4.7 的两个开关）是两件事；已提权的 kscan 再跑 `runas` 不会弹框。
- 每条命令绑定使用独立注册路由；重应用配置会丢弃旧的已排队触发事件。启动失败在主窗口显示错误并前置窗口。“已启动”只代表进程创建成功，不代表外部命令最终退出码为零。
- Linux 不新增应用内全局热键，仍由 niri binds 执行外部命令。layer-shell 与 Linux 桌面验收保持后续任务。

验证命令：`dotnet build Kscan/Kscan.slnx -c Release`、`dotnet test Kscan/Kscan.slnx -c Release`。界面测试项目 `tests/Kscan.App.Tests` 使用 Avalonia Headless + 实际 Skia 绘制（不是空绘图桩）。

### 7.1 截图识别（Windows）

**整窗截图的边界**：窗口矩形统一走 `WindowsWindowGeometry.VisibleRect`（`DwmGetWindowAttribute` 的
`DWMWA_EXTENDED_FRAME_BOUNDS`），**不用 `GetWindowRect`** —— 后者在 Vista 之后会朝左 / 右 / 下各多算
7~8 px 的透明拖拽边框（上面没有），拿它去屏幕裁剪就会三边多出一圈桌面（用户报过的"整窗多括了一点"）。
DWM 不可用时退回 `GetWindowRect`（宁可大一圈也不失败）。命中测试、遮挡判断、黄框高亮也都用可见矩形；
`PrintWindow` 是按窗口矩形出图的，所以渲染完再用 `ClipToVisible` 把可见那块裁出来。
```
热键/按钮 → IScreenCapture.CaptureRegionAsync()
  → 自绘全屏遮罩 + 框选 → 裁剪 → ImageFrame
  → ScanPipeline.Scan(frame)  [引擎回退链]
  → IResultAction 路由（复制 / 可选打开）
  → 结果面板（Win 上为普通窗口）
```

**定格光标标记**（2026-10-18，任务书 §3）：

- 遮罩出现时先抓一份光标快照（`ICursorCapture.Capture` → `CursorSnapshot`，契约是**物理像素**）。
- **默认什么都不画**（`cursorMarkerShowCursor=false`、`cursorMarkerShowFrame=false`）：按切换键
  （`captureCursorKey`，默认 `Shift`）才显示光标图像，此时截出来的图也带光标（`_includeCursor` 跟着切换）；
  虚线框是独立开关（`cursorMarkerShowFrame`，默认关），打开后贴着光标可见部分描。
- 框的尺寸按**光标可见部分**算（`CursorMarkerGeometry.VisibleBounds`：alpha > 8 的外接矩形），
  不是按位图尺寸 —— 32×32 的箭头其实只占 12×19，按位图描边会明显偏大。
- 光标快照的尺寸取"屏幕上真正画出来的大小"（`CursorSizePolicy`，三路互相取大）：
  ① 光标所在显示器 DPI 下系统声明的大小（`GetDpiForMonitor` + `GetSystemMetricsForDpi(SM_CXCURSOR)`）；
  ② **Windows 辅助功能「鼠标指针大小」的基准尺寸**（`HKCU\Control Panel\Cursors\CursorBaseSize`，
  默认 32、调大后是 48/64/80…；`SM_CXCURSOR` 不随它变化，漏了它光标就只剩真实大小的 40%）× 该屏 DPI / 96；
  ③ 位图尺寸 × DPI / 96（只放大不缩小）。只问 `GetDpiForWindow(GetDesktopWindow())` 拿到的是**系统** DPI，
  光标在另一块不同缩放的屏幕上时会算小。
- 颜色 / 粗细 / 透明度（`cursorMarkerColor` / `cursorMarkerThickness` / `cursorMarkerOpacity`）都是设置项，
  整个标记可以关掉（`cursorMarkerEnabled`）。

### 7.2 截图识别（Linux / niri）
```
niri binds: spawn "kscan" "capture" "--region"
  → kscan 单实例检测：已有实例 → 通过 Unix socket 转发命令，退出
  → IScreenCapture.CaptureRegionAsync() 执行（可配置模板）:
      默认: sh -c 'grim -g "$(slurp)" -'        # 输出 PNG 到 stdout
      可选: spectacle -r -b -n -o <tmp> / gnome-screenshot -a -f <tmp> / flameshot gui
  → 读取图片 → ScanPipeline.Scan → IResultAction → INotification 显示结果摘要
```

### 7.3 贴图生命周期
```
来源（截图结果 / 剪贴板 / 文件 / 生成结果）→ IWindowEffects.CreatePinAsync(image, options)
Windows: WS_POPUP + Topmost + SetWindowLong(WS_EX_TRANSPARENT 可选)
Linux(Wayland): layer-shell surface, layer=OVERLAY, namespace="kscan-pin"
   anchor=top|left + margin=(x,y) → 精确定位
   set_input_region(空) → 鼠标穿透；恢复区域 → 可交互
   keyboard-interactivity = on_demand（需要滚轮/双击/右键时）
交互：拖动（layer-shell 下改 margin 重设）、滚轮以光标为中心缩放、
     Ctrl+滚轮透明度、双击关闭、右键菜单（复制/另存/原始尺寸/识别二维码/穿透/关闭）
管理：进程内 PinManager 维护列表，可"关闭全部贴图"
降级：GNOME 等不支持 layer-shell → 普通无边框 toplevel（不保证置顶/定位/穿透）
```

---

## 8. layer-shell 集成设计（D5，风险最高）

Avalonia 不暴露 layer-shell，需要自写互操作：

- 方式：P/Invoke `libwayland-client.so.0`，从 Avalonia 的 Wayland 连接取 `wl_display` / `wl_registry`，或者**自建一条独立 Wayland 连接**（更干净，但需要自己处理 `wl_surface` 的 buffer 提交 —— 用 `wl_shm` 上传 RGBA）。
- 需要绑定的接口：`zwlr_layer_shell_v1`、`zwlr_layer_surface_v1`、`wl_region`、`wl_shm`/`wl_shm_pool`、`wl_buffer`、`wl_surface`（`attach/commit/damage/set_input_region/set_buffer_scale`）。
- 关键点：
  1. 层选 `OVERLAY`（niri 下 `TOP` 会被全屏窗口盖住）。
  2. `anchor = top|left` + `margin(top=y, left=x)` 实现任意坐标；四边全 anchor 会拉伸，不用。
  3. `exclusive_zone = 0`，避免影响 niri 的平铺工作区。
  4. `keyboard-interactivity`：需要键盘时用 `on_demand`，纯展示用 `none`（`none` 时收不到键盘，滚轮/鼠标仍可用）。
  5. `namespace` 用固定值（如 `kscan-pin`），便于 niri 按 namespace 配规则。
  6. 输出缩放：用 `wl_surface.set_buffer_scale` 处理 HiDPI，否则贴图会模糊/错位。
- 兜底：能力探测失败（非 layer-shell 合成器 / 非 Wayland / 非 Linux）→ 走 toplevel 降级路径，并在设置页显示"当前环境不支持置顶/穿透"的原因。
- 工作量：约 300-500 行 interop，估 3-5 天（含调试）。

---

## 9. 打包与分发

| 平台 | 产物 | 要点 |
|---|---|---|
| Windows | `kscan.exe` + `kscan-ui.exe`（self-contained, single-file, win-x64） | ZXing 纯托管；SkiaSharp native 随包 |
| Linux | `kscan` + `kscan-ui`（self-contained, linux-x64，tar.zst） | 需附带 `.desktop`（**reverse-DNS `app_id`**，如 `io.github.k0zombie.kscan`）、图标、`~/.config/autostart` 模板 |
| 通用 | GitHub Actions：build + test + 打包 artifact | Windows runner + ubuntu runner |

**发布时裁剪**：`SkiaSharp.NativeAssets.Linux.NoDependencies` 会带来 14 个 Linux RID 的 `.so`，开发时输出约 106MB；打包用 `dotnet publish -r <rid>` 只带目标 RID。

**发布形态**：默认**目录式**发布，单文件不作首选。`CLI/Kscan.Cli.csproj` 设了 `AssemblyName=kscan`；单文件需显式 `-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`（SkiaSharp 带 native 库，必须允许自解压，否则运行时报缺 `libSkiaSharp`）。实测：单文件 win-x64 = **41 MiB**；目录式 linux-x64 = **92 MiB**（195 个文件，`kscan` 为标准 ELF）。


**版本号**：全解决方案统一在 `Kscan/Directory.Build.props` 的 `<Version>`（当前 `0.1.0`）；`kscan version` 读 `AssemblyInformationalVersion`（带 `+commit` 时只取版本段）。pacman 包的 `pkgver`、`.desktop` 的版本都对着它 —— 发版改这一处 + `packaging/arch/PKGBUILD` 的 `pkgver`。

**预构建产物（0.1.0，2026-10-16）**：交付目录 `D:\kscan\` —— **Windows**：`windows-x64.zip` / `windows-gui-x64.zip`（解压即用）+ 单文件 `kscan-single-win-x64.exe` / `kscan-gui-single-win-x64.exe`；**Linux**：pacman 包 `kscan` / `kscan-gui`（可直接 `pacman -U`，不用自己 makepkg，GUI 包内含 reverse-DNS 的 `io.github.k0zombie.kscan.desktop` 与 256px 图标）+ `linux-x64.tar.gz` / `gui-linux-x64.tar.gz`；外加 `README.md`（用法）与 **`LINUX-TEST.md`（niri 验收单）**。Windows 侧没有 bsdtar/zstd，pacman 包是在 WSL 的 Arch 里跑 `make-pacman-package.sh` 生成的。

**Linux 分发：pacman 包（Arch）** —— 见 `packaging/arch/PKGBUILD` 与同目录 `README.md`：
- `build()` 直接对当前工作树跑 `dotnet publish -r linux-x64 --self-contained`（`source=()` + `_srcroot="$startdir/../.."`）；推到 git 仓库后应改成 `git+https://…` source。
- 安装布局按 Arch 惯例：`/usr/lib/kscan/`（整份产物）+ `/usr/bin/kscan`（符号链接）。
- `_self_contained=true`（默认，只依赖 `glibc`）或 `false`（约 2MB，需 `dotnet-runtime`）；Arch 滚动更新，默认选自包含以免被系统运行时升级影响。
- `options=('!strip')`（.NET 产物不 strip）；GUI 端进来后再补 `.desktop` 与图标。

**Linux 依赖说明（写进 README）**：
- 必需：`grim` + `slurp`（截图）；D-Bus（通知）
- 可选：`wl-clipboard`（`wl-copy`/`wl-paste`，剪贴板图片兜底）、`libnotify`
- 注意：**不要**把 `xdg-desktop-portal-wlr` 设为主 portal（niri 官方建议用 `xdg-desktop-portal-gtk` + `xdg-desktop-portal-gnome`）；本项目不依赖 portal 截图。

**niri 配置示例（写进 README）**：
```kdl
binds {
    // niri 的 spawn 不走 shell，参数要分开写引号
    Mod+Shift+S { spawn "kscan" "capture" "--region"; }
    Mod+Shift+V { spawn "kscan" "capture" "--clipboard"; }
    Mod+Shift+P { spawn "kscan" "pin" "--clipboard"; }
}
```

---

## 10. 里程碑与验收标准

每个里程碑都必须能用**可执行命令**验收。

### M0 设计文档与进度机制 — ✅ 完成
- 交付：本文件 + `AGENTS.md` + `PROGRESS.md`

### M1 环境 + 识别 Core + CLI — ✅ 完成（2026-10-15）
- 装 .NET SDK **10.0.402**（`winget install Microsoft.DotNet.SDK.10`，实测未弹 UAC 即成功）
- 建 `Kscan.slnx` + `CLI/`（命令行端）+ `src/Kscan.Core` + `tests/Kscan.Core.Tests`
- `ZxingEngine`（AutoRotate / TryHarder / 多码 / 定位 / 去重）+ `ScanPipeline`（回退链 + 异常隔离 + 耗时统计）
- 实测：`dotnet build` **0 警告 0 错误**；`kscan gen`→`kscan decode` 往返一致（含中文与 Windows 路径）

### M1.5 条码生成能力 — ✅ 核心完成（2026-10-15）
- 交付：`BarcodeFormats` / `BarcodeGenerationRequest` / `BarcodeMatrix` / `BarcodeEncoder` / `BarcodeRenderer` / `AsciiBarcodeRenderer` / `Colors`；CLI 的 `gen`（12 个选项）与 `formats`
- 实测验收：
  - `dotnet test Kscan.slnx -c Release` → **56 项全部通过**（含 15 种码制的生成验证）
  - `kscan gen "<文本>" out.png -f <码制>` → `kscan decode out.png` 往返一致（QR / DataMatrix / PDF417 / Aztec / Code128 / Code39 / Code93 / Codabar / EAN-8 / EAN-13 / UPC-A / ITF）
  - `kscan gen "hi" -a -f QR_CODE` → **15 行 × 29 字符**的终端二维码，结构正确可扫
  - `--svg` 输出合法 SVG；EAN-13 校验位自动补算
  - **管道**：`kscan decode -`（从 stdin 读原始 PNG/JPEG）与 `kscan gen ... -o -`（写二进制到 stdout）均已实测；`gen -o - | decode -` 全链路往返一致
- 未完成：结构化内容模板、Logo 叠加、1D 字符画行高（见 §6.5）
- **未验证**：Linux 侧执行同样命令（见 `PROGRESS.md` 第五节）

### M2 Avalonia UI + 截图扫码主线 — ✅ 主体 + 收尾 ①③④ 完成（2026-10-16）
- 建 `Kscan.App`（Avalonia 12.1.4，实际目录 `GUI/`），先跑通最小窗口
- 主窗口（区域截图 / 剪贴板 / 文件 / 拖放入口）、结果面板、**生成页**、设置页
- `IScreenCapture`：Windows 自绘框选（BitBlt + PerMonitorV2）；Linux 委托 `grim`+`slurp`
- `IResultAction`：复制 / 打开 URL / 默认程序打开（默认不自动打开）
- 验收（Windows）：框选 → 识别 → 剪贴板有结果 —— **实测通过**（2026-10-16，顺带修掉 `RegionSelector` 的两个真缺陷：窗口未 `Show()` 导致 `await` 永久挂起；窗口尺寸把物理像素当逻辑单位用）
- 多码高亮：凸包 + 悬停浮窗 + 与结果列表双向联动 —— **实测通过**（2026-10-16，见 `GUI/Controls/ResultOverlay.cs`；几何在 Core，16 项新测试）
- i18n：中英运行时切换 + 持久化 + 平台描述本地化 —— **实测通过**（2026-10-16，见 `GUI/Services/I18n.cs` 与 `Assets/i18n/*.axaml`）
- ✔ 收尾剩余：剪贴板图片监听 + 单实例 IPC —— **均实测通过**（2026-10-16，Windows；单实例另配 `tests/Kscan.Platform.Tests` 7 项单测）
- ⬜ 验收（Linux）：niri 上 `kscan capture --region` 生效

### M3 贴图 PinWindow
- Windows 完整（拖动/滚轮缩放/透明度/右键菜单/识别/穿透/多贴图）—— **已完成并实测**（2026-10-16）：`GUI/Views/PinWindow.axaml(.cs)` + `GUI/Services/PinManager.cs` + `IClickThrough`（Windows 用 `WS_EX_TRANSPARENT`）；实测项见 `PROGRESS.md` 第十批，其中 Ctrl+滚轮透明度未从外部验证（Avalonia 的 `Opacity` 不走 `WS_EX_LAYERED`）
- Linux layer-shell OVERLAY（定位 + 置顶 + 穿透）—— **未开始**，本机无法验证
- 验收：贴一张 4K 截图不卡 —— **Windows 侧已测（228 ms）**；niri 下贴图位于全屏窗口之上、位置正确、穿透可切换 —— **未验证**

### M4 Linux 平台层收尾
- **全局热键（Windows 侧已完成并实测，2026-10-16）**：4 个动作（框选截图 / 识别剪贴板图片 / 剪贴板图片贴到屏幕 / 唤起主窗口）各自可绑，**修饰符可选**（`F1` 这类裸键也支持）；`IGlobalHotkey` + `HotkeyDefinition` + `WindowsGlobalHotkey`（一条消息循环线程整批注册，`WM_HOTKEY` 按 id 分发）+ `GlobalHotkeyService`；设置页一行 = 一条绑定（可增删、同一动作可绑多个键），配置项 `hotkeys`（默认空 = 全不注册；旧版 4 个标量字段读时自动迁移）。Linux 无应用内实现（niri binds + CLI）
- 单实例 IPC —— **Windows 侧已完成并实测**（Linux 的 Unix socket 分支未验证）；D-Bus 通知、autostart `.desktop`、能力探测与降级提示 —— 待做
- 验收：niri 下连按 3 次绑键不会起 3 个进程；无 `grim` 时给出可读错误

### M5 打包与 CI
- 验收：干净 Windows 与 Linux 机器上从 artifact 解压即用

---

## 11. 风险与缓解

| 风险 | 状态 | 缓解 |
|---|---|---|
| Avalonia 与 net10 的兼容性 | ✅ 已消除 | 查证 Avalonia **12.1.4** 提供 `net10.0` 资产组；M2 首步锁版本实测 |
| layer-shell interop 调试成本超预期 | 监控中 | 先交付 toplevel 降级路径保证可用，layer-shell 作为增强并行开发；能力探测驱动，不阻塞主线 |
| niri 合成器行为差异（每次提交/重设 margin 的开销） | 监控中 | margin 变更为逐帧节流（~60fps 上限）；必要时评估 `wl_subsurface` |
| Wayland 剪贴板图片需要 `wl-clipboard` | 监控中 | Avalonia 剪贴板为主，`wl-paste` 兜底，失败时给出明确提示 |
| 无全局热键导致的体验落差 | 监控中 | README 给出 niri/waybar 一键配置片段；`kscan` 提供 `capture`/`pin`/`toggle` 短命令 |
| 老项目 native 引擎（zbar/OpenCV）在 Linux 缺失 | 监控中 | 主力 ZXing 已够用；两者做成可选插件，不阻塞 |
| SkiaSharp 的 native PDB 让每次构建复制数百 MB | ✅ 已解决 | `Directory.Build.props` 剔除输出/发布里的 native `.pdb`（654MB → 106MB） |
| Linux 端到端运行未验证 | 待用户验证 | `PROGRESS.md` 第五节列出需在 niri 上执行的命令 |

---

## 12. 可从旧项目复用的资产

| 资产 | 位置 | 复用方式 |
|---|---|---|
| 引擎调用与灰度矩阵（`.3/.59/.11`） | `Modules/MyScanner.cs` | ✔ 已落到 `ImageFrame.ToGray8()`（BT.601 权重） |
| 凸包与外接多边形 | `Modules/ConvexHullHelper.cs` | M2 迁移（纯算法，可直接搬） |
| 几何中心计算 | `Modules/MyScanner.cs` 中 `GeometryHelper` | ✔ 已落到 `ScanResult.Center` |
| i18n 字符串（60+ 条中英） | `Resources/zh-cn.xaml` / `en-us.xaml` | M2 转成新 i18n 资源 |
| DPI 感知清单思路 | `app.manifest` | M2 Windows 侧保留 PerMonitorV2 |
| 多码高亮/浮窗交互 | `Views/PicWindow.xaml.cs`（凸包绘制 + `GoPanel`） | M2 作为 Avalonia 结果面板的交互参考 |
| 开机自启注册表逻辑 | `Modules/Autorun.cs` | M4 复用但**改 HKCU**（原写法用 HKLM 需管理员，Win11 下被禁用） |

**不迁移**：`InputSimulatorPlus`（仅 net20 程序集）、`Teru.Code.*`（Windows-only native/闭源）、WPF 专属 XAML。

---

## 13. 已知限制与能力边界

### 13.1 生成侧：能生成、但本程序读不回来
`UPC_E`、`MSI`、`PLESSEY` —— ZXing.Net 有 writer 却没有可用的 reader（单码 `--single` 与多码模式都实测读不到）。生成的图形本身正常（用图像检查确认过条形结构无误）。已在 `BarcodeFormats.ReaderUnsupported` 中声明，**界面在用这三种码制生成时应提示"请用专用扫描设备验证"**。

### 13.2 只能读、不能生成
`MAXICODE`、`RSS_14`、`RSS_EXPANDED`、`IMB`、`PHARMA_CODE` 等（不在 `BarcodeFormats.Writable` 中）。

### 13.3 1D 码字符画
当前输出只有 1 行高，人眼可读但扫描器实际扫不到；需要重复若干行才实用（见 §6.5）。

### 13.4 平台
- Linux/Wayland 的能力缺口见 §3（无全局热键、无窗口绝对定位、截图需委托外部工具）。
- SkiaSharp 在 Linux 上必须显式引入 `SkiaSharp.NativeAssets.Linux.NoDependencies`（主包不含 Linux native）。
- 系统级限制：`.NET` 的 `Process.Start` 打开 URL/文件时，Linux 走 `xdg-open`，行为取决于桌面环境。

### 13.5 工具链坑（已踩）
- `dotnet test Kscan.slnx` **不会重新编译 `Kscan.Cli`**（只编译被测试引用的项目）。改完 CLI 必须先 `dotnet build Kscan.slnx`，再用 `dotnet run --no-build`，否则跑的还是旧二进制。
- .NET 10 的 `dotnet new sln` 生成 `.slnx`，不是 `.sln`。

---

## 14. 待定问题

1. 贴图的"拖动"在 layer-shell 下是"重设 margin"，需实测 niri 的跟手程度；若不理想，评估 `wl_subsurface` 或降级方案。
2. 是否 `git init` 以及仓库边界（当前 `Kscan/` 与工作区根的 `AGENTS.md`/`PROGRESS.md` 分居两处；本机 git 全局 `user.name`/`user.email` 未设置）。
3. 是否需要 macOS？当前设计（Avalonia）可扩展，但贴图/截图/热键需新增平台实现。
4. 摄像头扫码是否纳入本期（见 §1.2）。
5. 生成侧是否要加"二维码美化"（圆点风格、渐变、Logo），还是保持工业标准样式。
