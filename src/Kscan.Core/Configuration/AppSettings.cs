namespace Kscan.Core.Configuration;

/// <summary>
/// 用户配置。所有字段都必须能缺省 —— 配置文件可能是旧版本写的、也可能被手工编辑过，
/// 读不出来就当默认值用，绝不能让程序起不来。
/// </summary>
public sealed record AppSettings
{
    public RecordingSettings Recording { get; init; } = new();
    /// <summary>配置结构版本，便于日后迁移（缺省即 1；2 起有"排除应用"与默认的框选排除热键）。</summary>
    public const int CurrentVersion = 14;

    /// <summary>配置结构版本，便于日后迁移（缺省即 1）。</summary>
    public int Version { get; init; } = 1;

    /// <summary>
    /// 界面语言：<c>zh-CN</c> / <c>en-US</c>；<c>null</c> 表示跟随系统区域。
    /// </summary>
    public string? Language { get; init; }

    public bool StartSilently { get; init; }
    public bool MinimizeToTray { get; init; }
    public bool CloseToTray { get; init; } = true;

    /// <summary>只识别到一个码时自动复制到剪贴板（扫码工具的常见直觉）。</summary>
    public bool AutoCopySingleResult { get; init; } = true;

    /// <summary>截图扫码完成后显示主窗口；默认关闭。</summary>
    public bool ShowMainWindowAfterCaptureScan { get; init; }

    /// <summary>是否在窗口底部显示平台能力摘要（设置页可关掉）。</summary>
    public bool ShowPlatformSummary { get; init; } = true;

    /// <summary>
    /// 识别到<b>单个链接</b>时直接用系统默认浏览器打开。
    /// 默认<b>关闭</b>：自动拉起外部程序有安全风险（链接可能来自不可信来源），需用户在设置页显式开启。
    /// </summary>
    public bool OpenUrlAutomatically { get; init; }

    /// <summary>
    /// "快捷转码"用的码制：把剪贴板里的文本一键变成码时用它。默认二维码（最常用）。
    /// </summary>
    public string QuickCodeFormat { get; init; } = "QR_CODE";

    /// <summary>
    /// 监听剪贴板：出现新图片时自动识别。默认<b>关闭</b>（剪贴板内容可能敏感，不该默认被读取）。
    /// 只在有剪贴板变更通知的平台上有效（见 <c>IClipboardWatcher</c>）。
    /// </summary>
    public bool WatchClipboard { get; init; }

    /// <summary>
    /// 贴图时把剪贴板里的<b>图片地址</b>下载下来再贴（裸地址，例如 <c>https://…/a.png</c>）。
    /// 默认<b>关闭</b>：自动联网读取剪贴板里的地址属于隐私动作（会暴露 IP 与访问行为），
    /// 需要用户在设置页显式开启。内网 / 环回 / 链路本地地址一律拒绝，见 <c>RemoteImageFetcher</c>。
    /// </summary>
    public bool PinRemoteImages { get; init; }

    /// <summary>
    /// 触发过"Delete 会彻底删除贴图"的说明（第一次按 Delete 时弹一次，确认后记住）。
    /// 贴图一旦删掉连分组库里的文件一起没了，所以第一次要明确告知。
    /// </summary>
    public bool PinDeleteConfirmed { get; init; }

    /// <summary>
    /// 跨屏框选：遮罩覆盖所有显示器，可以从一块屏拖到另一块，拼成一张图。
    /// 默认<b>关闭</b>（只盖光标所在那块屏，行为和以前完全一致）；
    /// 开启后每次截图都要抓整个虚拟桌面（双 4K 约 33MB），开销明显更大。
    /// </summary>
    public bool CaptureAcrossScreens { get; init; }

    /// <summary>
    /// 跨屏截图里"显示器之间空档"的填充方式：<c>transparent</c>（默认，透明）或 <c>black</c>（黑色）。
    /// 空档在系统截图里本来是黑色，透明与否由用户决定 —— 有些程序不接受透明 PNG。
    /// </summary>
    public string MultiScreenGapFill { get; init; } = GapFill.Transparent;

