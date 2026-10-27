using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using System.Diagnostics;
using Kscan.Core.Configuration;
using Kscan.Core.Imaging;
using Kscan.Platform;
using Kscan.Platform.Abstractions;

namespace Kscan.App.Services;

/// <summary>
/// Windows 上的框选：把抓到的屏幕图像铺满窗口，用户拖出矩形后返回裁剪结果。
/// <para>
/// 开启「元素识别」时，鼠标移动会同时画两层高亮：
/// <b>黄色 = 整个窗口</b>，<b>橙色 = 窗口内的子元素</b>（UI Automation 的最内层控件）。
/// 单击截取橙色元素（没有元素时截窗口）；<b>滚轮</b>（可改成 Tab / ↑↓）在元素链上
/// <b>依次往上</b>切换层级，到窗口层就是截整个窗口；<b>按住 Ctrl 单击</b>（可改）直接截整个窗口；
/// <b>按住 Ctrl</b>（可改）忽略子元素。拖动仍然是自由框选（移动超过阈值即进入拖框模式）。
/// </para>
/// <para>
/// Linux 不需要这个 —— 那边直接委托 <c>grim -g "$(slurp)" -</c>，选区由 slurp 自己画。
/// </para>
/// </summary>
/// <summary>
/// 框选结果：裁出来的图 + 它在<b>屏幕上的物理像素区域</b>（<c>null</c> = 拿不到位置，例如 Linux 委托外部工具）。
/// 后者供"截图贴回原位"用。
/// </summary>
internal readonly record struct RegionSelection(ImageFrame Frame, PixelRect? ScreenRect);

internal static class RegionSelector
{
    /// <summary>
    /// 上一次"遮罩从开始创建到真正显示出来"花的毫秒数。
    /// 用来把 <c>Ctrl+F3</c> 的卡顿拆开定位：抓屏 / 遮罩显示 / 用户拖框 各占多少。
    /// </summary>
    public static long LastOverlayReadyMs { get; private set; }

    /// <summary>弹出框选窗口；用户取消（Esc / 右键 / 空选区）时返回 <c>null</c>。</summary>
    public static Task<RegionSelection?> SelectAsync(
        ImageFrame desktop,
        bool windowDetection = false,
        bool refreshAfterShow = false,
        IntPtr exclusiveTarget = default,
        ScreenRect? exclusiveTargetRect = null)
    {
        var started = Environment.TickCount64;

        var window = new RegionSelectorWindow(desktop, windowDetection, refreshAfterShow, exclusiveTarget, exclusiveTargetRect);

        // 必须显式 Show()：窗口不显示就收不到任何指针事件，await 会永久挂起
        window.Show();

        LastOverlayReadyMs = Environment.TickCount64 - started;

        return window.Result;
    }
}

internal sealed class RegionSelectorWindow : Window
{
    /// <summary>按下后移动超过这个 DIP 距离才算"拖框"，否则按"单击选元素/窗口"处理。</summary>
    private const double DragThreshold = 4;

    /// <summary>元素 / 窗口探测的节流间隔（毫秒）。UIA 查询不便宜，跟随鼠标每帧查会卡。</summary>
    private const long ProbeIntervalMilliseconds = 66;

    private ImageFrame _desktop;

    /// <summary>遮罩显示后是否要"再抓一次屏"（独占全屏游戏用：第一次抓屏拿不到它的画面）。</summary>
    private readonly bool _refreshAfterShow;

    /// <summary>
    /// 进遮罩前那个"铺满整屏的前台窗口"（真独占全屏应用）。这类窗口被挤出独占全屏后会连画面都没有，
    /// 抓到的快照里找不到它，光标探测就会命中它下面那些窗口 —— 所以这里记住它，
    /// 光标落在它的矩形里时直接把它当成目标窗口，不再去识别下层窗口 / 子元素。
    /// </summary>
    private readonly IntPtr _exclusiveTarget;

    private readonly ScreenRect? _exclusiveTargetRect;
    private readonly bool _windowDetection;
    private readonly Border _selection;

    /// <summary>框选尺寸标签（WxH，物理像素）。</summary>
    private readonly TextBlock _sizeLabel;
    private readonly Border _windowHighlight;
    private readonly Border _elementHighlight;
    private readonly Image _cursorImage;

    /// <summary>
    /// 主题的截图遮罩：铺在桌面图上、选框下面。颜色 / 透明度 / 样式都来自主题
    /// （<c>mask.color</c> / <c>mask.style</c>；样式为 <c>none</c> 时不铺，直接看原图）。
    /// </summary>
    private readonly Border _mask;

    /// <summary>
    /// 定格光标的虚线框（任务书 §3：默认不显示光标图像，只显示描出的边框）。
    /// 框按光标**可见部分**描（见 <see cref="CursorMarkerGeometry"/>），所以和真实光标一样大。
    /// </summary>
    private readonly Avalonia.Controls.Shapes.Rectangle _cursorFrame;

    /// <summary>是否画虚线框（设置项：<c>CursorMarkerShowFrame</c>）。</summary>
    private readonly bool _cursorFrameVisible;

    /// <summary>光标图像里是否算出了可见范围（算不出来就别画空框）。</summary>
    private bool _cursorFrameHasBounds;

    /// <summary>虚线框比光标外扩多少物理像素。</summary>
    private const double CursorFramePadding = 2;

    private readonly Canvas _canvas;
    private readonly TaskCompletionSource<RegionSelection?> _completion = new();

    /// <summary>遮罩出现时定格的光标；<c>null</c> = 关闭了标记 / 平台拿不到光标图像。</summary>
    private readonly CursorSnapshot? _cursor;

    /// <summary>
    /// 遮罩上的桌面位图与光标位图。Avalonia 的 <see cref="Avalonia.Media.Imaging.Bitmap"/> 持有
    /// <b>非托管</b>像素，只靠 GC 终结器回收太慢：一张 2560×1440 的桌面图就是十几 MB，
    /// 连按几次截图内存就一路涨 —— 所以窗口关闭时必须显式释放（见 OnClosed）。
    /// </summary>
    private Avalonia.Media.Imaging.Bitmap _desktopBitmap;

    private readonly Avalonia.Media.Imaging.Bitmap? _cursorBitmap;

    /// <summary>铺桌面图的那个 Image（关窗口时先摘掉 Source，再释放位图）。</summary>
    private readonly Image _desktopImage;

    /// <summary>按一下切换"遮罩上显示光标 / 截图带光标"；初始值来自设置。</summary>
    private bool _cursorToggle;

    /// <summary>切换键是否仍按住（防自动重复连翻）。</summary>
    private bool _toggleHeld;

    /// <summary>切换键（Shift / Ctrl / Alt）；<c>None</c> = 不可切换。</summary>
    private readonly KeyModifiers _captureCursorModifier;

