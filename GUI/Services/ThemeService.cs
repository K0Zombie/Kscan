using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Kscan.Core.Configuration;
using Kscan.Platform;

namespace Kscan.App.Services;

/// <summary>
/// 主题：把 <see cref="ThemeDefinition"/> 落到 Avalonia 资源上（颜色 / 圆角 / 字体 / 遮罩），
/// 并管理 <c>user/themes/</c> 下的用户主题文件。
/// <para>
/// 约定：界面里用 <c>{DynamicResource Kscan*Brush}</c> / <c>KscanCornerRadius</c> /
/// <c>KscanPreviewCornerRadius</c> / <c>KscanFontFamily</c>，换主题时改资源即可实时生效，
/// 不需要重建窗口。
/// </para>
/// <para>
/// 主题目录里的 <c>*.json</c> 被外部编辑器改动时会<b>自动重新加载并应用</b>（防抖 300ms），
/// 所以"改文件 → 界面立刻变"不用按任何按钮；「重新加载」按钮仍保留，用于目录被外部整体替换的情况。
/// </para>
/// </summary>
public sealed class ThemeService : IDisposable
{
    private readonly List<ThemeDefinition> _available = [];
    private readonly Lock _watchGate = new();
    private readonly string? _directoryOverride;
    private readonly bool _watchFiles;
    private bool _loaded;
    private FileSystemWatcher? _watcher;
    private Timer? _reloadTimer;

    /// <param name="themesDirectory">主题目录；<c>null</c> = 用 <see cref="PlatformPaths.ThemesDirectory"/>。</param>
    /// <param name="watchFiles">是否监听主题目录的变化（单测关掉，避免定时器干扰其它用例）。</param>
    public ThemeService(string? themesDirectory = null, bool watchFiles = true)
    {
        _directoryOverride = themesDirectory;
        _watchFiles = watchFiles;
    }

    public static ThemeService Current { get; } = new();

    /// <summary>内置 + 用户主题（跟随系统不是条目，单独处理）。</summary>
    public IReadOnlyList<ThemeDefinition> Available => _available;

    public string ThemesDirectory => _directoryOverride ?? PlatformPaths.ThemesDirectory;

    /// <summary>当前生效的主题（跟随系统时返回解析后的深/浅色）。</summary>
    public ThemeDefinition CurrentTheme { get; private set; } = ThemeDefinition.Dark;

    /// <summary>一套主题被应用后触发（颜色 / 圆角 / 字体 / 遮罩都写完资源了）。</summary>
    public event EventHandler? Changed;

    /// <summary>主题<b>列表</b>变化（重新扫描、或主题目录里的文件被增删改）：界面据此刷新下拉。</summary>
    public event EventHandler? ThemesChanged;

    /// <summary>框选遮罩用的画刷（颜色见主题 <c>mask.color</c>）。</summary>
    public IBrush MaskBrush => ParseBrush(CurrentTheme.Mask.Color, Color.FromArgb(160, 0, 0, 0));

    /// <summary>当前主题是否要铺遮罩（<c>mask.style = none</c> 时不铺）。</summary>
    public bool MaskVisible => !ThemeMaskStyles.IsNone(CurrentTheme.Mask.Style);