    /// <summary>
    /// 跨屏框选时识别光标下的<b>窗口</b>（默认开）：框出窗口，单击直接截那个窗口；
    /// 光标在空白处时框的是<b>整个虚拟桌面</b>。只做窗口级识别，<b>不做</b>子元素（UIA）识别。
    /// </summary>
    public bool CaptureAcrossScreensWindowDetection { get; init; } = true;

    /// <summary>
    /// 新贴出来的图默认「始终置顶」。默认<b>开启</b>（贴图的常见用法就是压在其它窗口上看）；
    /// 关掉后新贴图落到普通层级，单张仍可用右键菜单或热键随时改。
    /// </summary>
    public bool PinTopmostByDefault { get; init; } = true;

    /// <summary>
    /// 动图贴图的"定格键"：光标停在动图上时按住它，动画停在当前帧，松开继续。
    /// 取值 <c>Shift</c>（默认）/ <c>Control</c> / <c>Alt</c> / <c>None</c>（关闭）。
    /// </summary>
    public string AnimatedFreezeKey { get; init; } = "Shift";

    /// <summary>标注默认线宽（图片像素）。</summary>
    public float AnnotationThickness { get; init; } = 4;

    /// <summary>标注默认颜色（ARGB 十六进制，例如 <c>#FFFF3B30</c>）。</summary>
    public string AnnotationColor { get; init; } = "#FFFF3B30";

    /// <summary>
    /// 框选截图时识别光标下的窗口：高亮窗口边框，<b>单击直接截取整个窗口</b>（拖动仍是自由框选）。
    /// 默认开启；需要前台窗口探测（Windows 有实现）。
    /// </summary>
    public bool CaptureWindowDetection { get; init; } = true;

    /// <summary>
    /// 框选截图时是否识别光标下的<b>子元素</b>：橙色元素框、单击截取元素、滚轮 / Tab 换层。
    /// 默认<b>关闭</b> —— 只保留「截整个窗口 / 自由框选」这条主路径，需要细粒度截取时再到设置页打开。
    /// 关掉时完全不查 UIA（省掉每次探测的跨进程开销），<see cref="ElementCapturePadding"/>、
    /// <see cref="ElementLevelKey"/>、<see cref="ElementIgnoreModifier"/> 随之不起作用。
    /// </summary>
    public bool CaptureElementDetection { get; init; }

    /// <summary>
    /// 截窗口时窗口<b>超出屏幕</b>：用 <c>PrintWindow</c> 让窗口整窗渲染，屏幕外的部分也能截到。
    /// 默认开启；关掉后只截屏幕上露出来的那一块。
    /// </summary>
    public bool CaptureWholeWindowOffScreen { get; init; } = true;

    /// <summary>
    /// 截窗口时窗口<b>被其它窗口挡住</b>：同样用 <c>PrintWindow</c> 整窗渲染，被挡住的部分也能截到。
    /// 默认开启；关掉后截到的是屏幕上看到的样子（含遮挡物）。
    /// </summary>
    public bool CaptureWholeWindowOccluded { get; init; } = true;

    /// <summary>
    /// 单击截取<b>子元素</b>时四边外扩（正数）或内缩（负数）的像素数。默认 <b>+2</b>：
    /// 元素边框常常紧贴内容，留一点边更好看。窗口截取不受它影响（与元素隔离）。
    /// </summary>
    public int ElementCapturePadding { get; init; } = 2;

    /// <summary>
    /// 框选截图时在遮罩上标出"定格的光标"：截屏本身不含光标，靠它指示光标位置。
    /// 默认开启；光标图像拿不到时自动不画（Linux 没有实现）。
    /// </summary>
    public bool CursorMarkerEnabled { get; init; } = true;