    private Point _origin;
    private Point _pressedAt;
    private bool _pressed;

    private bool _dragging;
    private bool _includeCursor;
    private Rect _rectangle;
    private ScreenRect? _hoverWindow;
    private ScreenRect? _hoverElement;

    /// <summary>
    /// 当前光标下的<b>元素链</b>（从最外层子元素到最内层，<b>不含窗口</b>）。
    /// 「层级依次往上」在它上面移动，不用每换一层重查 UIA。
    /// </summary>
    private IReadOnlyList<ScreenRect> _elementChain = [];

    /// <summary>
    /// 层级偏移：<c>0</c> = 最内层，越大越往外；等于链长时就是「窗口层」
    /// （不画元素框，单击截整个窗口）。鼠标移动时保留它，选定层级不会一飘就丢。
    /// </summary>
    private int _elementLevelOffset;

    /// <summary>层级切换方式（滚轮 / Tab / ↑↓ / 关闭），来自设置。</summary>
    private readonly ElementLevelMode _elementLevelMode;

    /// <summary>按住它忽略子元素（只看窗口），来自设置。</summary>
    private readonly HeldModifier _ignoreModifier;

    /// <summary>当前高亮窗口的句柄（整窗离屏渲染用；<see cref="IntPtr.Zero"/> = 没有）。</summary>
    private IntPtr _hoverWindowHandle;

    /// <summary>窗口超出屏幕时是否允许整窗渲染（设置项）。</summary>
    private readonly bool _wholeWindowOffScreen;

    /// <summary>窗口被其它窗口挡住时是否允许整窗渲染（设置项）。</summary>
    private readonly bool _wholeWindowOccluded;

    /// <summary>单击截元素时四边外扩（正）/ 内缩（负）的像素数（设置项）。</summary>
    private readonly int _elementPadding;

    /// <summary>是否识别子元素（设置项，<b>默认关闭</b>）：关掉时只认窗口，完全不查 UIA。</summary>
    private readonly bool _elementDetection;

    /// <summary>按住它忽略子元素识别（只看窗口、不画元素框，单击直接截窗口）。</summary>
    private bool _ignoreElement;

    private long _lastProbeAt;

    /// <summary>上次探测时光标所在的屏幕坐标（用来判断"鼠标基本没动"）。</summary>
    private int _lastProbeX;
    private int _lastProbeY;

    /// <summary>后台元素探测的世代号：只有最新一次的结果才允许画到界面上。</summary>
    private int _probeGeneration;

    /// <summary>
    /// 本次框选里已经测到 UIA 太慢（≥ <see cref="SlowProbeMilliseconds"/>）就不再试 ——
    /// 全屏游戏 / OpenGL 窗口的 UIA 查询会阻塞几百毫秒，继续试只会让光标更卡。
    /// </summary>
    private bool _elementProbeTooSlow;

    /// <summary>窗口高亮那层半透明填充（全屏窗口时临时摘掉，见 <see cref="ApplyWindow"/>）。</summary>
    private readonly SolidColorBrush _windowHighlightFill;

    /// <summary>单次 UIA 探测超过这个毫秒数就认为"这个窗口的 UIA 太慢"，本次框选不再做元素识别。</summary>
    private const int SlowProbeMilliseconds = 250;