    /// <summary>在 App 初始化时调用一次：建目录、写分享说明、加载并应用主题，并开始监听文件变化。</summary>
    public void Initialize()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        EnsureThemesFolder();
        Reload();
        ApplyCurrent();
        StartWatching();
    }

    /// <summary>
    /// 重新扫描 <c>user/themes/*.json</c>。
    /// <para>
    /// 同一个 id 出现多次时<b>后来者覆盖</b>：先内置（<c>dark</c> / <c>light</c>），再按文件名顺序读用户文件。
    /// 于是"想改深色就放一份 <c>dark.json</c>"是可行的；<c>system</c> 是保留字，用户文件里写它会被忽略。
    /// </para>
    /// </summary>
    public void Reload()
    {
        var themes = new List<ThemeDefinition>(ThemeDefinition.BuiltIn);

        try
        {
            Directory.CreateDirectory(ThemesDirectory);

            foreach (var file in Directory.GetFiles(ThemesDirectory, "*.json")
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                ThemeDefinition? theme;
                var fallbackId = Path.GetFileNameWithoutExtension(file);
                try
                {
                    // 文件里没写 id 时用文件名（"文件名去掉后缀就是主题 id"）
                    theme = ThemeDefinition.TryParse(File.ReadAllText(file), fallbackId);
                }
                catch (IOException)
                {
                    continue;   // 编辑器正占着这个文件：这轮跳过，下次文件事件再读
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                if (theme is null)
                {
                    continue;   // 坏文件跳过，不影响其它主题
                }

                var id = string.IsNullOrWhiteSpace(theme.Id) ? fallbackId : theme.Id.Trim();

                if (string.Equals(id, ThemeDefinition.SystemThemeId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;   // "system" 是设置里的保留值，不能当主题 id
                }

                theme = theme with { Id = id };

                var index = themes.FindIndex(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    themes[index] = theme;   // 用户文件覆盖同 id 的内置 / 先读到的主题
                }
                else
                {
                    themes.Add(theme);
                }
            }
        }
        catch (Exception)
        {
            // 读不到主题目录就只用内置的
        }

        _available.Clear();
        _available.AddRange(themes);
        ThemesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>按配置里的 <see cref="AppSettings.ThemeId"/> 应用主题。</summary>
    public void ApplyCurrent()
    {
        var id = AppSettingsService.Current.Settings.ThemeId;

        var theme = string.IsNullOrWhiteSpace(id) || id == ThemeDefinition.SystemThemeId
            ? (IsSystemDark() ? ThemeDefinition.Dark : ThemeDefinition.Light)
            : _available.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
              ?? (IsSystemDark() ? ThemeDefinition.Dark : ThemeDefinition.Light);

        Apply(theme);
    }

    /// <summary>把一套主题写进当前应用的资源。</summary>
    public void Apply(ThemeDefinition theme)
    {
        theme = theme.Normalized();
        CurrentTheme = theme;

        if (Application.Current is not { } app)
        {
            return;
        }

        app.RequestedThemeVariant = theme.Base == "light" ? ThemeVariant.Light : ThemeVariant.Dark;

        // 对比度保护：前景与背景太接近时强制用黑/白，避免"改一个颜色整屏文字消失"。
        var background = ParseColor(theme.Colors.Background, Colors.Black);
        var foreground = ParseColor(theme.Colors.Foreground, Colors.White);

        if (Math.Abs(Luminance(background) - Luminance(foreground)) < 0.30)
        {
            foreground = Luminance(background) > 0.5 ? Colors.Black : Colors.White;
        }

        var muted = ParseColor(
            theme.Colors.MutedForeground,
            Color.FromArgb(153, foreground.R, foreground.G, foreground.B));

        // 次要文字与背景对比不足时退回"前景色的半透明版本"，否则标签会整片看不见。
        if (Math.Abs(Luminance(background) - Luminance(muted)) < 0.20)
        {
            muted = Color.FromArgb(153, foreground.R, foreground.G, foreground.B);
        }

        SetBrush(app, "KscanBackgroundBrush", theme.Colors.Background);
        SetBrushColor(app, "KscanForegroundBrush", foreground);
        SetBrushColor(app, "KscanMutedBrush", muted);
        SetBrush(app, "KscanAccentBrush", theme.Colors.Accent);
        SetBrushColor(app, "KscanAccentForegroundBrush", Luminance(ParseColor(theme.Colors.Accent, Colors.DodgerBlue)) > .55 ? Colors.Black : Colors.White);
        SetBrush(app, "KscanPanelBrush", theme.Colors.Panel);
        SetBrush(app, "KscanBorderBrush", theme.Colors.Border);
        SetBrush(app, "KscanWarningBrush", theme.Colors.Warning);
        SetBrush(app, "KscanSuccessBrush", theme.Colors.Success);
        SetBrush(app, "KscanDangerBrush", theme.Colors.Danger);
        SetBrush(app, "KscanSelectionBrush", theme.Colors.Selection);

        app.Resources["KscanCornerRadius"] = new CornerRadius(theme.CornerRadius);
        app.Resources["KscanPreviewCornerRadius"] = new CornerRadius(theme.PreviewCornerRadius);

        // 截图遮罩：颜色 + 样式（dim = 整屏铺一层，none = 不铺）。框选窗口直接读这两个资源。
        app.Resources["KscanMaskBrush"] = new SolidColorBrush(ParseColor(theme.Mask.Color, Color.FromArgb(160, 0, 0, 0)));
        app.Resources["KscanMaskEnabled"] = MaskVisible;
        app.Resources["KscanEffectiveMaskBrush"] = MaskVisible
            ? new SolidColorBrush(ParseColor(theme.Mask.Color, Color.FromArgb(160, 0, 0, 0)))
            : Brushes.Transparent;

        // 字体：主题没指定就**不设这个资源**（窗口的 FontFamily 退回"未设置"，
        // 由 Fluent / FontManagerOptions 决定默认字体）。
        // 不能塞 FontFamily.Default（= "$Default"）：它会显式覆盖整棵可视树的字体，
        // 而本机 / Avalonia 12 下 "$Default" 建立不了 glyphTypeface，界面直接崩
        // （实测：`Could not create glyphTypeface. Font family: $Default`）。
        if (ParseFont(theme.FontFamily) is { } font)
        {
            app.Resources["KscanFontFamily"] = font;
        }
        else
        {
            app.Resources.Remove("KscanFontFamily");
        }

        // 覆盖 Fluent 的"容器 / 文字 / 边框 / 强调"资源，普通界面也会跟着变色。
        // 刻意<b>不</b>覆盖 Button* / TextControl* / ComboBox*：那些是控件自己的底色，
        // 跟面板色绑在一起时，用户把面板改成一个颜色就会让按钮、输入框一片糊、文字看不清。
        SetBrush(app, "SystemControlBackgroundAltHighBrush", theme.Colors.Background);
        SetBrush(app, "SystemControlBackgroundAltMediumLowBrush", theme.Colors.Panel);
        SetBrush(app, "SystemControlBackgroundChromeMediumLowBrush", theme.Colors.Panel);
        SetBrush(app, "SystemControlBackgroundBaseLowBrush", theme.Colors.Panel);
        SetBrush(app, "SystemControlBackgroundListLowBrush", theme.Colors.Panel);
        SetBrushColor(app, "SystemControlForegroundBaseHighBrush", foreground);
        SetBrushColor(app, "SystemControlForegroundBaseMediumBrush", muted);
        SetBrushColor(app, "SystemControlForegroundBaseMediumHighBrush", foreground);
        SetBrush(app, "SystemControlBorderDefaultBrush", theme.Colors.Border);
        SetBrush(app, "SystemControlHighlightAccentBrush", theme.Colors.Accent);
        SetBrush(app, "SystemControlBackgroundAccentBrush", theme.Colors.Accent);

        app.Resources["SystemAccentColor"] = ParseColor(theme.Colors.Accent, Colors.DodgerBlue);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把一套主题写到指定文件（设置页「保存 / 导出」）。</summary>
    public void Export(ThemeDefinition theme, string path) =>
        File.WriteAllText(path, theme.Normalized().ToJson());

    /// <summary>
    /// 首次启动生成分享说明。说明本身带版本标记：只有"本程序生成的旧版"会被升级成新版，
    /// 用户自己改过的（没有标记、也不以固定标题开头）一律不动。
    /// </summary>
    public void EnsureThemesFolder()
    {
        try
        {
            Directory.CreateDirectory(ThemesDirectory);

            var readme = Path.Combine(ThemesDirectory, "README.md");
            if (!File.Exists(readme) || IsOutdatedStockGuide(readme))
            {
                File.WriteAllText(readme, ShareGuide);
            }
        }
        catch (Exception)
        {
            // 目录建不出来时主题功能降级为"只有内置"
        }
    }

    /// <summary>这份 README 是本程序生成的旧版说明吗（没有版本标记、且以固定标题开头）。</summary>
    private static bool IsOutdatedStockGuide(string path)
    {
        try
        {
            var text = File.ReadAllText(path);

            if (text.Contains(ShareGuideMarker, StringComparison.Ordinal))
            {
                return false;   // 已经是最新版
            }

            return text.TrimStart().StartsWith("# Kscan 主题分享说明", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>监听主题目录：外部编辑器保存 <c>*.json</c> 后自动重新加载并应用。</summary>
    private void StartWatching()
    {
        if (!_watchFiles || _watcher is not null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(ThemesDirectory);

            var watcher = new FileSystemWatcher(ThemesDirectory, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };

            watcher.Changed += OnThemeFileTouched;
            watcher.Created += OnThemeFileTouched;
            watcher.Deleted += OnThemeFileTouched;
            watcher.Renamed += OnThemeFileTouched;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception)
        {
            // 监听起不来时退化成"点重新加载"
        }
    }

    /// <summary>一次保存会触发多个文件事件，这里防抖（300ms 后再读，读的是最终内容）。</summary>
    private void OnThemeFileTouched(object sender, FileSystemEventArgs e)
    {
        lock (_watchGate)
        {
            _reloadTimer ??= new Timer(_ => ReloadFromDisk(), null, Timeout.Infinite, Timeout.Infinite);
            _reloadTimer.Change(300, Timeout.Infinite);
        }
    }

    private void ReloadFromDisk() => Dispatcher.UIThread.Post(() =>
    {
        Reload();
        ApplyCurrent();
    });

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;

        lock (_watchGate)
        {
            _reloadTimer?.Dispose();
            _reloadTimer = null;
        }
    }

    private static bool IsSystemDark()
    {
        // 跟随系统：Avalonia 的 ActualThemeVariant 在 Default 下会解析成当前系统明暗
        return Application.Current?.ActualThemeVariant != ThemeVariant.Light;
    }

    private static void SetBrush(Application app, string key, string value) =>
        app.Resources[key] = ParseBrush(value, Colors.Transparent);

    private static IBrush ParseBrush(string? value, Color fallback) =>
        Color.TryParse(value, out var color) ? new SolidColorBrush(color) : new SolidColorBrush(fallback);

    private static void SetBrushColor(Application app, string key, Color color) =>
        app.Resources[key] = new SolidColorBrush(color);

    /// <summary>
    /// 字体：留空 = 跟随系统，返回 <c>null</c>（调用方**不设**字体资源，让默认字体生效）。
    /// 写错字体名不会抛异常，渲染时会退回系统默认字体。
    /// </summary>
    private static FontFamily? ParseFont(string? family)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return null;
        }

        try
        {
            return new FontFamily(family.Trim());
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>相对亮度（0=黑，1=白），用来判断前景/背景对比度。</summary>
    private static double Luminance(Color color) =>
        (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;

    private static Color ParseColor(string? value, Color fallback) =>
        Color.TryParse(value, out var color) ? color : fallback;

    /// <summary>README 的版本标记：升级说明时靠它区分"我们生成的旧版"与"用户自己写的"。</summary>
    private const string ShareGuideMarker = "<!-- kscan:themes-readme v3 -->";

    private const string ShareGuide = """
        <!-- kscan:themes-readme v3 -->

        # Kscan 主题分享说明

        主题文件就放在这个文件夹里：`user/themes/*.json`。程序启动时会读取这里的全部 `*.json`，
        文件名去掉后缀就是主题 id；之后**改文件会自动生效**（不用重启，也不用点按钮）。
        设置页「外观 / 主题 → 重新加载」用于目录被外部整体替换的情况。

        ## 主题包含什么

        主题管的是**外观**：颜色、圆角、字体、截图遮罩。按键、语言这类行为设置在 `settings.json` 里，不属于主题。

        - 11 个颜色：背景 / 前景 / 次要文字 / 强调 / 面板 / 边框 / 警告 / 成功 / 危险 / 选中 / 截图遮罩
        - 圆角：`cornerRadius`（卡片、面板）、`previewCornerRadius`（预览框）
        - 字体：`fontFamily`（留空 = 跟随系统）
        - 截图遮罩：`mask.color`（颜色，含透明度）+ `mask.style`（`none` = 不铺，只画选框（**默认**）；`dim` = 整屏变暗）

        ## 三种做法

        1. **从现有主题改**：设置 → 外观 / 主题，选一个主题，直接改颜色 / 圆角 / 字体 / 遮罩样式，
           改的时候界面立刻变（只在内存里预览）。
        2. **保存**：点「保存到当前主题」覆盖当前主题文件（内置主题没有文件，会自动存成 `custom.json`）；
           或者填一个「主题名」点「另存为新主题」，生成 `my-theme.json` 并切过去。
        3. **只想试试**：改完不保存，退出程序就恢复原样；想留着又不想覆盖，就用「另存为新主题」。

        ## 文件格式

        ```json
        {
          "id": "my-dark",
          "name": "我的深色",
          "base": "dark",                     // dark / light：决定 Fluent 控件的基础明暗
          "colors": {
            "background": "#1E1E1E",
            "foreground": "#F0F0F0",
            "mutedForeground": "#99FFFFFF",
            "accent": "#0078D4",
            "panel": "#1F808080",
            "border": "#33FFFFFF",
            "warning": "#E6A23C",
            "success": "#67C23A",
            "danger": "#F56C6C",
            "selection": "#330078D4"
          },
          "mask": {
            "color": "#A0000000",             // 框选时铺在屏幕上的那层（A = 透明度）
            "style": "none"                   // none（默认）= 不遮罩；dim = 整屏变暗
          },
          "cornerRadius": 8,                  // 面板 / 卡片圆角，0…32
          "previewCornerRadius": 4,           // 预览框圆角，0…32
          "fontFamily": null                  // 留空 / null = 跟随系统；也可写 "Microsoft YaHei"
        }
        ```

        颜色写 `#RRGGBB` 或 `#AARRGGBB`（A = 透明度，00 全透明、FF 不透明）。
        哪一项写错只会让那一项退回默认色，不影响其它字段；整份 JSON 坏了这个主题会被跳过。
        圆角写成负数 / 超大 / `NaN` 会被自动收敛到 0…32，不会让界面起不来。

        ## 分享

        直接把 `my-dark.json` 发给别人，让对方放进自己的 `user/themes/` 即可（放进去就生效）。
        文件名最好与 `id` 一致，方便排查；`system` 是保留字，不能用作 id。
        想覆盖内置主题（深色 / 浅色）就放一份同名文件（`dark.json` / `light.json`），它会盖过内置那套。
        """;
}