    /// <summary>
    /// 遮罩出现时是否直接显示<b>光标图像</b>。默认 <b>false</b>：任务书 §3 要求
    /// "此时不显示光标，只显示描出的边框"，按切换键（<see cref="CaptureCursorKey"/>）才把光标显示出来
    /// （同时截出来的图也带光标）。
    /// </summary>
    public bool CursorMarkerShowCursor { get; init; }

    /// <summary>
    /// 是否用<b>虚线框</b>描出定格的光标（<b>默认关</b>）：默认遮罩上什么都不画，
    /// 按切换键才显示光标图像。打开后框贴着光标的可见部分，
    /// 颜色 / 粗细 / 透明度见下面三个设置；光标图像显示时框仍然画（"光标和描的框"）。
    /// </summary>
    public bool CursorMarkerShowFrame { get; init; }

    /// <summary>虚线框颜色（<c>#RRGGBB</c> / <c>#AARRGGBB</c>）。</summary>
    public string CursorMarkerColor { get; init; } = "#FF00C8FF";

    /// <summary>虚线框线宽（像素，0.5…8）。</summary>
    public double CursorMarkerThickness { get; init; } = 2;

    /// <summary>虚线框不透明度（0.1…1）。</summary>
    public double CursorMarkerOpacity { get; init; } = 0.9;

    /// <summary>
    /// 按一下这个键切换"遮罩上是否显示光标 / 截图是否带光标"：<c>Shift</c>（默认）/
    /// <c>Ctrl</c> / <c>Alt</c> / <c>none</c>（关闭）。是<b>切换</b>，不是按住。
    /// </summary>
    public string CaptureCursorKey { get; init; } = "Shift";

    /// <summary>
    /// 框选遮罩里「子元素层级依次往上」的切换方式：<c>wheel</c>（滚轮，默认）/ <c>tab</c> /
    /// <c>arrows</c> / <c>none</c>。语义固定为：往外一层（父级）/ 收回最内层。
    /// 到窗口层之后单击就是截整个窗口。
    /// </summary>
    public string ElementLevelKey { get; init; } = "wheel";

    /// <summary>
    /// 按住它<b>忽略子元素</b>：元素框不显示，单击直接截整个窗口。<c>Ctrl</c>（默认）/ <c>Alt</c> /
    /// <c>Shift</c> / <c>none</c>（关闭）。
    /// </summary>
    public string ElementIgnoreModifier { get; init; } = "Ctrl";
    /// <summary>
    /// 外观主题 id：<c>system</c>（跟随系统，默认）/ <c>dark</c> / <c>light</c>，或 <c>user/themes</c> 里的文件名。
    /// </summary>
    public string ThemeId { get; init; } = ThemeDefinition.SystemThemeId;

    /// <summary>
    /// 「保存剪贴板内容到文件夹」这个动作的目标文件夹；<c>null</c> / 空 = 未设置（触发时提示用户去设置）。
    /// 可以手填，也可以用设置页的「浏览…」选。
    /// </summary>
    public string? ClipboardSaveFolder { get; init; }

    /// <summary>
    /// 全局热键绑定：<b>有序列表</b>，同一个动作可以绑多个键（例如框选既绑 <c>F1</c> 又绑 <c>Ctrl+Alt+S</c>）。
    /// <para>
    /// 默认空：全局热键会占住系统级按键，该由用户显式选择。
    /// 只在支持全局热键的平台有效（见平台层的 <c>IGlobalHotkey</c>）。
    /// </para>
    /// <para>
    /// 用 <see cref="HotkeyBindings"/> 而不是 <c>List</c> / 字典：那个类型实现了<b>内容相等</b>，
    /// record 的值语义（<c>next == Settings</c> 决定要不要写盘）才成立。详见它的注释。
    /// </para>
    /// </summary>
    public HotkeyBindings Hotkeys { get; init; } = HotkeyBindings.Empty;

