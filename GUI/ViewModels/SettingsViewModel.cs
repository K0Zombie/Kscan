using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kscan.App.Services;
using Kscan.Core.Configuration;
using Kscan.Core.Model;
using Kscan.Platform;
using Kscan.Platform.Abstractions;

namespace Kscan.App.ViewModels;

/// <summary>语言下拉里的一项；<see cref="Code"/> 为空串表示"跟随系统"。</summary>
public sealed record LanguageOption(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>设置页：平台能力、开机自启、识别行为、界面语言、界面显示、配置文件位置。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private bool _initialized;

    /// <summary>上一次状态的 key 与参数；为 null 表示状态是外部文本（平台层消息），不参与重算。</summary>
    private string? _statusKey;

    private object?[] _statusArgs = [];

    public SettingsViewModel()
    {
        var autoStart = PlatformServices.AutoStart;
        _autoStartSupported = autoStart.IsSupported;
        _autoStart = autoStart.IsEnabled();

        var settings = AppSettingsService.Current.Settings;
        _autoCopySingleResult = settings.AutoCopySingleResult;
        _showMainWindowAfterCaptureScan = settings.ShowMainWindowAfterCaptureScan;
        _showPlatformSummary = settings.ShowPlatformSummary;
        _openUrlAutomatically = settings.OpenUrlAutomatically;
        _watchClipboard = settings.WatchClipboard;
        _pinRemoteImages = settings.PinRemoteImages;
        _pinTopmostByDefault = settings.PinTopmostByDefault;
        _animatedFreezeKey = AnimatedFreezeKeyOption(settings.AnimatedFreezeKey);
        _captureAcrossScreens = settings.CaptureAcrossScreens;
        _captureAcrossScreensWindowDetection = settings.CaptureAcrossScreensWindowDetection;
        _multiScreenGapFill = GapFill.IsBlack(settings.MultiScreenGapFill)
            ? MultiScreenGapFillOptions[1]
            : MultiScreenGapFillOptions[0];
        _clipboardSaveFolder = settings.ClipboardSaveFolder ?? string.Empty;
        _quickCodeFormat = settings.QuickCodeFormat;
        _annotationThickness = SnapThickness(settings.AnnotationThickness);
        _annotationColor = AnnotationLabelFor(settings.AnnotationColor);
        _startSilently = settings.StartSilently;
        _minimizeToTray = settings.MinimizeToTray;
        _closeToTray = settings.CloseToTray;
        _forceHotkeyModifiers = settings.ForceHotkeyModifiers ?? string.Empty;

        foreach (var app in settings.IgnoredApps)
        {
            IgnoredApps.Add(app);
        }

        RebuildLanguages();
        _language = FindLanguage(settings.Language);
        RebuildMaskStyles();
        RebuildThemes();
        LoadThemeFields();
        ReloadCommands();

        // 本 VM 与应用同生命周期（由 MainViewModel 持有），无需退订
        AppSettingsService.Current.Changed += (_, _) => RefreshFromSettings();
        I18n.Current.Changed += (_, _) => RefreshLocalizedText();

        // 主题文件被外部编辑器改动时 ThemeService 会自动重新扫描：这里同步下拉与各输入框
        ThemeService.Current.ThemesChanged += (_, _) => OnThemesReloaded();

        _initialized = true;

        // 计划任务查询要起 schtasks.exe，别卡在启动路径上
        _ = LoadElevatedAutoStartAsync();
    }

    [ObservableProperty]
    private bool _autoStartSupported;

    [ObservableProperty]
    private bool _autoStart;

    [ObservableProperty] private bool _startSilently;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _closeToTray = true;

    public bool TraySupported => PlatformServices.TrayAvailable;

    partial void OnStartSilentlyChanged(bool value)
    {
        if (_initialized) _ = AppSettingsService.Current.UpdateAsync(s => s with { StartSilently = value });
    }
    partial void OnMinimizeToTrayChanged(bool value)
    {
        if (_initialized) _ = AppSettingsService.Current.UpdateAsync(s => s with { MinimizeToTray = value });
    }
    partial void OnCloseToTrayChanged(bool value)
    {
        if (_initialized) _ = AppSettingsService.Current.UpdateAsync(s => s with { CloseToTray = value });
    }

    [RelayCommand]
    private void ExitApplication() => (Avalonia.Application.Current as App)?.ExitApplication();

    /// <summary>只识别到一个码时自动复制（写进配置文件，下次启动仍生效）。</summary>
    [ObservableProperty]
    private bool _autoCopySingleResult;

    [ObservableProperty]
    private bool _showMainWindowAfterCaptureScan;

    partial void OnShowMainWindowAfterCaptureScanChanged(bool value)
    {
        if (_initialized)
            _ = AppSettingsService.Current.UpdateAsync(settings => settings with { ShowMainWindowAfterCaptureScan = value });
    }

    /// <summary>识别到单个链接时直接用默认浏览器打开（默认关闭 —— 有安全风险，写进配置文件）。</summary>
    [ObservableProperty]
    private bool _openUrlAutomatically;

    /// <summary>监听剪贴板、出现新图片就自动识别（默认关闭 —— 剪贴板可能含敏感内容）。</summary>
    [ObservableProperty]
    private bool _watchClipboard;

    /// <summary>贴图时把剪贴板里的图片地址下载下来再贴（默认关闭 —— 属于自动联网）。</summary>
    [ObservableProperty]
    private bool _pinRemoteImages;

    /// <summary>新贴出来的图默认置顶（默认开启）。</summary>
    [ObservableProperty]
    private bool _pinTopmostByDefault = true;

    /// <summary>跨屏框选：遮罩覆盖所有显示器（默认关闭）。</summary>
    [ObservableProperty]
    private bool _captureAcrossScreens;

    /// <summary>跨屏框选时识别光标下的窗口（默认开；空白处 = 整个虚拟桌面）。</summary>
    [ObservableProperty]
    private bool _captureAcrossScreensWindowDetection;

    partial void OnCaptureAcrossScreensWindowDetectionChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }

        _ = AppSettingsService.Current.UpdateAsync(
            settings => settings with { CaptureAcrossScreensWindowDetection = value });
    }

    /// <summary>动图定格键（下拉里给的是本地化文案，落库时换回 Shift/Control/Alt/None）。</summary>
    [ObservableProperty]
    private string _animatedFreezeKey = string.Empty;

    /// <summary>动图定格键的四个选项。</summary>
    public IReadOnlyList<string> AnimatedFreezeKeyOptions { get; } =
    [
        I18n.T("Str.Settings.AnimatedFreezeShift"),
        I18n.T("Str.Settings.AnimatedFreezeControl"),
        I18n.T("Str.Settings.AnimatedFreezeAlt"),
        I18n.T("Str.Settings.AnimatedFreezeOff"),
    ];

    private string AnimatedFreezeKeyOption(string? stored) => FreezeKey.ToModifier(stored) switch
    {
        HoldModifier.Control => AnimatedFreezeKeyOptions[1],
        HoldModifier.Alt => AnimatedFreezeKeyOptions[2],
        HoldModifier.Shift => AnimatedFreezeKeyOptions[0],
        _ => AnimatedFreezeKeyOptions[3],
    };

    partial void OnAnimatedFreezeKeyChanged(string value)
    {
        if (!_initialized)
        {
            return;
        }

        var stored = value switch
        {
            var option when option == AnimatedFreezeKeyOptions[1] => FreezeKey.Control,
            var option when option == AnimatedFreezeKeyOptions[2] => FreezeKey.Alt,
            var option when option == AnimatedFreezeKeyOptions[3] => FreezeKey.None,
            _ => FreezeKey.Shift,
        };

        _ = AppSettingsService.Current.UpdateAsync(settings => settings with { AnimatedFreezeKey = stored });
    }

    /// <summary>跨屏截图里"显示器之间空档"填什么：透明 / 黑色。</summary>
    [ObservableProperty]
    private string _multiScreenGapFill = GapFill.Transparent;

    /// <summary>下拉里显示的两个选项（本地化文案）。</summary>
    public IReadOnlyList<string> MultiScreenGapFillOptions { get; } =
    [
        I18n.T("Str.Settings.MultiScreenGapTransparent"),
        I18n.T("Str.Settings.MultiScreenGapBlack"),
    ];

    partial void OnMultiScreenGapFillChanged(string value)
    {
        if (!_initialized)
        {
            return;
        }

        // 下拉里给的是本地化文案，写配置时要换回内部取值
        var stored = string.Equals(value, MultiScreenGapFillOptions[1], StringComparison.Ordinal)
            ? GapFill.Black
            : GapFill.Transparent;

        _ = AppSettingsService.Current.UpdateAsync(settings => settings with { MultiScreenGapFill = stored });
    }

    // ---- 「标注」子页 ----

    /// <summary>标注默认线宽（图片像素）。</summary>
    [ObservableProperty]
    private float _annotationThickness = 4;

    public IReadOnlyList<float> AnnotationThicknessOptions { get; } = [2, 4, 8];

    /// <summary>配置里的线宽可能不是 2/4/8（历史值/手改），落到最近的可选项，避免下拉空白。</summary>
    private float SnapThickness(float value) =>
        AnnotationThicknessOptions.OrderBy(option => Math.Abs(option - value)).First();

    partial void OnAnnotationThicknessChanged(float value)
    {
        if (!_initialized)
        {
            return;
        }

        _ = AppSettingsService.Current.UpdateAsync(settings => settings with { AnnotationThickness = value });
    }

    /// <summary>标注默认颜色（ARGB 十六进制）。</summary>
    [ObservableProperty]
    private string _annotationColor = "#FFFF3B30";

    public IReadOnlyList<string> AnnotationColorOptions { get; } =
    [
        "红 #FF3B30",
        "橙 #FF9500",
        "黄 #FFCC00",
        "绿 #34C759",
        "蓝 #0A84FF",
        "白 #FFFFFF",
    ];

    private static readonly Dictionary<string, string> AnnotationColorHex = new()
    {
        ["红 #FF3B30"] = "#FFFF3B30",
        ["橙 #FF9500"] = "#FFFF9500",
        ["黄 #FFCC00"] = "#FFFFCC00",
        ["绿 #34C759"] = "#FF34C759",
        ["蓝 #0A84FF"] = "#FF0A84FF",
        ["白 #FFFFFF"] = "#FFFFFFFF",
    };

    /// <summary>配置里的十六进制 → 下拉显示文案（认不出就给红色）。</summary>
    private static string AnnotationLabelFor(string? hex)
    {
        var normalized = string.IsNullOrWhiteSpace(hex) ? "#FFFF3B30" : hex.Trim().ToUpperInvariant();

        foreach (var (label, value) in AnnotationColorHex)
        {
            if (string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return label;
            }
        }

        return "红 #FF3B30";
    }

    partial void OnAnnotationColorChanged(string value)
    {
        if (!_initialized)
        {
            return;
        }

        var stored = AnnotationColorHex.GetValueOrDefault(value, "#FFFF3B30");
        _ = AppSettingsService.Current.UpdateAsync(settings => settings with { AnnotationColor = stored });
    }

    /// <summary>「保存剪贴板内容到文件夹」的目标文件夹；空 = 未设置（热键触发时会提示）。</summary>
    [ObservableProperty]
    private string _clipboardSaveFolder = string.Empty;

    /// <summary>"快捷转码"用的码制（把剪贴板文本一键变成码）；默认二维码。</summary>
    [ObservableProperty]
    private string _quickCodeFormat = "QR_CODE";

    /// <summary>可选的码制（来自 Core 的清单，与生成页同一份）。</summary>
    public IReadOnlyList<string> CodeFormats => BarcodeFormats.Writable;

    /// <summary>是否在窗口底部显示平台信息（写进配置文件）。</summary>
    [ObservableProperty]
    private bool _showPlatformSummary = true;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>「全局热键」区块（单独一个 VM，免得 SettingsViewModel 继续膨胀）。</summary>
    public HotkeySettingsViewModel Hotkey { get; } = new();

    /// <summary>可选的界面语言（第一项是"跟随系统"）。</summary>
    public ObservableCollection<LanguageOption> Languages { get; } = [];

    [ObservableProperty]
    private LanguageOption? _language;

    public string PlatformSummary => PlatformText.Summary;

    public string ScreenCaptureDescription => PlatformText.Capture;

    public string NotificationDescription => PlatformText.Notification;

    public string AutoStartDescription => PlatformText.AutoStart;

    public string ClipboardWatchDescription => PlatformText.Clipboard;

    /// <summary>当前平台是否支持剪贴板监听（不支持时界面上的开关要禁用）。</summary>
    public bool ClipboardWatchSupported => PlatformServices.ClipboardWatcher.IsSupported;

    // ---- 截图 ----

    /// <summary>
    /// 框选截图时是否识别光标下的窗口（单击直接截整个窗口）。
    /// 直接读写配置：本项没有额外的界面状态，避免再维护一套 _initialized 同步。
    /// </summary>
    public bool CaptureWindowDetection
    {
        get => AppSettingsService.Current.Settings.CaptureWindowDetection;
        set
        {
            if (value == CaptureWindowDetection)
            {
                return;
            }

            _ = AppSettingsService.Current.UpdateAsync(settings => settings with { CaptureWindowDetection = value });
            OnPropertyChanged();
        }
    }

    /// <summary>当前平台是否支持窗口识别（需要前台窗口探测）。</summary>
    public bool WindowDetectionSupported => PlatformServices.ForegroundApp.IsSupported;

    /// <summary>
    /// 框选截图时是否识别光标下的<b>子元素</b>（橙色元素框 / 单击截元素 / 滚轮换层）。
    /// <b>默认关闭</b>：只保留窗口截取与自由框选，需要细粒度截取时再打开。
    /// </summary>
    public bool CaptureElementDetection
    {
        get => AppSettingsService.Current.Settings.CaptureElementDetection;
        set
        {
            if (value == CaptureElementDetection)
            {
                return;
            }

            _ = AppSettingsService.Current.UpdateAsync(settings => settings with { CaptureElementDetection = value });
            OnPropertyChanged();
        }
    }

    /// <summary>当前平台是否支持子元素识别（需要 UIA，Windows 有实现）。</summary>
    public bool ElementDetectionSupported => PlatformServices.ForegroundApp.IsSupported;

    /// <summary>
    /// 截窗口时窗口超出屏幕：用 PrintWindow 整窗渲染，屏幕外的部分也能截到。
    /// 与 <see cref="CaptureWholeWindowOccluded"/> 各自独立，都直接读写配置。
    /// </summary>
    public bool CaptureWholeWindowOffScreen
    {
        get => AppSettingsService.Current.Settings.CaptureWholeWindowOffScreen;
        set
        {
            if (value == CaptureWholeWindowOffScreen)
            {
                return;
            }

            _ = AppSettingsService.Current.UpdateAsync(settings => settings with { CaptureWholeWindowOffScreen = value });
            OnPropertyChanged();
        }
    }

    /// <summary>截窗口时窗口被其它窗口挡住：同样整窗渲染，被挡住的部分也能截到。</summary>
    public bool CaptureWholeWindowOccluded
    {
        get => AppSettingsService.Current.Settings.CaptureWholeWindowOccluded;
        set
        {
            if (value == CaptureWholeWindowOccluded)
            {
                return;
            }

            _ = AppSettingsService.Current.UpdateAsync(settings => settings with { CaptureWholeWindowOccluded = value });
            OnPropertyChanged();
        }
    }

    /// <summary>当前平台是否支持"整窗渲染"（Windows 的 PrintWindow；其它平台没有等价能力）。</summary>
    public bool WholeWindowCaptureSupported => PlatformServices.WindowCapture.IsSupported;

    /// <summary>
    /// 单击截取子元素时四边外扩（正）/ 内缩（负）的像素数，默认 2。
    /// 界面用 <see cref="decimal"/>（<c>NumericUpDown.Value</c> 的类型），写回配置时取整并夹在 ±50。
    /// </summary>
    public decimal? ElementCapturePadding
    {
        get => AppSettingsService.Current.Settings.ElementCapturePadding;
        set
        {
            var clamped = Math.Clamp((int)Math.Round(value ?? 0), -50, 50);
            if (clamped == AppSettingsService.Current.Settings.ElementCapturePadding)
            {
                return;
            }

            _ = AppSettingsService.Current.UpdateAsync(settings => settings with { ElementCapturePadding = clamped });
            OnPropertyChanged();
        }
    }

    // ---- 遮罩上「子元素识别」相关的按键（任务书：涉及的按键全可以自定义） ----

    /// <summary>子元素层级切换方式：<c>wheel</c>（滚轮，默认）/ <c>tab</c> / <c>arrows</c> / <c>none</c>。</summary>
    public string ElementLevelKey
    {
        get => AppSettingsService.Current.Settings.ElementLevelKey;
        set
        {
            if (value == ElementLevelKey)
            {
                return;
            }

            _ = AppSettingsService.Current.UpdateAsync(settings => settings with { ElementLevelKey = value });
            OnPropertyChanged();
        }
    }

    /// <summary>层级切换方式的可选值（下拉用；值本身就是配置里存的字符串）。</summary>
    public IReadOnlyList<string> ElementLevelKeyOptions { get; } = ["wheel", "tab", "arrows", "none"];

    /// <summary>按住它忽略子元素、单击直接截整个窗口：Ctrl（默认）/ Alt / Shift / none。</summary>
    public string ElementIgnoreModifier
    {
        get => AppSettingsService.Current.Settings.ElementIgnoreModifier;
        set
        {
            if (value == ElementIgnoreModifier)
            {
                return;
            }

            _ = AppSettingsService.Current.UpdateAsync(settings => settings with { ElementIgnoreModifier = value });
            OnPropertyChanged();
        }
    }

    /// <summary>遮罩上的修饰键可选值（下拉用）：Ctrl / Alt / Shift / none。</summary>
    public IReadOnlyList<string> MaskModifierOptions { get; } = ["Ctrl", "Alt", "Shift", "none"];

    // ---- 光标标记（框选遮罩上的定格光标） ----
    // 全部直接读写配置：没有额外的界面状态，不需要 _initialized 同步。

    /// <summary>遮罩上是否标出光标位置。</summary>
    public bool CursorMarkerEnabled
    {
        get => AppSettingsService.Current.Settings.CursorMarkerEnabled;
        set
        {
            if (value == CursorMarkerEnabled)
            {
                return;
            }

            _ = AppSettingsService.Current.UpdateAsync(settings => settings with { CursorMarkerEnabled = value });
            OnPropertyChanged();
        }
    }

    /// <summary>遮罩出现时先显示光标图像（默认关：任务书要求"此时不显示光标，只显示描出的边框"）。</summary>
    public bool CursorMarkerShowCursor
    {
        get => AppSettingsService.Current.Settings.CursorMarkerShowCursor;
        set
        {
            if (value == CursorMarkerShowCursor)
            {
                return;
            }

            _ = AppSettingsService.Current.UpdateAsync(settings => settings with { CursorMarkerShowCursor = value });
            OnPropertyChanged();
        }
    }

    /// <summary>是否用虚线框描出光标（默认开）。</summary>
    public bool CursorMarkerShowFrame
    {
        get => AppSettingsService.Current.Settings.CursorMarkerShowFrame;
        set
        {
            if (value == CursorMarkerShowFrame)
            {
                return;
            }

            _ = AppSettingsService.Current.UpdateAsync(settings => settings with { CursorMarkerShowFrame = value });
            OnPropertyChanged();
        }
    }

    /// <summary>虚线框颜色。</summary>
    public string CursorMarkerColor
    {
        get => AppSettingsService.Current.Settings.CursorMarkerColor;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == CursorMarkerColor)
            {
                return;
            }

            _ = AppSettingsService.Current.UpdateAsync(settings => settings with { CursorMarkerColor = value.Trim() });
            OnPropertyChanged();
        }
    }

    /// <summary>虚线框线宽（0.5…8 像素）。</summary>
    public double CursorMarkerThickness
    {
        get => AppSettingsService.Current.Settings.CursorMarkerThickness;
        set
        {
            var clamped = Math.Clamp(value, 0.5, 8);
            if (Math.Abs(clamped - CursorMarkerThickness) < 0.001)
            {
                return;
            }

            _ = AppSettingsService.Current.UpdateAsync(settings => settings with { CursorMarkerThickness = clamped });
            OnPropertyChanged();
        }
    }

    /// <summary>虚线框不透明度（0.1…1）。</summary>
    public double CursorMarkerOpacity
    {
        get => AppSettingsService.Current.Settings.CursorMarkerOpacity;
        set
        {
            var clamped = Math.Clamp(value, 0.1, 1);
            if (Math.Abs(clamped - CursorMarkerOpacity) < 0.001)
            {
                return;
            }

            _ = AppSettingsService.Current.UpdateAsync(settings => settings with { CursorMarkerOpacity = clamped });
            OnPropertyChanged();
        }
    }

    /// <summary>从色板挑一个虚线框颜色。</summary>
    [RelayCommand]
    private async Task PickCursorColorAsync()
    {
        var picked = await ColorPalette.PickAsync(CursorMarkerColor);
        if (picked is not null)
        {
            CursorMarkerColor = picked;
        }
    }

    /// <summary>按一下它切换"显示光标 / 截图带光标"：Shift / Ctrl / Alt / none。</summary>
    public string CaptureCursorKey
    {
        get => AppSettingsService.Current.Settings.CaptureCursorKey;
        set
        {
            if (value == CaptureCursorKey)
            {
                return;
            }

            _ = AppSettingsService.Current.UpdateAsync(settings => settings with { CaptureCursorKey = value });
            OnPropertyChanged();
        }
    }

    /// <summary>切换键的可选值（下拉用）。</summary>
    public IReadOnlyList<string> CaptureCursorKeyOptions { get; } = ["Shift", "Ctrl", "Alt", "none"];

    // ---- 设置子页导航 ----

    [ObservableProperty]
    private string _activeSection = "general";

    public bool SectionGeneral => ActiveSection == "general";

    public bool SectionAppearance => ActiveSection == "appearance";

    public bool SectionHotkey => ActiveSection == "hotkey";

    public bool SectionCommands => ActiveSection == "commands";

    public bool SectionRecognition => ActiveSection == "recognition";

    /// <summary>「贴图」子页：剪贴板 → 贴图这一段的功能都放这里。</summary>
    public bool SectionPin => ActiveSection == "pin";

    /// <summary>「多屏」子页：跨屏框选。</summary>
    public bool SectionMultiScreen => ActiveSection == "multiscreen";

    /// <summary>「标注」子页：字体 / 默认线宽 / 默认颜色。</summary>
    public bool SectionAnnotation => ActiveSection == "annotation";

    public bool SectionInterface => ActiveSection == "interface";

    public bool SectionPermissions => ActiveSection == "permissions" && ElevationSupported;

    [RelayCommand]
    private void SelectSection(string? section)
    {
        if (!string.IsNullOrEmpty(section))
        {
            ActiveSection = section;
        }
    }

    partial void OnActiveSectionChanged(string value)
    {
        OnPropertyChanged(nameof(SectionGeneral));
        OnPropertyChanged(nameof(SectionAppearance));
        OnPropertyChanged(nameof(SectionHotkey));
        OnPropertyChanged(nameof(SectionCommands));
        OnPropertyChanged(nameof(SectionRecognition));
        OnPropertyChanged(nameof(SectionInterface));
        OnPropertyChanged(nameof(SectionPermissions));
        OnPropertyChanged(nameof(SectionPin));
        OnPropertyChanged(nameof(SectionMultiScreen));
        OnPropertyChanged(nameof(SectionAnnotation));
    }

    // ---- 执行命令（user/cmd） ----

    /// <summary>
    /// 命令下拉里的一项：<c>名称 (热键)</c> + 管理员标记。
    /// <para>
    /// 用 <see cref="ToString"/> 而不是 <c>ItemTemplate</c>：ComboBox 的**收起状态**拿不到模板里的
    /// DataContext（会只剩静态文本），而 <c>ToString</c> 在收起和展开时走同一份字符串。
    /// </para>
    /// </summary>
    public sealed record CommandOption(UserCommand Command, string Display)
    {
        public override string ToString() => Display;
    }

    /// <summary>用户命令列表（来自 <c>user/cmd/*.json</c>）。</summary>
    public ObservableCollection<UserCommand> Commands { get; } = [];

    /// <summary>下拉用的包装（带管理员标记）；与 <see cref="Commands"/> 同步重建。</summary>
    public ObservableCollection<CommandOption> CommandOptions { get; } = [];

    [ObservableProperty]
    private CommandOption? _selectedCommandOption;

    [ObservableProperty]
    private UserCommand? _selectedCommand;

    partial void OnSelectedCommandOptionChanged(CommandOption? value)
    {
        // 下拉选中 → 表单回填（走 OnSelectedCommandChanged）
        SelectedCommand = value?.Command;
    }

    /// <summary>下拉里怎么显示：<c>名称 (热键)</c>，勾了管理员的再加一个标记。</summary>
    private static string Describe(UserCommand command) =>
        command.RunAsAdmin
            ? $"{command}  [{I18n.T("Str.Settings.CommandAdminTag")}]"
            : command.ToString();

    [ObservableProperty] private string _commandName = string.Empty;
    [ObservableProperty] private string _commandProgram = string.Empty;
    [ObservableProperty] private string _commandArguments = string.Empty;
    [ObservableProperty] private string _commandDirectory = string.Empty;
    [ObservableProperty] private string _commandHotkey = string.Empty;

    /// <summary>「以管理员身份运行」勾选框（Windows 弹 UAC / Linux 走 pkexec）。</summary>
    [ObservableProperty] private bool _commandRunAsAdmin;

    private string _editingCommandId = string.Empty;

    public string CommandsPath => UserCommandStore.Current.Directory;

    partial void OnSelectedCommandChanged(UserCommand? value)
    {
        if (value is null)
        {
            return;
        }

        _editingCommandId = value.Id;
        CommandName = value.Name;
        CommandProgram = value.Program;
        CommandArguments = value.Arguments ?? string.Empty;
        CommandDirectory = value.WorkingDirectory ?? string.Empty;
        CommandHotkey = value.Hotkey ?? string.Empty;
        CommandRunAsAdmin = value.RunAsAdmin;
    }

    [RelayCommand]
    private void NewCommand()
    {
        _editingCommandId = string.Empty;
        SelectedCommandOption = null;
        SelectedCommand = null;
        CommandName = "新命令";
        CommandProgram = string.Empty;
        CommandArguments = string.Empty;
        CommandDirectory = string.Empty;
        CommandHotkey = string.Empty;
        CommandRunAsAdmin = false;
        SetStatus("Str.Settings.CommandNewHint");
    }

    [RelayCommand]
    private void SaveCommand()
    {
        if (string.IsNullOrWhiteSpace(CommandProgram))
        {
            SetStatus("Str.Settings.CommandRequired");
            return;
        }

        var command = new UserCommand
        {
            Id = _editingCommandId,
            Name = CommandName,
            Program = CommandProgram.Trim(),
            Arguments = string.IsNullOrWhiteSpace(CommandArguments) ? null : CommandArguments,
            WorkingDirectory = string.IsNullOrWhiteSpace(CommandDirectory) ? null : CommandDirectory.Trim(),
            Hotkey = string.IsNullOrWhiteSpace(CommandHotkey) ? null : CommandHotkey.Trim(),
            RunAsAdmin = CommandRunAsAdmin,
        };

        var saved = UserCommandStore.Current.Save(command);
        _editingCommandId = saved.Id;
        ReloadCommands();
        SelectedCommandOption = CommandOptions.FirstOrDefault(option => option.Command.Id == saved.Id);
        SetStatus("Str.Settings.CommandSaved", saved.Name);
    }

    [RelayCommand]
    private void DeleteCommand()
    {
        if (SelectedCommand is not { } command)
        {
            return;
        }

        UserCommandStore.Current.Delete(command.Id);
        _editingCommandId = string.Empty;
        ReloadCommands();
        SelectedCommandOption = null;
        SelectedCommand = null;
        SetStatus("Str.Settings.CommandDeleted");
    }

    /// <summary>
    /// 「试运行」：**按表单里的值**启动一次（不要求先保存），权限跟随「以管理员身份运行」勾选框。
    /// 用来确认程序路径 / 参数 / 工作目录对不对 —— 省得存盘、绑键、再按热键试。
    /// </summary>
    [RelayCommand]
    private Task TestCommandAsync() => RunCommandOnceAsync(CommandRunAsAdmin);

    /// <summary>「管理员试运行」：不管勾选框，强制以管理员身份跑一次（会弹 UAC / Polkit）。</summary>
    [RelayCommand]
    private Task TestCommandElevatedAsync() => RunCommandOnceAsync(true);

    private async Task RunCommandOnceAsync(bool asAdmin)
    {
        var program = CommandProgram?.Trim();
        if (string.IsNullOrEmpty(program))
        {
            SetStatus("Str.Settings.CommandRequired");
            return;
        }

        var binding = new HotkeyBinding(HotkeyActionIds.RunCommand, "F24")
        {
            Command = program,
            Arguments = string.IsNullOrWhiteSpace(CommandArguments) ? null : CommandArguments,
            WorkingDirectory = string.IsNullOrWhiteSpace(CommandDirectory) ? null : CommandDirectory.Trim(),
            RunAsAdmin = asAdmin,
        };

        try
        {
            await Task.Run(() => CommandLauncher.Start(binding));
            SetStatus(
                asAdmin ? "Str.Settings.CommandTestStartedAdmin" : "Str.Settings.CommandTestStarted",
                program);
        }
        catch (Exception ex)
        {
            SetStatus("Str.Settings.CommandTestFailed", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenCommandsFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(CommandsPath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = CommandsPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("Str.Settings.ThemeOpenFailed", ex.Message);
        }
    }

    /// <summary>用系统文件夹选择器挑「保存剪贴板内容」的目标文件夹（也可以直接在输入框里手填）。</summary>
    [RelayCommand]
    private async Task BrowseClipboardSaveFolderAsync()
    {
        if (MainWindow?.StorageProvider is not { } storage)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = I18n.T("Str.Settings.ClipboardSaveFolderPick"),
            AllowMultiple = false,
        });

        if (folders.Count == 0)
        {
            return;
        }

        var path = folders[0].TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            ClipboardSaveFolder = path;
            SetStatus("Str.Settings.ClipboardSaveFolderSet", path);
        }
    }

    /// <summary>在资源管理器里打开目标文件夹（没设置或建不出来就给提示）。</summary>
    [RelayCommand]
    private void OpenClipboardSaveFolder()
    {
        var folder = ClipboardSaveFolder?.Trim();

        if (string.IsNullOrEmpty(folder))
        {
            SetStatus("Str.ClipSave.NoFolder");
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("Str.Settings.ThemeOpenFailed", ex.Message);
        }
    }

    /// <summary>把旧配置（settings.json）里 actionId=runCommand 的行迁移成 user/cmd 文件，并从配置里移除。</summary>
    [RelayCommand]
    private async Task MigrateLegacyCommandsAsync()
    {
        var legacy = AppSettingsService.Current.Settings.Hotkeys
            .Where(binding => binding.ActionId == HotkeyActionIds.RunCommand && !string.IsNullOrWhiteSpace(binding.Command))
            .ToList();

        if (legacy.Count == 0)
        {
            SetStatus("Str.Settings.CommandNothingToMigrate");
            return;
        }

        foreach (var binding in legacy)
        {
            var fileName = Path.GetFileName(binding.Command!);

            UserCommandStore.Current.Save(new UserCommand
            {
                Name = string.IsNullOrWhiteSpace(fileName) ? "旧命令" : fileName,
                Program = binding.Command!,
                Arguments = binding.Arguments,
                WorkingDirectory = binding.WorkingDirectory,
                Hotkey = binding.Keys,
            });
        }

        await AppSettingsService.Current.UpdateAsync(settings => settings with
        {
            Hotkeys = new HotkeyBindings(settings.Hotkeys.Where(binding =>
                binding.ActionId != HotkeyActionIds.RunCommand)),
        });

        ReloadCommands();
        SetStatus("Str.Settings.CommandMigrated", legacy.Count);
    }

    /// <summary>按仓库内容重建命令列表（热键服务订阅了仓库变化，会自己重新注册）。</summary>
    private void ReloadCommands()
    {
        var currentId = SelectedCommand?.Id ?? _editingCommandId;
        var guard = _initialized;
        _initialized = false;

        Commands.Clear();
        CommandOptions.Clear();
        foreach (var command in UserCommandStore.Current.Commands)
        {
            Commands.Add(command);
            CommandOptions.Add(new CommandOption(command, Describe(command)));
        }

        if (!string.IsNullOrEmpty(currentId))
        {
            SelectedCommandOption = CommandOptions.FirstOrDefault(option => option.Command.Id == currentId);
        }
        else
        {
            SelectedCommandOption = null;
            SelectedCommand = null;
        }

        _initialized = guard;
        OnPropertyChanged(nameof(CommandsPath));
    }

    // ---- 外观 / 主题：颜色 / 圆角 / 字体 / 遮罩（改动即时预览，写文件才落地） ----

    [ObservableProperty]
    private string _colorBackground = string.Empty;

    [ObservableProperty]
    private string _colorForeground = string.Empty;

    [ObservableProperty]
    private string _colorMuted = string.Empty;

    [ObservableProperty]
    private string _colorAccent = string.Empty;

    [ObservableProperty]
    private string _colorPanel = string.Empty;

    [ObservableProperty]
    private string _colorBorder = string.Empty;

    [ObservableProperty]
    private string _colorWarning = string.Empty;

    [ObservableProperty]
    private string _colorSuccess = string.Empty;

    [ObservableProperty]
    private string _colorDanger = string.Empty;

    [ObservableProperty]
    private string _colorSelection = string.Empty;

    [ObservableProperty]
    private string _colorMask = string.Empty;

    partial void OnColorBackgroundChanged(string value) => PreviewTheme();

    partial void OnColorForegroundChanged(string value) => PreviewTheme();

    partial void OnColorMutedChanged(string value) => PreviewTheme();

    partial void OnColorAccentChanged(string value) => PreviewTheme();

    partial void OnColorPanelChanged(string value) => PreviewTheme();

    partial void OnColorBorderChanged(string value) => PreviewTheme();

    partial void OnColorWarningChanged(string value) => PreviewTheme();

    partial void OnColorSuccessChanged(string value) => PreviewTheme();

    partial void OnColorDangerChanged(string value) => PreviewTheme();

    partial void OnColorSelectionChanged(string value) => PreviewTheme();

    partial void OnColorMaskChanged(string value) => PreviewTheme();

    /// <summary>面板 / 卡片圆角（0…32）。改动即时预览。</summary>
    [ObservableProperty]
    private double _themeCornerRadius = 8;

    partial void OnThemeCornerRadiusChanged(double value) => PreviewTheme();

    /// <summary>预览框圆角（0…32）。改动即时预览。</summary>
    [ObservableProperty]
    private double _themePreviewCornerRadius = 4;

    partial void OnThemePreviewCornerRadiusChanged(double value) => PreviewTheme();

    /// <summary>自定义字体（留空 = 跟随系统）。改动即时预览；也可以从「系统字体」下拉里挑。</summary>
    [ObservableProperty]
    private string _themeFontFamily = string.Empty;

    partial void OnThemeFontFamilyChanged(string value)
    {
        // 手动填的名字可能正好等于某个系统字体：让下拉跟着高亮 / 清空
        OnPropertyChanged(nameof(ThemeFontSelection));
        PreviewTheme();
    }

    private IReadOnlyList<string>? _fontFamilies;

    /// <summary>系统里已装的字体名（下拉用；按名字排序、忽略大小写去重）。</summary>
    public IReadOnlyList<string> FontFamilies => _fontFamilies ??= LoadFontFamilies();

    /// <summary>「系统字体」下拉的选中项：只有填写的名字正好是系统字体时才有值（否则下拉显示空）。</summary>
    public string? ThemeFontSelection
    {
        get => FontFamilies.FirstOrDefault(
            name => string.Equals(name, ThemeFontFamily.Trim(), StringComparison.OrdinalIgnoreCase));
        set
        {
            if (string.IsNullOrWhiteSpace(value) ||
                string.Equals(value, ThemeFontFamily, StringComparison.Ordinal))
            {
                return;
            }

            ThemeFontFamily = value;   // 走上面的赋值 → 即时预览
        }
    }

    /// <summary>
    /// 取系统字体列表。Avalonia 的 <c>FontManager.SystemFonts</c> 在精简环境 / 无字体配置时
    /// 可能抛异常或为空，这时退化成"只能手填"，不影响其它设置。
    /// </summary>
    private static IReadOnlyList<string> LoadFontFamilies()
    {
        try
        {
            return Avalonia.Media.FontManager.Current.SystemFonts
                .Select(font => font.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>「主题名」输入框：只给「另存为新主题」用，不写进当前主题。</summary>
    [ObservableProperty]
    private string _themeName = string.Empty;

    /// <summary>遮罩样式下拉项（<c>dim</c> / <c>none</c>，文案随语言变）。</summary>
    public sealed record ThemeMaskOption(string Style, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    /// <summary>「截图遮罩样式」下拉的候选：整屏变暗 / 不遮罩。</summary>
    public ObservableCollection<ThemeMaskOption> MaskStyles { get; } = [];

    [ObservableProperty]
    private ThemeMaskOption? _maskStyle;

    partial void OnMaskStyleChanged(ThemeMaskOption? value) => PreviewTheme();

    private static string Pick(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private bool _syncingTheme;

    private ThemeDefinition CurrentTheme => ThemeService.Current.CurrentTheme;

    /// <summary>
    /// 当前主题在主题列表里的**原始定义**（不是预览过的那份）。
    /// 「恢复」「保存」都以它为基准：预览会改写 <see cref="ThemeService.CurrentTheme"/>，
    /// 拿它当基准的话「恢复」只会恢复成刚改出来的样子。
    /// </summary>
    private ThemeDefinition ThemeSource()
    {
        var applied = CurrentTheme;
        return ThemeService.Current.Available
            .FirstOrDefault(theme => string.Equals(theme.Id, applied.Id, StringComparison.OrdinalIgnoreCase))
            ?? applied;
    }

    /// <summary>从色板选一个颜色写回对应输入框（property = Background / Foreground / ... / Mask）。</summary>
    [RelayCommand]
    private async Task PickColorAsync(string? property)
    {
        if (string.IsNullOrWhiteSpace(property))
        {
            return;
        }

        var current = property switch
        {
            "Background" => ColorBackground,
            "Foreground" => ColorForeground,
            "Muted" => ColorMuted,
            "Accent" => ColorAccent,
            "Panel" => ColorPanel,
            "Border" => ColorBorder,
            "Warning" => ColorWarning,
            "Success" => ColorSuccess,
            "Danger" => ColorDanger,
            "Selection" => ColorSelection,
            "Mask" => ColorMask,
            _ => null,
        };

        var picked = await ColorPalette.PickAsync(current);
        if (picked is null)
        {
            return;
        }

        switch (property)
        {
            case "Background": ColorBackground = picked; break;
            case "Foreground": ColorForeground = picked; break;
            case "Muted": ColorMuted = picked; break;
            case "Accent": ColorAccent = picked; break;
            case "Panel": ColorPanel = picked; break;
            case "Border": ColorBorder = picked; break;
            case "Warning": ColorWarning = picked; break;
            case "Success": ColorSuccess = picked; break;
            case "Danger": ColorDanger = picked; break;
            case "Selection": ColorSelection = picked; break;
            case "Mask": ColorMask = picked; break;
        }
    }

    /// <summary>把当前主题的颜色 / 圆角 / 字体 / 遮罩填进控件（不触发预览）。</summary>
    private void LoadThemeFields(ThemeDefinition? theme = null)
    {
        _syncingTheme = true;

        var source = theme ?? ThemeSource();
        ColorBackground = source.Colors.Background;
        ColorForeground = source.Colors.Foreground;
        ColorMuted = source.Colors.MutedForeground;
        ColorAccent = source.Colors.Accent;
        ColorPanel = source.Colors.Panel;
        ColorBorder = source.Colors.Border;
        ColorWarning = source.Colors.Warning;
        ColorSuccess = source.Colors.Success;
        ColorDanger = source.Colors.Danger;
        ColorSelection = source.Colors.Selection;
        ColorMask = source.Mask.Color;
        ThemeCornerRadius = source.CornerRadius;
        ThemePreviewCornerRadius = source.PreviewCornerRadius;
        ThemeFontFamily = source.FontFamily ?? string.Empty;
        MaskStyle = FindMaskStyle(source.Mask.Style);

        _syncingTheme = false;
    }

    private ThemeMaskOption FindMaskStyle(string? style)
    {
        var normalized = ThemeMaskStyles.Normalize(style);
        return MaskStyles.FirstOrDefault(option => option.Style == normalized) ?? MaskStyles[0];
    }

    /// <summary>遮罩样式下拉的文案随语言变 —— 语言切换时重建。</summary>
    private void RebuildMaskStyles()
    {
        var current = MaskStyle?.Style ?? ThemeMaskStyles.Normalize(CurrentTheme.Mask.Style);
        var guard = _initialized;
        _initialized = false;

        MaskStyles.Clear();
        foreach (var style in ThemeMaskStyles.All)
        {
            var key = style == ThemeMaskStyles.None
                ? "Str.Settings.ThemeMaskNone"
                : "Str.Settings.ThemeMaskDim";
            MaskStyles.Add(new ThemeMaskOption(style, I18n.T(key)));
        }

        MaskStyle = MaskStyles.FirstOrDefault(option => option.Style == current) ?? MaskStyles[0];

        _initialized = guard;
    }

    /// <summary>控件的当前值合成一套主题（不写文件）。</summary>
    private ThemeDefinition ComposeTheme(ThemeDefinition source, string? id = null, string? name = null) =>
        (source.CopyAs(id ?? source.Id, name ?? source.Name) with
        {
            CornerRadius = ThemeCornerRadius,
            PreviewCornerRadius = ThemePreviewCornerRadius,
            FontFamily = string.IsNullOrWhiteSpace(ThemeFontFamily) ? null : ThemeFontFamily.Trim(),
            Colors = new ThemeColors
            {
                Background = Pick(ColorBackground, source.Colors.Background),
                Foreground = Pick(ColorForeground, source.Colors.Foreground),
                MutedForeground = Pick(ColorMuted, source.Colors.MutedForeground),
                Accent = Pick(ColorAccent, source.Colors.Accent),
                Panel = Pick(ColorPanel, source.Colors.Panel),
                Border = Pick(ColorBorder, source.Colors.Border),
                Warning = Pick(ColorWarning, source.Colors.Warning),
                Success = Pick(ColorSuccess, source.Colors.Success),
                Danger = Pick(ColorDanger, source.Colors.Danger),
                Selection = Pick(ColorSelection, source.Colors.Selection),
            },
            Mask = new ThemeMask
            {
                Color = Pick(ColorMask, source.Mask.Color),
                Style = MaskStyle?.Style ?? ThemeMaskStyles.Normalize(source.Mask.Style),
            },
        }).Normalized();

    /// <summary>输入框变化时实时预览（只影响本次运行，不写文件）。</summary>
    private void PreviewTheme()
    {
        if (_syncingTheme || !_initialized)
        {
            return;
        }

        ThemeService.Current.Apply(ComposeTheme(ThemeSource()));
    }

    /// <summary>「恢复主题」：丢掉内存里的预览，回到主题文件（或内置定义）本来的样子。</summary>
    [RelayCommand]
    private void ResetTheme()
    {
        var source = ThemeSource();
        LoadThemeFields(source);
        ThemeService.Current.Apply(source);
        SetStatus("Str.Settings.ThemeColorsReset");
    }

    /// <summary>
    /// 「保存到当前主题」：写回当前主题对应的文件。
    /// 内置主题（深色 / 浅色 / 跟随系统）本身没有文件，会另存为 <c>custom.json</c> 并切过去。
    /// </summary>
    [RelayCommand]
    private void SaveTheme()
    {
        var source = ThemeSource();
        var id = ResolveSaveTargetId();
        var name = string.Equals(id, source.Id, StringComparison.OrdinalIgnoreCase)
            ? source.Name
            : string.IsNullOrWhiteSpace(ThemeName) ? I18n.T("Str.Settings.ThemeCustomName") : ThemeName.Trim();

        WriteTheme(ComposeTheme(source, id, name));
    }

    /// <summary>「另存为新主题」：用当前外观 + 「主题名」生成一个新主题文件并切过去。</summary>
    [RelayCommand]
    private void SaveThemeAs()
    {
        var name = ThemeName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            SetStatus("Str.Settings.ThemeNeedName");
            return;
        }

        var id = ThemeDefinition.SuggestId(name, ThemeService.Current.Available.Select(theme => theme.Id));
        if (WriteTheme(ComposeTheme(ThemeSource(), id, name)))
        {
            ThemeName = string.Empty;
        }
    }

    /// <summary>「保存到当前主题」写哪个 id：能写回文件就写回，否则落到 custom。</summary>
    private string ResolveSaveTargetId()
    {
        var configured = AppSettingsService.Current.Settings.ThemeId;

        if (string.IsNullOrWhiteSpace(configured) ||
            string.Equals(configured, ThemeDefinition.SystemThemeId, StringComparison.OrdinalIgnoreCase))
        {
            return ThemeDefinition.CustomThemeId;
        }

        // 内置主题本身没有文件；但用户放的同名文件（dark.json）会盖过内置，那种情况写回它
        var builtInWithoutFile = ThemeDefinition.IsBuiltInId(configured) &&
                                 !File.Exists(Path.Combine(ThemesPath, configured + ".json"));

        return builtInWithoutFile ? ThemeDefinition.CustomThemeId : configured;
    }

    /// <summary>写主题文件 → 切到它 → 重新扫描 → 应用。返回是否写成功。</summary>
    private bool WriteTheme(ThemeDefinition theme)
    {
        var fileName = theme.Id + ".json";

        try
        {
            ThemeService.Current.EnsureThemesFolder();
            ThemeService.Current.Export(theme, Path.Combine(ThemesPath, fileName));
        }
        catch (Exception ex)
        {
            SetStatus("Str.Settings.ThemeSaveFailed", ex.Message);
            return false;
        }

        _ = AppSettingsService.Current.UpdateAsync(settings => settings with { ThemeId = theme.Id });
        ThemeService.Current.Reload();
        ThemeService.Current.Apply(theme);
        RebuildThemes(theme.Id);
        LoadThemeFields(theme);
        SetStatus("Str.Settings.ThemeSavedCurrent", fileName);
        return true;
    }

    // ---- 外观 / 主题 ----

    /// <summary>主题下拉项：<see cref="Id"/> 是配置值（<c>system</c> / <c>dark</c> / 用户主题 id）。</summary>
    public sealed record ThemeOption(string Id, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    /// <summary>主题下拉的候选：跟随系统 + 内置 + <c>user/themes</c> 下的用户主题。</summary>
    public ObservableCollection<ThemeOption> Themes { get; } = [];

    [ObservableProperty]
    private ThemeOption? _theme;

    partial void OnThemeChanged(ThemeOption? value)
    {
        if (!_initialized || value is null)
        {
            return;
        }

        _ = AppSettingsService.Current.UpdateAsync(settings => settings with { ThemeId = value.Id });
        ThemeService.Current.ApplyCurrent();
        LoadThemeFields();
    }

    /// <summary>主题文件夹（界面上显示 / 打开资源管理器用）。</summary>
    public string ThemesPath => ThemeService.Current.ThemesDirectory;

    [RelayCommand]
    private void ReloadThemes()
    {
        ThemeService.Current.Reload();
        ThemeService.Current.ApplyCurrent();
        LoadThemeFields();
        SetStatus("Str.Settings.ThemeReloaded");
    }

    [RelayCommand]
    private void OpenThemesFolder()
    {
        ThemeService.Current.EnsureThemesFolder();

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ThemesPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("Str.Settings.ThemeOpenFailed", ex.Message);
        }
    }

    /// <summary>导出到用户挑的文件（没有文件选择器时退回主题目录）。</summary>
    [RelayCommand]
    private async Task ExportThemeAsync()
    {
        var theme = ComposeTheme(ThemeSource());
        var suggested = theme.Id + ".json";

        if (MainWindow?.StorageProvider is { } storage)
        {
            try
            {
                var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = I18n.T("Str.Settings.ThemeExportPick"),
                    SuggestedFileName = suggested,
                    DefaultExtension = "json",
                    FileTypeChoices =
                    [
                        new FilePickerFileType("Kscan theme") { Patterns = ["*.json"] },
                    ],
                });

                var picked = file?.TryGetLocalPath();
                if (string.IsNullOrEmpty(picked))
                {
                    return;   // 用户取消
                }

                ThemeService.Current.Export(theme, picked);
                SetStatus("Str.Settings.ThemeExported", picked);
                return;
            }
            catch (Exception ex)
            {
                SetStatus("Str.Settings.ThemeExportFailed", ex.Message);
                return;
            }
        }

        try
        {
            ThemeService.Current.EnsureThemesFolder();
            var path = Path.Combine(ThemesPath, suggested);
            ThemeService.Current.Export(theme, path);
            SetStatus("Str.Settings.ThemeExported", path);
        }
        catch (Exception ex)
        {
            SetStatus("Str.Settings.ThemeExportFailed", ex.Message);
        }
    }

    /// <summary>用系统默认编辑器打开当前主题的 JSON（内置 / 跟随系统没有文件，退化成打开文件夹）。</summary>
    [RelayCommand]
    private void EditThemeFile()
    {
        var configured = AppSettingsService.Current.Settings.ThemeId;
        var path = string.IsNullOrWhiteSpace(configured) ||
                   string.Equals(configured, ThemeDefinition.SystemThemeId, StringComparison.OrdinalIgnoreCase)
            ? null
            : Path.Combine(ThemesPath, configured + ".json");

        if (path is null || !File.Exists(path))
        {
            SetStatus("Str.Settings.ThemeEditNoFile");
            OpenThemesFolder();
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            SetStatus("Str.Settings.ThemeEditing", path);
        }
        catch (Exception ex)
        {
            SetStatus("Str.Settings.ThemeOpenFailed", ex.Message);
        }
    }

    /// <summary>重建主题下拉（内置 + 用户主题），并保住当前选中项（可用 <paramref name="preferId"/> 指定）。</summary>
    private void RebuildThemes(string? preferId = null)
    {
        var currentId = preferId ?? Theme?.Id ?? AppSettingsService.Current.Settings.ThemeId;
        var guard = _initialized;
        _initialized = false;

        Themes.Clear();
        Themes.Add(new ThemeOption(ThemeDefinition.SystemThemeId, I18n.T("Str.Settings.ThemeSystem")));

        foreach (var theme in ThemeService.Current.Available)
        {
            Themes.Add(new ThemeOption(theme.Id, theme.Name));
        }

        Theme = Themes.FirstOrDefault(t => string.Equals(t.Id, currentId, StringComparison.OrdinalIgnoreCase))
            ?? Themes.FirstOrDefault();

        _initialized = guard;
        OnPropertyChanged(nameof(ThemesPath));
    }

    /// <summary>主题列表或主题文件变化（外部编辑器保存、点重新加载、另存）后刷新界面。</summary>
    private void OnThemesReloaded()
    {
        RebuildThemes();
        LoadThemeFields();
    }

    // ---- 热键排除应用 ----

    /// <summary>排除列表（配置是唯一来源，这里只是它的界面镜像）。</summary>
    public ObservableCollection<string> IgnoredApps { get; } = [];

    /// <summary>输入框草稿：进程名或 exe 路径。</summary>
    [ObservableProperty]
    private string _ignoredAppDraft = string.Empty;

    /// <summary>排除应用里强制触发的修饰键（默认 Ctrl+Alt；空 = 不强制触发）。</summary>
    [ObservableProperty]
    private string _forceHotkeyModifiers = "Ctrl+Alt";

    /// <summary>当前平台是否支持排除应用（需要前台进程探测）。</summary>
    public bool IgnoredAppsSupported => PlatformServices.ForegroundApp.IsSupported;

    partial void OnForceHotkeyModifiersChanged(string value)
    {
        if (!_initialized)
        {
            return;
        }

        _ = AppSettingsService.Current.UpdateAsync(settings => settings with
        {
            ForceHotkeyModifiers = string.IsNullOrWhiteSpace(value) ? null : value.Trim(),
        });
    }

    /// <summary>
    /// 录制「强制修饰键」：这个字段只要修饰键组合（如 Ctrl+Alt），
    /// 所以用只录修饰键的模式，同样要用户确认才写入。
    /// </summary>
    [RelayCommand]
    private async Task CaptureForceModifiersAsync()
    {
        if (await Views.HotkeyCaptureWindow.CaptureAsync(ForceHotkeyModifiers, modifierOnly: true) is { } captured)
        {
            ForceHotkeyModifiers = captured;
        }
    }

    /// <summary>把输入框里的内容加进排除列表。</summary>
    [RelayCommand]
    private async Task AddIgnoredAppAsync()
    {
        var text = (IgnoredAppDraft ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            SetStatus("Str.Settings.ExcludedInvalid");
            return;
        }

        await AddIgnoredAppEntryAsync(text);
        IgnoredAppDraft = string.Empty;
    }

    /// <summary>从排除列表移除一项（条目是完整路径或进程名，原样匹配）。</summary>
    [RelayCommand]
    private async Task RemoveIgnoredAppAsync(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return;
        }

        await AppSettingsService.Current.UpdateAsync(settings => settings with
        {
            IgnoredApps = new AppList(settings.IgnoredApps.Where(item =>
                !string.Equals(item, entry, StringComparison.OrdinalIgnoreCase))),
        });

        SetStatus("Str.Settings.ExcludedRemoved");
    }

    /// <summary>用文件选择器挑一个 exe（也可以手填进程名）。</summary>
    [RelayCommand]
    private async Task BrowseIgnoredAppAsync()
    {
        var window = MainWindow;
        if (window?.StorageProvider is not { } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = I18n.T("Str.Settings.ExcludedPickFile"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(I18n.T("Str.Settings.ExcludedPickFileType"))
                {
                    Patterns = ["*.exe"],
                    MimeTypes = ["application/x-msdownload"],
                },
            ],
        });

        if (files.Count == 0)
        {
            return;
        }

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await AddIgnoredAppEntryAsync(path);
    }

    private async Task AddIgnoredAppEntryAsync(string text)
    {
        var processName = ExcludedAppMatcher.NormalizeProcessName(null, text);
        var fullPath = text.Contains('\\') || text.Contains('/') ? text : null;

        if (ExcludedAppMatcher.IsExcluded(
                AppSettingsService.Current.Settings.IgnoredApps,
                processName,
                fullPath))
        {
            SetStatus("Str.Settings.ExcludedAlready", processName);
            return;
        }

        await AppSettingsService.Current.UpdateAsync(settings => settings with
        {
            IgnoredApps = new AppList([.. settings.IgnoredApps, text]),
        });

        SetStatus("Str.Settings.ExcludedAdded", processName);
    }

    // ---- 提权 / 最高权限自启 ----

    public bool ElevationSupported => PlatformServices.Elevation.IsSupported;

    public bool IsElevated => PlatformServices.Elevation.IsElevated;

    public string ElevationStatus => I18n.T(IsElevated
        ? "Str.Settings.ElevationStatusElevated"
        : "Str.Settings.ElevationStatusNormal");

    public bool ElevatedAutoStartSupported => PlatformServices.ElevatedAutoStart.IsSupported;

    /// <summary>以最高权限开机自启（计划任务；与普通自启互斥）。</summary>
    [ObservableProperty]
    private bool _elevatedAutoStart;

    partial void OnElevatedAutoStartChanged(bool value)
    {
        if (_initialized)
        {
            _ = ToggleElevatedAutoStartAsync();
        }
    }

    /// <summary>
    /// 以管理员身份重新启动自己（解决 UIPI 让热键在任务管理器前台下失效）。
    /// 先释放单实例锁，新的提权实例才能接手。
    /// </summary>
    [RelayCommand]
    private async Task RestartElevatedAsync()
    {
        if (!ElevationSupported || IsElevated)
        {
            return;
        }

        SetStatus("Str.Settings.ElevationRestarting");

        // 两个锁都要放：管道端点 + 内核 Mutex，否则提权后的新实例会因为抢不到 Mutex 直接退出
        PlatformServices.SingleInstance.Release();
        PlatformServices.InstanceLock.Dispose();

        var result = await PlatformServices.Elevation.RestartElevatedAsync();

        if (!result.Success)
        {
            // 没启动成功就把锁都拿回来，否则会出现两个实例
            PlatformServices.InstanceLock.TryAcquire();
            PlatformServices.SingleInstance.TryAcquire(App.HandleCommandAsync);
            SetStatus("Str.Settings.ElevationFailed", result.Message);
            return;
        }

        (Avalonia.Application.Current as App)?.ExitApplication();
    }

    private async Task ToggleElevatedAutoStartAsync()
    {
        var result = await PlatformServices.ElevatedAutoStart.SetEnabledAsync(ElevatedAutoStart);

        if (result.Success)
        {
            SetStatus(ElevatedAutoStart
                ? "Str.Settings.ElevatedAutoStartOn"
                : "Str.Settings.ElevatedAutoStartOff");

            // 两个自启开关互斥：开启计划任务时平台层会删掉 HKCU Run 项，界面同步回来
            _initialized = false;
            AutoStart = PlatformServices.AutoStart.IsEnabled();
            _initialized = true;
        }
        else
        {
            SetStatus("Str.Settings.ElevatedAutoStartFailed", result.Message);

            _initialized = false;
            ElevatedAutoStart = PlatformServices.ElevatedAutoStart.IsEnabled();
            _initialized = true;
        }
    }

    private async Task LoadElevatedAutoStartAsync()
    {
        if (!ElevatedAutoStartSupported)
        {
            return;
        }

        var enabled = await Task.Run(() => PlatformServices.ElevatedAutoStart.IsEnabled());

        Dispatcher.UIThread.Post(() =>
        {
            _initialized = false;
            ElevatedAutoStart = enabled;
            _initialized = true;
        });
    }

    private static Avalonia.Controls.TopLevel? MainWindow =>
        (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>配置文件位置（含最近一次读写失败原因），方便用户直接去看。</summary>
    public string ConfigHint
    {
        get
        {
            var service = AppSettingsService.Current;
            return service.LastError is { Length: > 0 } error
                ? I18n.F("Str.Settings.ConfigPathError", service.FilePath, error)
                : I18n.F("Str.Settings.ConfigPath", service.FilePath);
        }
    }

    /// <summary>会话类型提示：Wayland 下热键/置顶等能力受限，明确写出来。</summary>
    public string SessionHint => !OperatingSystem.IsLinux()
        ? string.Empty
        : I18n.T(PlatformServices.SessionType == "wayland"
            ? "Str.Settings.SessionWayland"
            : "Str.Settings.SessionX11");

    [RelayCommand]
    private async Task ToggleAutoStartAsync()
    {
        var result = await PlatformServices.AutoStart.SetEnabledAsync(AutoStart);

        if (result.Success)
        {
            SetStatus(AutoStart ? "Str.Settings.AutoStartOn" : "Str.Settings.AutoStartOff");
        }
        else
        {
            SetStatus("Str.Settings.AutoStartFailed", result.Message);

            _initialized = false;
            AutoStart = PlatformServices.AutoStart.IsEnabled();
            _initialized = true;
        }
    }

    partial void OnAutoStartChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }

        // 开关一变就直接应用，不给用户"忘了点保存"的机会
        _ = ToggleAutoStartAsync();
    }

    partial void OnAutoCopySingleResultChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }

        _ = AppSettingsService.Current.UpdateAsync(settings => settings with { AutoCopySingleResult = value });
    }

    partial void OnOpenUrlAutomaticallyChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }

        _ = AppSettingsService.Current.UpdateAsync(settings => settings with { OpenUrlAutomatically = value });
    }

    partial void OnQuickCodeFormatChanged(string value)
    {
        if (!_initialized || string.IsNullOrEmpty(value))
        {
            return;
        }

        _ = AppSettingsService.Current.UpdateAsync(settings => settings with { QuickCodeFormat = value });
    }

    partial void OnWatchClipboardChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }

        // 写进配置就够了：ScanViewModel 订阅了配置变化，会自己启停监听
        _ = AppSettingsService.Current.UpdateAsync(settings => settings with { WatchClipboard = value });
    }

    partial void OnPinRemoteImagesChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }

        _ = AppSettingsService.Current.UpdateAsync(settings => settings with { PinRemoteImages = value });
    }

    partial void OnPinTopmostByDefaultChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }

        _ = AppSettingsService.Current.UpdateAsync(settings => settings with { PinTopmostByDefault = value });
    }

    partial void OnCaptureAcrossScreensChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }

        _ = AppSettingsService.Current.UpdateAsync(settings => settings with { CaptureAcrossScreens = value });
    }

    partial void OnClipboardSaveFolderChanged(string value)
    {
        if (!_initialized)
        {
            return;
        }

        // 手填或浏览都会走到这里；空串 = 未设置，热键触发时提示用户
        var folder = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        _ = AppSettingsService.Current.UpdateAsync(settings => settings with { ClipboardSaveFolder = folder });
    }

    partial void OnShowPlatformSummaryChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }

        _ = AppSettingsService.Current.UpdateAsync(settings => settings with { ShowPlatformSummary = value });
    }

    partial void OnLanguageChanged(LanguageOption? value)
    {
        if (!_initialized || value is null)
        {
            return;
        }

        _ = ApplyLanguageAsync(value.Code);
    }

    /// <summary>切换语言：先换字典（DynamicResource 立刻刷新），再写进配置文件。</summary>
    private async Task ApplyLanguageAsync(string code)
    {
        I18n.Current.Apply(code);

        // 配置里存 null 表示"跟随系统"，这样用户改了系统语言界面会跟着变
        await AppSettingsService.Current.UpdateAsync(settings =>
            settings with { Language = string.IsNullOrEmpty(code) ? null : code });

        RebuildLanguages();
    }

    /// <summary>重建语言下拉（显示名要跟着当前语言走），并保住当前选中项。</summary>
    private void RebuildLanguages()
    {
        var code = Language?.Code ?? AppSettingsService.Current.Settings.Language ?? string.Empty;
        var guard = _initialized;
        _initialized = false;

        Languages.Clear();
        foreach (var item in I18n.AvailableLanguages)
        {
            Languages.Add(new LanguageOption(item, DisplayNameFor(item)));
        }

        // 守卫已置 false，赋值不会触发 ApplyLanguageAsync；属性变化通知由生成器负责
        Language = FindLanguage(code);

        _initialized = guard;
    }

    private LanguageOption? FindLanguage(string? code)
    {
        var normalized = code ?? string.Empty;
        return Languages.FirstOrDefault(l => l.Code.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ?? Languages.FirstOrDefault();
    }

    private static string DisplayNameFor(string code) => code switch
    {
        I18n.Chinese => I18n.T("Str.Lang.ZhCn"),
        I18n.English => I18n.T("Str.Lang.EnUs"),
        _ => I18n.T("Str.Lang.System"),
    };

    /// <summary>配置在别处被改（例如手工编辑配置文件）时同步回界面。</summary>
    private void RefreshFromSettings()
    {
        var settings = AppSettingsService.Current.Settings;
        _initialized = false;
        AutoCopySingleResult = settings.AutoCopySingleResult;
        ShowMainWindowAfterCaptureScan = settings.ShowMainWindowAfterCaptureScan;
        OpenUrlAutomatically = settings.OpenUrlAutomatically;
        WatchClipboard = settings.WatchClipboard;
        PinRemoteImages = settings.PinRemoteImages;
        PinTopmostByDefault = settings.PinTopmostByDefault;
        AnimatedFreezeKey = AnimatedFreezeKeyOption(settings.AnimatedFreezeKey);
        CaptureAcrossScreens = settings.CaptureAcrossScreens;
        CaptureAcrossScreensWindowDetection = settings.CaptureAcrossScreensWindowDetection;
        MultiScreenGapFill = GapFill.IsBlack(settings.MultiScreenGapFill)
            ? MultiScreenGapFillOptions[1]
            : MultiScreenGapFillOptions[0];
        ClipboardSaveFolder = settings.ClipboardSaveFolder ?? string.Empty;
        QuickCodeFormat = settings.QuickCodeFormat;
        StartSilently = settings.StartSilently;
        MinimizeToTray = settings.MinimizeToTray;
        CloseToTray = settings.CloseToTray;
        ShowPlatformSummary = settings.ShowPlatformSummary;
        ForceHotkeyModifiers = settings.ForceHotkeyModifiers ?? string.Empty;

        IgnoredApps.Clear();
        foreach (var app in settings.IgnoredApps)
        {
            IgnoredApps.Add(app);
        }

        _initialized = true;

        OnPropertyChanged(nameof(CaptureWindowDetection));
        OnPropertyChanged(nameof(CaptureElementDetection));
        OnPropertyChanged(nameof(CaptureWholeWindowOffScreen));
        OnPropertyChanged(nameof(CaptureWholeWindowOccluded));
        OnPropertyChanged(nameof(ElementCapturePadding));
        OnPropertyChanged(nameof(ElementLevelKey));
        OnPropertyChanged(nameof(ElementIgnoreModifier));
        RebuildThemes();   // 主题可能在别处被改，或新加了主题文件
        OnPropertyChanged(nameof(ConfigHint));
    }

    /// <summary>语言切换后刷新所有"算好的"文案。</summary>
    private void RefreshLocalizedText()
    {
        if (_statusKey is not null)
        {
            Status = I18n.F(_statusKey, _statusArgs);
        }

        RebuildLanguages();
        RebuildMaskStyles();
        RebuildThemes();
        OnPropertyChanged(nameof(ConfigHint));
        OnPropertyChanged(nameof(SessionHint));
        OnPropertyChanged(nameof(PlatformSummary));
        OnPropertyChanged(nameof(ScreenCaptureDescription));
        OnPropertyChanged(nameof(NotificationDescription));
        OnPropertyChanged(nameof(AutoStartDescription));
        OnPropertyChanged(nameof(ClipboardWatchDescription));
        OnPropertyChanged(nameof(ElevationStatus));
    }

    private void SetStatus(string key, params object?[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        Status = I18n.F(key, args);
    }
}