    public RegionSelectorWindow(
        ImageFrame desktop,
        bool windowDetection,
        bool refreshAfterShow = false,
        IntPtr exclusiveTarget = default,
        ScreenRect? exclusiveTargetRect = null)
    {
        _desktop = desktop;
        _refreshAfterShow = refreshAfterShow;
        _exclusiveTarget = exclusiveTarget;
        // 兜底：只要是"进遮罩前的窗口铺满整屏"，就默认把整块屏当目标 ——
        // 游戏被最小化后窗口探测会落到它下面那层，直接框全屏最稳（用户建议的做法）。
        _exclusiveTargetRect = null;   // 独占全屏兜底会让框永远整屏（粘住），先禁用

        _windowDetection = windowDetection;

        var settings = AppSettingsService.Current.Settings;
        _cursorToggle = settings.CursorMarkerShowCursor;
        _wholeWindowOffScreen = settings.CaptureWholeWindowOffScreen;
        _wholeWindowOccluded = settings.CaptureWholeWindowOccluded;
        _elementPadding = settings.ElementCapturePadding;
        _elementDetection = settings.CaptureElementDetection;
        _captureCursorModifier = ParseModifier(settings.CaptureCursorKey);
        _elementLevelMode = ElementLevelModeParser.Parse(settings.ElementLevelKey);
        _ignoreModifier = HeldModifierParser.Parse(settings.ElementIgnoreModifier);
        _cursor = settings.CursorMarkerEnabled ? PlatformServices.CursorCapture.Capture() : null;
        _cursorFrameVisible = settings.CursorMarkerEnabled && settings.CursorMarkerShowFrame;

        // 位图只创建一次，窗口关掉时统一释放
        _desktopBitmap = ResultActions.ToBitmap(desktop);
        _cursorBitmap = _cursor is null ? null : ResultActions.ToBitmap(_cursor.Image);

        if (_refreshAfterShow)
        {
            // 350ms：留给"游戏从独占全屏退回窗口化 + DWM 重新合成"的时间
            DispatcherTimer.RunOnce(() => _ = RefreshDesktopAsync(), TimeSpan.FromMilliseconds(350));

            // 游戏被最小化时会把键盘焦点抢走，遮罩就收不到 Esc（取消不了）。稍后再激活一次遮罩。
            DispatcherTimer.RunOnce(
                () =>
                {
                    if (!IsVisible)
                    {
                        return;
                    }

                    Activate();
                    Focus();
                    RaiseOverFullscreen();
                },
                TimeSpan.FromMilliseconds(450));
        }

        // 虚线框：颜色 / 粗细 / 透明度全部可自定义（任务书 §3）。虚线节拍按线宽算（3/2 个线宽）。
        _cursorFrame = new Avalonia.Controls.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(ParseColor(settings.CursorMarkerColor, Color.FromArgb(255, 0, 200, 255))),
            StrokeThickness = Math.Clamp(settings.CursorMarkerThickness, 0.5, 8),
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3, 2 },
            Opacity = Math.Clamp(settings.CursorMarkerOpacity, 0.1, 1),
            IsVisible = false,
            IsHitTestVisible = false,
        };

        // Avalonia 12：WindowDecorations 取代了旧的 SystemDecorations，且枚举与属性同名，需完全限定
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        Cursor = new Cursor(StandardCursorType.Cross);

        _selection = new Border
        {
            BorderBrush = Brushes.DeepSkyBlue,
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(48, 0, 120, 215)),
            IsVisible = false,
        };

        // 窗口高亮：黄色；元素高亮：橙色（在窗口框内部）。两层同时显示，一眼能看出层级。
        _windowHighlightFill = new SolidColorBrush(Color.FromArgb(20, 0xFF, 0xCC, 0x00));
        _windowHighlight = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x00)),
            BorderThickness = new Thickness(2),
            Background = _windowHighlightFill,
            IsVisible = false,
            IsHitTestVisible = false,
        };

        _elementHighlight = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(28, 0xFF, 0x8C, 0x00)),
            IsVisible = false,
            IsHitTestVisible = false,
        };

        // 定格光标：截图本身不含光标，这里直接把光标图像摆到它定格的位置。
        // 不画方框 / 轮廓 —— 光标形状由图像自己决定，方框反而会和光标"不一样大"。
        _cursorImage = new Image
        {
            Source = _cursorBitmap,
            Stretch = Stretch.Fill,
            IsVisible = false,
            IsHitTestVisible = false,
        };

        _canvas = new Canvas();
        _canvas.Children.Add(_windowHighlight);
        _canvas.Children.Add(_elementHighlight);
        _canvas.Children.Add(_selection);
        // 框选时在旁边显示**实际尺寸**（物理像素）：便于判断"到底框了多大"，也方便反馈偏移问题。
        _sizeLabel = new TextBlock
        {
            FontSize = 12,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            Padding = new Thickness(4, 1),
            IsVisible = false,
            IsHitTestVisible = false,
        };
        _canvas.Children.Add(_sizeLabel);
        _canvas.Children.Add(_cursorFrame);
        _canvas.Children.Add(_cursorImage);

        var image = new Image
        {
            Source = _desktopBitmap,
            Stretch = Stretch.Uniform,
        };
        _desktopImage = image;

        // 截图遮罩（主题的 mask.color / mask.style）：盖在桌面图上、选框下面。
        // 以前这个颜色只写进了资源、没人用 —— 改主题里的「截图遮罩」等于没效果。
        _mask = new Border
        {
            Background = ThemeService.Current.MaskBrush,
            IsVisible = ThemeService.Current.MaskVisible,
            IsHitTestVisible = false,
        };

        // 用 Grid 约束 Image 的尺寸：Canvas 不限制子元素，Image 会按原始像素撑开溢出，
        // 那样实际渲染尺寸就与 Crop() 里基于 _canvas.Bounds 的换算对不上了。
        var root = new Grid();
        root.Children.Add(image);
        root.Children.Add(_mask);
        root.Children.Add(_canvas);

        Content = root;

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerExited += (_, _) => ClearHighlight();
        PointerWheelChanged += OnPointerWheelChanged;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;

        // 布局稳定后再摆一次光标标记：OnOpened 里 Dispatcher.Post 的那次有可能赶在
        // 画布测量完成之前（Bounds 还是 0），只靠一次机会的话标记会整块不显示。
        _canvas.SizeChanged += (_, _) => UpdateCursorMarker();
    }

    public Task<RegionSelection?> Result => _completion.Task;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // 量"到第一帧真正画出来"的耗时：Show() 返回并不等于画面已经出现，
        // 整屏位图上传 GPU + 合成都发生在之后。这段如果大，用户看到的就是"按下后停一下才出蒙版"。
        var firstFrameWatch = System.Diagnostics.Stopwatch.StartNew();

        Dispatcher.UIThread.Post(
            () => GuiLog.Write($"perf: overlay first-frame after show={firstFrameWatch.ElapsedMilliseconds}ms"),
            DispatcherPriority.Render);

        // 铺满**鼠标所在那块屏**（不是固定主屏）：全屏程序常跑在副屏上，遮罩只盖主屏就会
        // "看不见蒙版 / 点不到"，用户报的"全屏程序没正常出蒙版"就是这个。
        // 注意：Bounds 是物理像素，而 Width/Height 是逻辑单位 —— 不按缩放比换算的话，
        // 150% 缩放下窗口会变成屏幕的 1.5 倍，右下角直接跑到屏幕外。
        var cursorPosition = PlatformServices.ForegroundApp.GetCursorPosition();
        var targetScreen = (cursorPosition is { } point
                ? Screens.ScreenFromPoint(new PixelPoint(point.X, point.Y))
                : null)
            ?? Screens.Primary;

        if (targetScreen is { } screen)
        {
            Position = screen.Bounds.Position;
            Width = screen.Bounds.Width / screen.Scaling;
            Height = screen.Bounds.Height / screen.Scaling;

        }

        // 有些全屏程序会自己抢到最前面（或本身就是 topmost）：让平台层显式
        // SetWindowPos(HWND_TOPMOST) 抢一次最上层，稍后再补一次 —— 防止对方在我们之后又抬一次。
        // 注意：这里**不再**做 `Topmost = false; Topmost = true`，Avalonia 里重新设 Topmost 会走
        // 一次窗口层级重建，实测环境下出现过"遮罩根本不出现"的风险，改成只调平台 API。
        Dispatcher.UIThread.Post(RaiseOverFullscreen, DispatcherPriority.Loaded);
        DispatcherTimer.RunOnce(RaiseOverFullscreen, TimeSpan.FromMilliseconds(160));

        // 布局完成后才能拿到 _canvas.Bounds；这时候先按鼠标当前位置画一次高亮
        Dispatcher.UIThread.Post(UpdateHighlight, DispatcherPriority.Background);

        // 光标描边同样要等布局完成（ToCanvasRect 依赖 _canvas.Bounds 与 Position）
        Dispatcher.UIThread.Post(UpdateCursorMarker, DispatcherPriority.Background);
    }

    /// <summary>把遮罩窗口强行提到最上层（压住全屏程序 / topmost 窗口）。</summary>
    private void RaiseOverFullscreen()
    {
        if (!PlatformServices.WindowPlacement.IsSupported)
        {
            return;
        }

        if (TryGetPlatformHandle()?.Handle is { } handle && handle != IntPtr.Zero)
        {
            PlatformServices.WindowPlacement.ForceTopmost(handle);
        }
    }

    /// <summary>
    /// 重新抓一次整屏并换掉遮罩底图（只在"进遮罩前前台是全屏窗口"时调用）。
    /// 尺寸和原来不同就放弃（分辨率变了，坐标换算不可信）。
    /// </summary>
    private async Task RefreshDesktopAsync()
    {
        if (!IsVisible)
        {
            return;
        }

        ImageFrame? fresh;

        try
        {
            fresh = await PlatformServices.ScreenCapture.CaptureDesktopAsync();
        }
        catch (Exception)
        {
            return;
        }

        if (fresh is null || fresh.Width != _desktop.Width || fresh.Height != _desktop.Height)
        {
            return;
        }

        Array.Copy(fresh.Pixels, _desktop.Pixels, _desktop.Pixels.Length);

        var bitmap = ResultActions.ToBitmap(_desktop);
        _desktopImage.Source = bitmap;
        var old = _desktopBitmap;
        _desktopBitmap = bitmap;
        old.Dispose();

        // 底图换了 → 立刻按新画面重新探一次光标下的窗口
        _lastProbeAt = 0;
        UpdateHighlight();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _completion.TrySetResult(null);

        // 关键：**不要在这个时机**把底图摘掉 / 释放位图。
        // 关窗时窗口还会再画一帧，Source 已经为 null 的话这一帧就是窗口背景（黑）——
        // 用户看到的就是"按 Esc 取消时闪一下"。改成排到 UI 队列末尾（那时窗口已经销毁），
        // 既不会闪那一帧黑，也不会让 8~16 MB 位图长期留着。
        // 刻意不在这里 GC.Collect()：紧接着就是解码/识别，主线程上压缩大对象堆会拖慢那一步。
        // 回收放到"结果出来之后的空闲时刻"，见 MemoryHygiene.CompactWhenIdle。
        Dispatcher.UIThread.Post(
            () =>
            {
                _cursorImage.Source = null;
                _desktopImage.Source = null;
                _desktopBitmap.Dispose();
                _cursorBitmap?.Dispose();
            },
            DispatcherPriority.Background);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            return;
        }

        // 切换键是"按一下切换"而不是"按住"：按下时翻转状态，遮罩上立即反映。
        // _toggleHeld 防按住不放时的自动重复把状态来回翻。
        if (IsToggleKey(e) && !_toggleHeld)
        {
            _toggleHeld = true;
            _cursorToggle = !_cursorToggle;
            ApplyCursorVisibility();
        }

        // 修饰键按住 = 忽略子元素（默认 Ctrl，设置里可改）
        if (IsModifierKey(_ignoreModifier, e.Key))
        {
            SetIgnoreElement(true);
            return;
        }

        // 层级切换除了滚轮之外的可选方式（设置里选 Tab / ↑↓；默认滚轮走 OnPointerWheelChanged）
        if (_elementLevelMode == ElementLevelMode.Tab && e.Key == Key.Tab)
        {
            MoveElementLevel(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            e.Handled = true;
        }
        else if (_elementLevelMode == ElementLevelMode.Arrows && e.Key is Key.Up or Key.Down)
        {
            MoveElementLevel(e.Key == Key.Up ? 1 : -1);
            e.Handled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        // 松开后才允许下一次切换（长按只会切一次）
        if (IsToggleKey(e))
        {
            _toggleHeld = false;
        }

        if (IsModifierKey(_ignoreModifier, e.Key))
        {
            SetIgnoreElement(false);
        }
    }

    /// <summary>
    /// 滚轮切换子元素层级（默认方式）：<b>向上滚 = 往外一层</b>（父级），
    /// <b>向下滚 = 收回一层</b>。方式在设置里可以改成 <c>Tab</c> / <c>↑↓</c> / 关闭。
    /// </summary>
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_elementLevelMode != ElementLevelMode.Wheel || e.Delta.Y == 0)
        {
            return;
        }

        // 拖框过程中不切层级：那时两层高亮已经让位给蓝色选框
        if (_pressed || _dragging)
        {
            return;
        }

        MoveElementLevel(Math.Sign(e.Delta.Y));
        e.Handled = true;
    }

    /// <summary>
    /// 在元素链上移动层级：正数 = 往外一层（父级），负数 = 往里一层。到窗口层后不再往外，
    /// 到最内层后不再往里。变化立刻重绘（绕过 50ms 探测节流）。
    /// </summary>
    private void MoveElementLevel(int delta)
    {
        if (!_windowDetection || _elementChain.Count == 0)
        {
            return;
        }

        var next = ElementChainLevels.Clamp(_elementChain, _elementLevelOffset + delta);
        if (next == _elementLevelOffset)
        {
            return;
        }

        _elementLevelOffset = next;
        _hoverElement = ElementAtCurrentLevel();
        UpdateBorder(_elementHighlight, _hoverElement);
    }

    /// <summary>当前层级对应的元素矩形；<c>null</c> = 已到窗口层（不画元素框，单击截窗口）。</summary>
    private ScreenRect? ElementAtCurrentLevel() =>
        _ignoreElement ? null : ElementChainLevels.At(_elementChain, _elementLevelOffset);

    /// <summary>键盘事件里的按键是不是这个修饰键（左右两侧都算）。</summary>
    private static bool IsModifierKey(HeldModifier modifier, Key key) => modifier switch
    {
        HeldModifier.Shift => key is Key.LeftShift or Key.RightShift,
        HeldModifier.Control => key is Key.LeftCtrl or Key.RightCtrl,
        HeldModifier.Alt => key is Key.LeftAlt or Key.RightAlt,
        _ => false,
    };

    /// <summary>鼠标事件里的修饰键状态是不是按着这个修饰键。</summary>
    private static bool IsModifierHeld(HeldModifier modifier, KeyModifiers held) => modifier switch
    {
        HeldModifier.Shift => held.HasFlag(KeyModifiers.Shift),
        HeldModifier.Control => held.HasFlag(KeyModifiers.Control),
        HeldModifier.Alt => held.HasFlag(KeyModifiers.Alt),
        _ => false,
    };

    /// <summary>
    /// 切换"忽略子元素"：修饰键（默认 <c>Ctrl</c>）按住期间元素框不显示，单击直接截整个窗口。
    /// 状态变化后立刻重绘一次 —— 否则要等鼠标移动，而且可能被 50ms 节流吞掉。
    /// </summary>
    private void SetIgnoreElement(bool ignore)
    {
        if (_ignoreElement == ignore)
        {
            return;
        }

        _ignoreElement = ignore;
        _lastProbeAt = 0;
        _hoverElement = null;
        _elementHighlight.IsVisible = false;

        if (!ignore)
        {
            UpdateHighlight();
        }
    }

    /// <summary>本次按下的键是不是"切换光标"键（Shift / Ctrl / Alt 各自的左右键都算）。</summary>
    private bool IsToggleKey(KeyEventArgs e) => _captureCursorModifier switch
    {
        KeyModifiers.Shift => e.Key is Key.LeftShift or Key.RightShift,
        KeyModifiers.Control => e.Key is Key.LeftCtrl or Key.RightCtrl,
        KeyModifiers.Alt => e.Key is Key.LeftAlt or Key.RightAlt,
        _ => false,
    };

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetPosition(_canvas);

        if (e.GetCurrentPoint(_canvas).Properties.IsRightButtonPressed)
        {
            Close();
            return;
        }

        _origin = point;
        _pressedAt = point;
        _pressed = true;
        _dragging = false;
        _rectangle = default;

        _selection.IsVisible = false;
        _windowHighlight.IsVisible = false;
        _elementHighlight.IsVisible = false;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        // 还没按下：只更新高亮（顺便跟踪修饰键是否按住 —— 鼠标移动也要反映"忽略元素"）
        if (!_pressed)
        {
            SetIgnoreElement(IsModifierHeld(_ignoreModifier, e.KeyModifiers));
            UpdateHighlight();
            return;
        }

        var current = e.GetPosition(_canvas);

        if (!_dragging && Distance(_pressedAt, current) < DragThreshold)
        {
            return;
        }

        // 开始拖框：两层高亮让位给蓝色拖选框
        _dragging = true;
        _windowHighlight.IsVisible = false;
        _elementHighlight.IsVisible = false;
        _rectangle = Normalize(_origin, current);

        _selection.IsVisible = true;
        Canvas.SetLeft(_selection, _rectangle.X);
        Canvas.SetTop(_selection, _rectangle.Y);
        _selection.Width = _rectangle.Width;
        _selection.Height = _rectangle.Height;

        // 尺寸标签跟着选区走，显示屏幕物理像素（4x4 像素以下视为取消，不必显示）
        var scale = DisplayScale();
        var pixelWidth = (int)Math.Round(_rectangle.Width / scale);
        var pixelHeight = (int)Math.Round(_rectangle.Height / scale);
        _sizeLabel.Text = pixelWidth + " x " + pixelHeight;
        Canvas.SetLeft(_sizeLabel, _rectangle.X + _rectangle.Width + 6);
        Canvas.SetTop(_sizeLabel, _rectangle.Y + _rectangle.Height + 6);
        _sizeLabel.IsVisible = pixelWidth > 4 && pixelHeight > 4;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        _pressed = false;

        // "带不带光标"由切换状态决定（按一下切换键翻转），不是看释放时按着谁
        _includeCursor = _cursorToggle;

        if (_dragging)
        {
            _dragging = false;

            if (_rectangle.Width < 4 || _rectangle.Height < 4)
            {
                _selection.IsVisible = false;
                return;
            }

            CompleteWithCanvasRect(_rectangle);
            return;
        }

        // 单击（没有拖动）：已到窗口层（含"按住忽略键"时元素框不显示）就截整个窗口，否则截当前层级的元素
        var wantsWholeWindow = _hoverElement is null;
        var target = _hoverElement ?? _hoverWindow;

        if (_windowDetection && target is { } rect)
        {
            // 元素截图按设置外扩 / 内缩（截整个窗口时不受影响）
            if (!wantsWholeWindow && rect.Inflate(_elementPadding) is { } padded)
            {
                rect = padded;
            }

            // 单击窗口 = "截这个窗口的完整内容"。PrintWindow 能拿到屏幕上根本看不到的部分，
            // 但两种场景各自由设置里的开关控制；都关掉时退回屏幕裁剪（= 屏幕上看得到的样子）。
            var offScreen = IsPartiallyOffScreen(rect);
            var wholeCaptureAllowed = offScreen
                ? _wholeWindowOffScreen
                : _wholeWindowOccluded && IsCoveredByOtherWindow();

            if (wantsWholeWindow && wholeCaptureAllowed && TryCaptureWholeWindow(out var whole, out var coverage))
            {
                // 贴图 / 选区位置以"图实际覆盖的屏幕矩形"为准（和裁掉的边一致，不会错位）
                var placed = coverage.Width > 0 && coverage.Height > 0 ? coverage : rect;
                _completion.TrySetResult(
                    new RegionSelection(whole, new PixelRect(placed.X, placed.Y, placed.Width, placed.Height)));
                Close();
                return;
            }

            var canvasRect = ToCanvasRect(rect);
            if (canvasRect.Width >= 4 && canvasRect.Height >= 4)
            {
                CompleteWithCanvasRect(canvasRect);
                return;
            }
        }

        _selection.IsVisible = false;
    }

    /// <summary>高亮窗口是否被其它窗口挡住（遮罩自己不算）。</summary>
    private bool IsCoveredByOtherWindow()
    {
        if (_hoverWindowHandle == IntPtr.Zero)
        {
            return false;
        }

        var overlay = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        return PlatformServices.ForegroundApp.IsWindowCovered(_hoverWindowHandle, overlay);
    }

    /// <summary>窗口矩形是否有一部分落在抓屏图之外（虚拟桌面之外）。</summary>

    private bool IsPartiallyOffScreen(ScreenRect rect) =>
        rect.X < Position.X
        || rect.Y < Position.Y
        || rect.X + rect.Width > Position.X + _desktop.Width
        || rect.Y + rect.Height > Position.Y + _desktop.Height;



    /// <summary>
    /// 用 <see cref="IWindowCapture"/>（Windows 上是 <c>PrintWindow</c>）把整个窗口渲染出来。
    /// 应用拒绝渲染时会得到全黑 / 单色图，这种情况返回 <c>false</c>，调用方回退到屏幕裁剪。
    /// </summary>
    private bool TryCaptureWholeWindow(out ImageFrame frame, out ScreenRect coverage)
    {
        frame = null!;
        coverage = default;

        if (_hoverWindowHandle == IntPtr.Zero || !PlatformServices.WindowCapture.IsSupported)
        {
            return false;
        }

        var captured = PlatformServices.WindowCapture.CaptureWhole(_hoverWindowHandle);
        if (captured is not { } result || result.Image.Width < 4 || result.Image.Height < 4
            || IsNearlyBlank(result.Image))
        {
            return false;
        }

        frame = result.Image;

        // 平台是按像素判断"该不该裁"的，这里跟着它给的屏幕矩形走：
        // 普通窗口 = DWM 可见边框；桌面 / 边框屏 = 原始窗口矩形（一点不裁）
        coverage = result.Coverage;
        return true;
    }

    /// <summary>
    /// 整张图基本是同一个颜色（PrintWindow 对某些窗口返回全黑 / 空白）。
    /// <para>
    /// 采样 512 个点，**≥95% 是同一个颜色**就认为没内容 —— 比"必须 100% 相同"更贴近实际：
    /// 桌面 / UWP 之类窗口 PrintWindow 出来的常常是"绝大部分黑、边上混进几个亮点"，
    /// 旧判据会放过它，于是点击就截到一张黑图（用户报的"点桌面截图是黑屏"）。
    /// </para>
    /// </summary>
    internal static bool IsNearlyBlank(ImageFrame frame)
    {
        const int samples = 512;
        const int blankPercent = 95;

        var total = frame.Width * frame.Height;
        var stride = Math.Max(1, total / samples);
        var counts = new Dictionary<int, int>();

        for (var index = 0; index < total; index += stride)
        {
            var offset = index * ImageFrame.BytesPerPixel;
            var value = frame.Pixels[offset] | (frame.Pixels[offset + 1] << 8) | (frame.Pixels[offset + 2] << 16);
            counts[value] = counts.TryGetValue(value, out var count) ? count + 1 : 1;
        }

        if (counts.Count == 0)
        {
            return true;
        }

        var dominant = counts.Values.Max();
        var sampled = counts.Values.Sum();
        return dominant * 100 >= sampled * blankPercent;
    }

    private void CompleteWithCanvasRect(Rect rectangle)
    {
        var pixels = ToImagePixels(rectangle);

        // 窗口可能有一部分在屏幕外（最大化窗口的边框），裁剪到抓屏图范围内再取
        var x = Math.Clamp(pixels.X, 0, Math.Max(0, _desktop.Width - 1));
        var y = Math.Clamp(pixels.Y, 0, Math.Max(0, _desktop.Height - 1));
        var width = Math.Clamp(pixels.Width, 1, Math.Max(1, _desktop.Width - x));
        var height = Math.Clamp(pixels.Height, 1, Math.Max(1, _desktop.Height - y));

        if (width < 2 || height < 2)
        {
            _selection.IsVisible = false;
            return;
        }

        var frame = _desktop.Crop(x, y, width, height);

        // 切换状态为"带光标"时：把定格的光标图像混合进截出来的这块图里。
        // 坐标要减裁剪区域的原点 (x, y) —— cursor.Left/Top 是抓屏图坐标，不是这块小图的坐标。
        if (_includeCursor && _cursor is { } cursor)
        {
            frame = CursorCompositor.Draw(
                frame, cursor.Image, cursor.Left - Position.X - x, cursor.Top - Position.Y - y);
        }

        _completion.TrySetResult(new RegionSelection(frame, ScreenRectOf((x, y, width, height))));

        Close();
    }

    /// <summary>
    /// 把定格的光标摆到画布上：位置跟着桌面图一起映射（<see cref="ToCanvasRect"/>），
    /// 但<b>大小按物理像素 1:1 换算</b>（<see cref="TopLevel.RenderScaling"/>）。
    /// <para>
    /// 之前尺寸也乘桌面图的显示缩放（<see cref="DisplayScale"/>），于是遮罩画布只要比抓屏图
    /// 小一点点（窗口被系统尺寸上限钳制、多显示器虚拟桌面、以后改成只铺主屏等），
    /// 光标就会跟着整体缩小 —— 表现就是"遮罩上的光标比真实光标偏小"。
    /// 光标是屏幕上的实物尺寸，不该跟随桌面图的缩放。
    /// </para>
    /// </summary>
    private void UpdateCursorMarker()
    {
        if (_cursor is not { } cursor || _canvas.Bounds.Width <= 0 || _canvas.Bounds.Height <= 0)
        {
            // 布局还没稳定（或没有光标快照）。SizeChanged 里会再调一次，所以这里直接返回即可。
            return;
        }

        var rect = ToCanvasRect(new ScreenRect(cursor.Left, cursor.Top, cursor.Image.Width, cursor.Image.Height));
        var scaling = RenderScaling > 0 ? RenderScaling : 1;

        Canvas.SetLeft(_cursorImage, rect.X);
        Canvas.SetTop(_cursorImage, rect.Y);
        _cursorImage.Width = cursor.Image.Width / scaling;
        _cursorImage.Height = cursor.Image.Height / scaling;

        UpdateCursorFrame(cursor, rect, scaling);
        ApplyCursorVisibility();
    }

    /// <summary>
    /// 虚线框贴着光标的<b>可见部分</b>（透明边不算）：光标位图通常比图形本身大一圈，
    /// 按位图尺寸描边会得到一个明显偏大的框 —— 那正是"框和光标大小对不上"的来源。
    /// 位置仍按物理像素 1:1 换算（与光标图像一致），不跟桌面图的显示缩放。
    /// </summary>
    private void UpdateCursorFrame(CursorSnapshot cursor, Rect rect, double scaling)
    {
        var bounds = CursorMarkerGeometry.VisibleBounds(cursor.Image);
        _cursorFrameHasBounds = !bounds.IsEmpty;

        if (!_cursorFrameHasBounds)
        {
            return;   // 整张全透明（拿不到图形），不要画一个空框
        }

        var padding = CursorFramePadding / scaling;

        Canvas.SetLeft(_cursorFrame, rect.X + (bounds.X / scaling) - padding);
        Canvas.SetTop(_cursorFrame, rect.Y + (bounds.Y / scaling) - padding);
        _cursorFrame.Width = (bounds.Width / scaling) + (padding * 2);
        _cursorFrame.Height = (bounds.Height / scaling) + (padding * 2);
    }

    /// <summary>
    /// 遮罩上显示什么：<b>光标图像</b>由切换状态决定（默认不显示，按一下切换键才出来）；
    /// <b>虚线框</b>由设置决定（默认画）。两者互不影响，也可以只要光标不要框。
    /// </summary>
    private void ApplyCursorVisibility()
    {
        if (_cursor is null)
        {
            return;
        }

        _cursorImage.IsVisible = _cursorToggle;
        _cursorFrame.IsVisible = _cursorFrameVisible && _cursorFrameHasBounds;
    }

    private static Color ParseColor(string? value, Color fallback) =>
        Color.TryParse(value, out var color) ? color : fallback;

    private static KeyModifiers ParseModifier(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        "shift" => KeyModifiers.Shift,
        "ctrl" or "control" => KeyModifiers.Control,
        "alt" => KeyModifiers.Alt,
        _ => KeyModifiers.None,
    };

    /// <summary>
    /// 查询光标下的窗口与<b>元素链</b>，画黄色窗口框 + 橙色元素框（当前层级；元素与窗口同大、
    /// 或层级已到窗口层时只画窗口）。桌面、全屏窗口与过小的矩形不参与。
    /// 按 <see cref="ProbeIntervalMilliseconds"/> 节流。
    /// </summary>
    private void UpdateHighlight()
    {
        if (!_windowDetection)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastProbeAt < ProbeIntervalMilliseconds)
        {
            return;
        }

        var cursor = PlatformServices.ForegroundApp.GetCursorPosition();
        if (cursor is not { } location)
        {
            return;
        }

        // 光标几乎没动（< 2px）且上次已有窗口高亮：连 UIA 都不用问，直接沿用上次结果。
        // 遮罩上的鼠标抖动（画笔级别的 1px 抖动）非常常见，这一步能省掉绝大多数跨进程探测。
        if (_lastProbeAt != 0
            && _hoverWindow is not null
            && Math.Abs(location.X - _lastProbeX) < 2
            && Math.Abs(location.Y - _lastProbeY) < 2)
        {
            _lastProbeAt = now;
            return;
        }

        _lastProbeAt = now;
        _lastProbeX = location.X;
        _lastProbeY = location.Y;

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

        // 独占全屏目标：光标落在它的矩形里就只认它本身（不再看它下面被挡住的窗口 / 子元素）。
        if (ExclusiveTargetAt(location.X, location.Y) is { } exclusive)
        {
            ApplyWindow(exclusive, _exclusiveTarget);
            SetChain([]);
            return;
        }

        // 句柄与矩形必须来自**同一次**命中测试，后面所有判断也一律用这个句柄。
        // 旧代码先按坐标取矩形，再拿上一帧的 _hoverWindowHandle 去判"是不是桌面本体"：
        // 从桌面挪到普通窗口的那一帧会把窗口矩形覆盖成整屏工作区，而重算只由鼠标移动触发，
        // 停手不动时整屏框就一直挂着（用户报的"框包裹全屏常驻"）。
        var hovered = PlatformServices.ForegroundApp.GetWindowHandleAt(location.X, location.Y, handle);
        var window = Filter(PlatformServices.ForegroundApp.GetRectOf(hovered));

        // 桌面本体：平台层明确告知"这是桌面"，就按光标所在屏的工作区给框（副屏才正确，且不误伤普通窗口）。
        if (window is not null && PlatformServices.ForegroundApp.IsDesktopSurface(hovered)
            && Screens.ScreenFromPoint(new PixelPoint(location.X, location.Y)) is { } here)
        {
            window = new ScreenRect(
                here.WorkingArea.X, here.WorkingArea.Y,
                here.WorkingArea.Width, here.WorkingArea.Height);
        }

        // 探测不到任何窗口（空桌面：副屏上尤其常见，桌面壳可能不在枚举里）→ 按**当前屏工作区**给框。
        if (window is null && Screens.ScreenFromPoint(new PixelPoint(location.X, location.Y)) is { } empty)
        {
            window = new ScreenRect(
                empty.WorkingArea.X, empty.WorkingArea.Y,
                empty.WorkingArea.Width, empty.WorkingArea.Height);
        }

        // 遮罩映射日志（诊断副屏偏移用）：窗口物理位置/尺寸、底图尺寸、换算比例、最终窗口矩形。
        try
        {
            var logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "kscan");
            System.IO.Directory.CreateDirectory(logDir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(logDir, "probe.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + " mask pos=" + Position.X + "," + Position.Y
                + " size=" + Width + "x" + Height + " dip  desktop=" + _desktop.Width + "x" + _desktop.Height
                + " scale=" + DisplayScale().ToString("F3")
                + " window=" + (window is { } ww ? ww.X + "," + ww.Y + " " + ww.Width + "x" + ww.Height : "null")
                + Environment.NewLine);
        }
        catch (Exception)
        {
        }


        // 窗口框立刻更新：这一步只调两个 Win32 API（微秒级），不依赖下面可能很慢的 UIA。
        ApplyWindow(window, window is null ? IntPtr.Zero : hovered);

        // 元素识别（UIA）在这几种情况下**完全不做**：
        //   - 设置里没开子元素识别，或正按着"忽略子元素"的键；
        //   - 光标下是铺满整块屏的窗口（全屏游戏 / 播放器：本来没有可截的子控件，
        //     而它们的 UIA 查询会阻塞几百毫秒 —— 用户报的"Minecraft 后台时移动光标很卡"就是它）；
        //   - 本次框选里已经测到 UIA 太慢（见下面的 SlowProbeMilliseconds 退避）。
        if (!_elementDetection || _ignoreElement || window is not { } target || IsFullScreen(target) || _elementProbeTooSlow)
        {
            SetChain([]);
            return;
        }

        // 其余情况把 UIA 丢到后台线程：UIA 是跨进程调用，目标窗口忙的时候会把 UI 线程堵住，
        // 在那之前用户就已经感到"光标一顿一顿"。回来时用世代号丢弃过期结果。
        var generation = ++_probeGeneration;
        var x = location.X;
        var y = location.Y;

        _ = Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            IReadOnlyList<ScreenRect>? chain;

            try
            {
                chain = PlatformServices.ForegroundApp.GetElementChainAt(x, y, handle);
            }
            catch (Exception)
            {
                chain = null;
            }

            watch.Stop();

            if (watch.ElapsedMilliseconds >= SlowProbeMilliseconds)
            {
                // 这个前台窗口的 UIA 就是慢：这一次框选里别再试了，保住鼠标跟手
                _elementProbeTooSlow = true;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _probeGeneration || !IsVisible || _ignoreElement)
                {
                    return;   // 已经过期（光标又动了）或遮罩已经关了
                }

                SetChain(FilterChain(chain, target));
            });
        });
    }

    /// <summary>光标是不是落在"独占全屏目标窗口"里；是就返回它的矩形。</summary>
    private ScreenRect? ExclusiveTargetAt(int x, int y) =>
        _exclusiveTargetRect is { } rect && rect.Contains(x, y) ? rect : null;

    /// <summary>更新"光标下的窗口"与它对应的句柄；只有真的换了窗口才重画黄框。</summary>
    private void ApplyWindow(ScreenRect? window, IntPtr handle)
    {
        _hoverWindowHandle = handle;

        if (window == _hoverWindow)
        {
            return;
        }

        _hoverWindow = window;

        // 换了窗口就立刻清掉元素高亮：子元素识别是后台线程算的，光标快速挪走时
        // 旧结果会晚一点才回来，橙框就会"赖着不走"（用户报的现象）。
        _hoverElement = null;
        _elementChain = [];
        _elementHighlight.IsVisible = false;
        _probeGeneration++;   // 让在途的旧探测结果作废

        // 全屏窗口（游戏 / 播放器 / 无边框全屏）**不铺那层半透明填充**：
        // 那是一整块 1920×1080 的 alpha 混合，压在正在全屏渲染的游戏上，
        // 每帧都要多混一次全屏 —— 用户报的"光标一动就卡"就是这个（黄框本身照画，不影响选择）。
        _windowHighlight.Background = window is { } full && IsFullScreen(full) ? null : _windowHighlightFill;

        // 悬停高亮时把"选区框"关掉：之前拖过框再点桌面/窗口时，选区框会残留在屏幕上，
        // 看起来就是"两层遮罩、还有一个整屏框"（用户报的现象）。
        if (!_pressed && _selection.IsVisible)
        {
            _selection.IsVisible = false;
        }

        UpdateBorder(_windowHighlight, window);
    }

    /// <summary>换一条元素链（可能来自后台线程的结果），并按当前层级偏移刷新橙框。</summary>
    private void SetChain(IReadOnlyList<ScreenRect> chain)
    {
        var previous = _hoverElement;
        _elementChain = chain;

        // 鼠标移到别的层级深度时保留用户选的偏移，夹在链的范围内
        _elementLevelOffset = ElementChainLevels.Clamp(_elementChain, _elementLevelOffset);
        _hoverElement = ElementAtCurrentLevel();

        if (_hoverElement != previous)
        {
            UpdateBorder(_elementHighlight, _hoverElement);
        }
    }

    /// <summary>这个窗口是不是铺满了它所在的那块屏（全屏游戏 / 播放器 / 无边框全屏）。</summary>
    private bool IsFullScreen(ScreenRect rect)
    {
        var center = new PixelPoint(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
        if (Screens.ScreenFromPoint(center) is not { } screen)
        {
            return false;
        }

        return rect.Width >= screen.Bounds.Width - 2 && rect.Height >= screen.Bounds.Height - 2;
    }

    /// <summary>
    /// 过滤元素链：去掉过小 / 全屏的矩形，以及与窗口近似同大的项（那不算"更细的控件"）。
    /// </summary>
    private IReadOnlyList<ScreenRect> FilterChain(IReadOnlyList<ScreenRect>? chain, ScreenRect? window)
    {
        if (chain is null || chain.Count == 0)
        {
            return [];
        }

        var filtered = new List<ScreenRect>(chain.Count);
        foreach (var rect in chain)

        {
            // 元素框几乎盖满整个窗口时（桌面的 SysListView32 就是这种）与窗口框完全重叠：
            // 同时看到黄框+橙框、单击还会截整屏 —— 直接丢掉这个元素。
            if (window is { } owner
                && (long)rect.Width * rect.Height >= (long)owner.Width * owner.Height * 9 / 10)
            {
                continue;
            }
            if (Filter(rect) is null)
            {
                continue;
            }

            // 和窗口"差不多大"的元素不算更细的控件：UIA 给的窗口级元素矩形常常正好是
            // GetWindowRect（比可见边框大 7~8px），以前只容忍 3px，于是它会漏过过滤、
            // 用户看到的就只有"一个窗口"却被当成元素截图（屏幕上被挡住的部分就拍到遮挡窗口）。
            if (window is { } windowRect && NearlySameWindow(rect, windowRect))
            {
                continue;
            }

            filtered.Add(rect);
        }

        return filtered;
    }

    private void UpdateBorder(Border border, ScreenRect? rect)
    {
        if (rect is not { } value || _canvas.Bounds.Width <= 0 || _canvas.Bounds.Height <= 0)
        {
            border.IsVisible = false;
            return;
        }

        var canvasRect = ToCanvasRect(value);
        Canvas.SetLeft(border, canvasRect.X);
        Canvas.SetTop(border, canvasRect.Y);
        border.Width = canvasRect.Width;
        border.Height = canvasRect.Height;
        border.IsVisible = true;
    }

    /// <summary>
    /// 只丢掉"太小"的矩形。<b>不再丢弃"铺满整个屏幕"的矩形</b>：无边框全屏 / 浏览器 F11 / 全屏播放器
    /// 都是这种尺寸，丢掉它们就等于"这类窗口点不了、也截不到"（用户报的"无边框全屏不能被当做窗口截"）。
    /// 遮罩自己不会被命中（<c>WindowAtPoint</c> 传了 ignoreHandle），桌面的情况按普通窗口处理即可。
    /// </summary>
    private ScreenRect? Filter(ScreenRect? rect) =>
        rect is { } value && (value.Width < 8 || value.Height < 8) ? null : rect;

    /// <summary>
    /// 元素矩形是不是"就是那个窗口"：位置 / 尺寸差在 <paramref name="tolerance"/> 以内就算。
    /// 容差按窗口小边取 2%（至少 4px、至多 16px）—— 正好覆盖 Vista 之后那圈 7~8px 的透明拖拽边框，
    /// 以及 UIA 与 DWM 之间的取整差异。
    /// </summary>
    internal static bool NearlySameWindow(ScreenRect element, ScreenRect window)
    {
        var tolerance = Math.Clamp((int)Math.Round(Math.Min(window.Width, window.Height) * 0.02), 4, 16);

        // 按"每一边"比较：透明边框是左 / 右 / 下各多 7~8px，用宽高去比会变成 14~16px，容易超容差
        var left = Math.Abs(element.X - window.X);
        var top = Math.Abs(element.Y - window.Y);
        var right = Math.Abs(element.X + element.Width - (window.X + window.Width));
        var bottom = Math.Abs(element.Y + element.Height - (window.Y + window.Height));

        return left <= tolerance && top <= tolerance && right <= tolerance && bottom <= tolerance;
    }

    private static bool NearlySame(ScreenRect a, ScreenRect b) =>
        Math.Abs(a.X - b.X) <= 3
        && Math.Abs(a.Y - b.Y) <= 3
        && Math.Abs(a.Width - b.Width) <= 3
        && Math.Abs(a.Height - b.Height) <= 3;

    private void ClearHighlight()
    {
        _hoverWindow = null;
        _hoverElement = null;
        _windowHighlight.IsVisible = false;
        _elementHighlight.IsVisible = false;
    }


    /// <summary>屏幕物理像素矩形 → 画布 DIP 矩形（与 Image 的 Stretch=Uniform 换算一致）。</summary>
    private Rect ToCanvasRect(ScreenRect rect)
    {
        var scale = DisplayScale();
        var offsetX = (_canvas.Bounds.Width - _desktop.Width * scale) / 2;
        var offsetY = (_canvas.Bounds.Height - _desktop.Height * scale) / 2;

        return new Rect(
            (rect.X - Position.X) * scale + offsetX,
            (rect.Y - Position.Y) * scale + offsetY,
            rect.Width * scale,
            rect.Height * scale);
    }

    private double DisplayScale()
    {
        var scale = Math.Min(_canvas.Bounds.Width / _desktop.Width, _canvas.Bounds.Height / _desktop.Height);
        return scale <= 0 ? 1 : scale;
    }

    /// <summary>把画布上的矩形换算成"抓屏图上的像素矩形"（与 Image 的 Stretch=Uniform 一致：等比缩放后居中）。</summary>
    private (int X, int Y, int Width, int Height) ToImagePixels(Rect selection)
    {
        var scale = DisplayScale();
        var displayedWidth = _desktop.Width * scale;
        var displayedHeight = _desktop.Height * scale;
        var offsetX = (_canvas.Bounds.Width - displayedWidth) / 2;
        var offsetY = (_canvas.Bounds.Height - displayedHeight) / 2;

        return (
            (int)Math.Round((selection.X - offsetX) / scale),
            (int)Math.Round((selection.Y - offsetY) / scale),
            (int)Math.Round(selection.Width / scale),
            (int)Math.Round(selection.Height / scale));
    }

    /// <summary>
    /// 选区在<b>屏幕物理像素</b>里的位置：窗口就铺在目标屏幕上，它的 <c>Position</c> 正是那块屏幕的原点，
    /// 再加上抓屏图内的偏移就是屏幕坐标。留着给"贴回原位"用。
    /// </summary>
    private PixelRect ScreenRectOf((int X, int Y, int Width, int Height) pixels) =>
        new(Position.X + pixels.X, Position.Y + pixels.Y, pixels.Width, pixels.Height);

    private static Rect Normalize(Point a, Point b) => new(
        Math.Min(a.X, b.X),
        Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X),
        Math.Abs(a.Y - b.Y));

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