    /// <summary>
    /// 热键排除列表：焦点落在这些应用里时，全局热键<b>不触发</b>
    /// （条目可以是进程名 <c>taskmgr.exe</c>，也可以是完整 exe 路径）。
    /// 按住 <see cref="ForceHotkeyModifiers"/> 可以强制触发。
    /// </summary>
    public AppList IgnoredApps { get; init; } = AppList.Empty;

    /// <summary>
    /// 在排除应用里强制触发热键时要额外按住的修饰键（默认 <c>Ctrl+Alt</c>）。
    /// <c>null</c> / 空串 = 关闭强制触发（排除应用里完全不响应）。
    /// </summary>
    public string? ForceHotkeyModifiers { get; init; } = "Ctrl+Alt";

    /// <summary>「框选排除应用」这个动作的默认热键。</summary>
    public const string DefaultExcludeAppHotkey = "Ctrl+Alt+Backspace";

    /// <summary>「关闭所有贴图」这个动作的默认热键。</summary>
    public const string DefaultCloseAllPinsHotkey = "Ctrl+Q";

    /// <summary>「截取并贴图」这个动作的默认热键。</summary>
    public const string DefaultCaptureAndPinHotkey = "Ctrl+F3";

    /// <summary>「贴图置顶开关」这个动作的默认热键。</summary>
    public const string DefaultTogglePinTopmostHotkey = "Ctrl+Shift+F3";

    /// <summary>「贴图分组管理」这个动作的默认热键。</summary>
    public const string DefaultOpenPinManagerHotkey = "Ctrl+Shift+F4";

    /// <summary>「跨屏框选开关」这个动作的默认热键。</summary>
    public const string DefaultToggleCaptureAcrossScreensHotkey = "Ctrl+Alt+S";

    /// <summary>「截图并标注」这个动作的默认热键（截完先标注，标完复制到剪贴板）。</summary>
    public const string DefaultCaptureRegionWithAnnotationHotkey = "Ctrl+Shift+F1";
    public const string DefaultScrollCaptureHotkey = "Ctrl+Shift+F2";

    /// <summary>「标注剪贴板图」这个动作的默认热键。</summary>
    public const string DefaultAnnotateClipboardHotkey = "Ctrl+Shift+E";

    /// <summary>「屏幕批注层」这个动作的默认热键。</summary>
    public const string DefaultAnnotateScreenHotkey = "Ctrl+Alt+D";

    /// <summary>「关闭光标下的贴图」这个动作的默认热键（穿透贴图靠它关闭）。</summary>
    public const string DefaultClosePinUnderCursorHotkey = "Alt+Q";

    // ---- 旧版（M4 第十一批）的 4 个标量热键字段 ----
    // 那时一个动作只能绑一个键，所以是 4 个固定字段。现在改成上面的 Hotkeys 列表（同一动作可绑多个键）。
    // 这里保留它们**只为读出老配置**：JsonSettingsStore.Load() 读完立刻调 MigrateHotkeys()，
    // 值搬进 Hotkeys 后旧字段置 null，写回的文件里就再也不会出现它们。

    /// <summary>旧字段：框选截图的热键。仅为兼容老配置保留。</summary>
    public string? HotkeyCaptureRegion { get; init; }

    /// <summary>旧字段：识别剪贴板图片的热键。仅为兼容老配置保留。</summary>
    public string? HotkeyScanClipboard { get; init; }

    /// <summary>旧字段：贴剪贴板图片到屏幕的热键。仅为兼容老配置保留。</summary>
    public string? HotkeyPinClipboard { get; init; }

    /// <summary>旧字段：唤起主窗口的热键。仅为兼容老配置保留。</summary>
    public string? HotkeyActivate { get; init; }

    public static AppSettings Default { get; } = new();

    /// <summary>
    /// 读出配置后的统一迁移入口：先搬旧标量热键，再按 <see cref="Version"/> 补默认值。
    /// 什么都没变时返回同一实例（值相等 → 不会触发无谓写盘）。
    /// </summary>
    public AppSettings Migrate()
    {
        var settings = MigrateHotkeys();

        if (settings.Version >= CurrentVersion)
        {
            return settings;
        }

        var bindings = settings.Hotkeys.ToList();

        // 1 → 2：老配置补上「框选排除应用」的默认热键。
        // 只按版本补一次：用户删掉这条后 version 已是 2，不会再被加回来。
        if (settings.Version < 2 &&
            !bindings.Any(binding => binding.ActionId == HotkeyActionIds.PickExcludedApp))
        {
            bindings.Add(new HotkeyBinding(HotkeyActionIds.PickExcludedApp, DefaultExcludeAppHotkey));
        }

        // 2 → 3：光标标记改成"默认只画虚线框，不显示光标图像"（任务书 §3）。
        // 老配置里存的是以前默认的 true，所以按版本改一次；改完 version 就是 3，
        // 之后用户在设置里手动打开就不会再被覆盖。
        if (settings.Version < 3)
        {
            settings = settings with { CursorMarkerShowCursor = false };
        }

        // 3 → 4：虚线框也默认关（用户要求"光标画虚线框默认为关"）：默认遮罩上什么都不画，
        // 按切换键才显示光标图像；想要框就在设置里单独打开。同样只改这一次。
        if (settings.Version < 4)
        {
            settings = settings with { CursorMarkerShowFrame = false };
        }

        // 4 → 5：补上「关闭所有贴图」的默认热键 Ctrl+Q（贴图多了以后总得有个一键清场）。
        // 同样只按版本补一次：用户删掉这条后 version 已是 5，不会再被加回来。
        if (settings.Version < 5 &&
            !bindings.Any(binding => binding.ActionId == HotkeyActionIds.CloseAllPins))
        {
            bindings.Add(new HotkeyBinding(HotkeyActionIds.CloseAllPins, DefaultCloseAllPinsHotkey));
        }

        // 5 → 6：补上「截取并贴图」的默认热键 Ctrl+F3（框选完直接贴到屏幕上，省掉"复制 → 再贴"两步）。
        if (settings.Version < 6 &&
            !bindings.Any(binding => binding.ActionId == HotkeyActionIds.CaptureRegionAndPin))
        {
            bindings.Add(new HotkeyBinding(HotkeyActionIds.CaptureRegionAndPin, DefaultCaptureAndPinHotkey));
        }

        // 6 → 7：补上「贴图置顶开关」的默认热键 Ctrl+Shift+F3。
        if (settings.Version < 7 &&
            !bindings.Any(binding => binding.ActionId == HotkeyActionIds.TogglePinTopmost))
        {
            bindings.Add(new HotkeyBinding(HotkeyActionIds.TogglePinTopmost, DefaultTogglePinTopmostHotkey));
        }

        // 7 → 8：补上「贴图分组管理」的默认热键 Ctrl+Shift+F4。
        if (settings.Version < 8 &&
            !bindings.Any(binding => binding.ActionId == HotkeyActionIds.OpenPinManager))
        {
            bindings.Add(new HotkeyBinding(HotkeyActionIds.OpenPinManager, DefaultOpenPinManagerHotkey));
        }

        // 8 → 9：补上「关闭光标下的贴图」的默认热键 Alt+Q（鼠标穿透的贴图只能靠它关闭）。
        if (settings.Version < 9 &&
            !bindings.Any(binding => binding.ActionId == HotkeyActionIds.ClosePinUnderCursor))
        {
            bindings.Add(new HotkeyBinding(HotkeyActionIds.ClosePinUnderCursor, DefaultClosePinUnderCursorHotkey));
        }

        // 9 → 10：补上「跨屏框选开关」的默认热键 Ctrl+Alt+S。
        if (settings.Version < 10 &&
            !bindings.Any(binding => binding.ActionId == HotkeyActionIds.ToggleCaptureAcrossScreens))
        {
            bindings.Add(new HotkeyBinding(
                HotkeyActionIds.ToggleCaptureAcrossScreens,
                DefaultToggleCaptureAcrossScreensHotkey));
        }

        // 10 → 11：补上「截图并标注」的默认热键 Ctrl+Shift+F1。
        if (settings.Version < 11 &&
            !bindings.Any(binding => binding.ActionId == HotkeyActionIds.CaptureRegionWithAnnotation))
        {
            bindings.Add(new HotkeyBinding(
                HotkeyActionIds.CaptureRegionWithAnnotation,
                DefaultCaptureRegionWithAnnotationHotkey));
        }

        // 11 → 12：补上「标注剪贴板图」的默认热键 Ctrl+Shift+E。
        if (settings.Version < 12 &&
            !bindings.Any(binding => binding.ActionId == HotkeyActionIds.AnnotateClipboard))
        {
            bindings.Add(new HotkeyBinding(
                HotkeyActionIds.AnnotateClipboard,
                DefaultAnnotateClipboardHotkey));
        }

        // 12 → 13：补上「屏幕批注层」的默认热键 Ctrl+Alt+D。
        if (settings.Version < 13 &&
            !bindings.Any(binding => binding.ActionId == HotkeyActionIds.AnnotateScreen))
        {
            bindings.Add(new HotkeyBinding(HotkeyActionIds.AnnotateScreen, DefaultAnnotateScreenHotkey));
        }

        // 13 → 14：只补充新动作，不覆盖扫码、贴图及用户自定义绑定。
        if (settings.Version < 14 && !bindings.Any(binding => binding.ActionId == HotkeyActionIds.ScrollCapture))
            bindings.Add(new HotkeyBinding(HotkeyActionIds.ScrollCapture, DefaultScrollCaptureHotkey));

        return settings with { Version = CurrentVersion, Hotkeys = new HotkeyBindings(bindings) };
    }

    /// <summary>
    /// 把旧版的 4 个标量热键字段搬进 <see cref="Hotkeys"/>（<b>只在列表为空时</b>，
    /// 免得新格式的列表被老字段里的残留覆盖），并清空旧字段。
    /// 没有旧字段时原样返回同一实例 —— 这样"读出来什么都没变"的配置仍然值相等。
    /// </summary>
    public AppSettings MigrateHotkeys()
    {
        if (HotkeyCaptureRegion is null
            && HotkeyScanClipboard is null
            && HotkeyPinClipboard is null
            && HotkeyActivate is null)
        {
            return this;
        }

        var bindings = Hotkeys.ToList();

        if (bindings.Count == 0)
        {
            foreach (var (actionId, text) in new[]
            {
                (HotkeyActionIds.CaptureRegion, HotkeyCaptureRegion),
                (HotkeyActionIds.ScanClipboard, HotkeyScanClipboard),
                (HotkeyActionIds.PinClipboard, HotkeyPinClipboard),
                (HotkeyActionIds.Activate, HotkeyActivate),
            })
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    bindings.Add(new HotkeyBinding(actionId, text));
                }
            }
        }

        return this with
        {
            Hotkeys = new HotkeyBindings(bindings),
            HotkeyCaptureRegion = null,
            HotkeyScanClipboard = null,
            HotkeyPinClipboard = null,
            HotkeyActivate = null,
        };
    }
}

/// <summary>跨屏截图里"显示器之间空档"的填充方式取值。</summary>
public static class GapFill
{
    /// <summary>透明（PNG 的 alpha=0）。</summary>
    public const string Transparent = "transparent";

    /// <summary>不透明黑。</summary>
    public const string Black = "black";

    /// <summary>归一化：认不出的值一律当透明。</summary>
    public static string Normalize(string? value) =>
        string.Equals(value, Black, StringComparison.OrdinalIgnoreCase) ? Black : Transparent;

    public static bool IsBlack(string? value) =>
        string.Equals(Normalize(value), Black, StringComparison.Ordinal);
}
