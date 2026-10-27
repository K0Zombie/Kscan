using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Kscan.App.Services;
using Kscan.Core.Configuration;
using Kscan.Core.Imaging;
using Kscan.Platform;

namespace Kscan.App.Views;

/// <summary>一块显示器的信息（物理矩形 + 缩放）。</summary>
internal readonly record struct ScreenInfo(PixelRect Bounds, double Scaling);

/// <summary>选区的四条边之一。</summary>
internal enum SelectionEdge
{
    Top,
    Bottom,
    Left,
    Right,
}

/// <summary>
/// 选区边框的判定：这块屏上该不该画某条边。
/// <para>
/// 规则只有一句：<b>这条边是选区的真实边</b>（不是被屏幕边界裁断产生的假边）才画。
/// 这样跨屏选区在两屏接缝处不会画出内部竖线，跨屏窗口也一样。
/// </para>
/// <para>
/// 容差 <see cref="Tolerance"/>：混合 DPI 下屏边界可能差 1~2 像素（2559 vs 2560），
/// 严格相等会让真实边被判成假边 —— 边框会"缺一段"。
/// </para>
/// </summary>
internal static class SelectionEdgeRules
{
    public const int Tolerance = 2;

    public static bool ShouldDraw(PixelRect selection, PixelRect screen, SelectionEdge edge, int tolerance = Tolerance) =>
        edge switch
        {
            // 选区上边在这块屏的上边界上或之内（= 不是被上方裁断的）→ 画
            SelectionEdge.Top => selection.Y >= screen.Y - tolerance,
            // 下边在这块屏的下边界上或之内（= 不是被下方裁断的）→ 画
            SelectionEdge.Bottom => selection.Y + selection.Height <= screen.Y + screen.Height + tolerance,
            SelectionEdge.Left => selection.X >= screen.X - tolerance,
            SelectionEdge.Right => selection.X + selection.Width <= screen.X + screen.Width + tolerance,
            _ => true,
        };
}

/// <summary>
/// 跨屏框选：<b>每块显示器一个遮罩窗口</b>，每个窗口只显示拼合图里属于自己那块屏的切片。
/// <para>
/// 为什么不是一个大窗口盖住所有屏：显示器可能缩放不同（100% / 150%），一个窗口只有一种缩放，
/// 铺到别的屏上尺寸就错了。一块屏一个窗口、各自按自己的缩放渲染，才能做到"每屏物理像素 1:1"。
/// </para>
/// <para>
/// 窗口位置与尺寸只用平台 API（<c>SetWindowPos</c>）按物理像素设置，不再用 Avalonia 的
/// <c>Width</c>/<c>Height</c> 覆盖 —— 那样会把窗口放大（副屏被放大、任务栏跑出屏外）。
/// </para>
/// </summary>
internal static class MultiScreenSelector
{
    /// <summary>弹出跨屏选区；用户取消（Esc / 右键 / 空选区）返回 <c>null</c>。</summary>
    public static Task<Services.RegionSelection?> SelectAsync(
        ImageFrame desktop,
        PixelRect virtualRect,
        IReadOnlyList<ScreenInfo> screens)
    {
        if (screens.Count == 0)
        {
            return Task.FromResult<Services.RegionSelection?>(null);
        }

        var session = new MultiScreenSession(desktop, virtualRect, screens);

        foreach (var screen in screens)
        {
            session.Add(new ShadeWindow(session, screen));
        }

        session.ShowAll();
        return session.Result;
    }
}

/// <summary>跨屏选区的一次会话：持有拼合图、各窗口、当前选区与结果缩略图。</summary>
internal sealed class MultiScreenSession
{
    private const long PreviewIntervalMs = 80;
    private const int PreviewMaxWidth = 260;
    private const int PreviewMaxHeight = 170;

    /// <summary>悬停窗口识别的节流间隔（毫秒）。</summary>
    private const int HoverIntervalMs = 40;

    private readonly List<ShadeWindow> _windows = [];
    private readonly TaskCompletionSource<Services.RegionSelection?> _completion = new();

    /// <summary>是否做"光标下的窗口识别"（设置项，默认开）。关掉就只有手动拖框。</summary>
    private readonly bool _windowDetection;

    private Kscan.Platform.Abstractions.ScreenPoint _start;
    private bool _dragging;
    private bool _dragged;
    private bool _done;
    private long _lastPreviewTicks;
    private long _lastHoverTicks;
    private int _lastHoverX;
    private int _lastHoverY;

    public MultiScreenSession(ImageFrame desktop, PixelRect virtualRect, IReadOnlyList<ScreenInfo> screens)
    {
        Desktop = desktop;
        VirtualRect = virtualRect;
        Screens = screens;

        _windowDetection = AppSettingsService.Current.Settings.CaptureAcrossScreensWindowDetection
            && PlatformServices.ForegroundApp.IsSupported;
    }

    public ImageFrame Desktop { get; }

    /// <summary>虚拟桌面在屏幕坐标里的矩形（物理像素）。</summary>
    public PixelRect VirtualRect { get; }

    /// <summary>参与拼接的显示器（用于填充空档）。</summary>
    public IReadOnlyList<ScreenInfo> Screens { get; }

    /// <summary>当前选区（虚拟桌面物理坐标）；<c>null</c> = 还没开始拖。</summary>
    public PixelRect? Selection { get; private set; }

    /// <summary>光标下识别到的窗口（虚拟桌面物理坐标，已夹进虚拟桌面）；<c>null</c> = 空白处/没开识别。</summary>
    public PixelRect? HoverWindow => _hoverTarget?.Rect;

    /// <summary>光标下识别到的窗口的完整信息（矩形 / 句柄 / 遮罩句柄 / 未裁剪矩形）。</summary>
    private HoverTarget? _hoverTarget;

    /// <summary>光标下窗口的完整信息。<paramref name="Rect"/> 是夹进虚拟桌面之后的；<paramref name="FullRect"/> 是原始的。</summary>
    private readonly record struct HoverTarget(PixelRect Rect, PixelRect FullRect, IntPtr Handle, IntPtr Overlay);

    /// <summary>窗口识别是否启用（设置项 + 平台支持）。</summary>
    public bool WindowDetectionEnabled => _windowDetection;

    /// <summary>是否正在拖框（用来区分"挑窗口"和"手动框"）。</summary>
    public bool IsDragging => _dragging;

    /// <summary>选区结果的缩略图（每块屏正中显示一张）。</summary>
    public Bitmap? Preview { get; private set; }

    public Task<Services.RegionSelection?> Result => _completion.Task;

    public void Add(ShadeWindow window) => _windows.Add(window);

    public void ShowAll()
    {
        foreach (var window in _windows)
        {
            window.Show();
        }

        // Show 之后再摆一次：此时窗口已挂到目标显示器，Avalonia 会用该屏缩放换算物理尺寸。
        foreach (var window in _windows)
        {
            window.Place();
            window.RefreshMask();
        }

        _windows[0].Activate();

        // 打开就按光标位置给一个框：光标在窗口上 → 框那个窗口；在空白桌面 → 框整个虚拟桌面。
        // 这样"什么都不拖，直接单击"就是截光标下的窗口或整个桌面。
        UpdateHover(IntPtr.Zero, force: true);
    }

    public void BeginDrag()
    {
        if (PlatformServices.ForegroundApp.GetCursorPosition() is not { } cursor)
        {
            return;
        }

        _dragging = true;
        _dragged = false;
        _start = cursor;

        // 按下时不清选框：保持悬停框（光标在窗口上/空白处），松手没拖动就截这个框。
        if (!_windowDetection)
        {
            Selection = null;
        }

        // 遮罩打开期间用户可能改了缩放/分辨率或插拔了显示器：那样窗口几何已经过期。
        // 检测到不一致就取消本次，让用户下次按热键重新按新布局建窗口（比画错更安全）。
        if (ScreensChangedSinceOpen())
        {
            GuiLog.Write("multiscreen: screen layout changed while overlay was open, cancelling");
            Cancel();
            return;
        }

        // 诊断：一次性记录坐标系（屏幕矩形 / 虚拟桌面 / 拼合图尺寸 / 起始光标）
        GuiLog.Write(
            $"multiscreen: begin cursor=({cursor.X},{cursor.Y}) virtual=({VirtualRect.X},{VirtualRect.Y},{VirtualRect.Width},{VirtualRect.Height}) desktop={Desktop.Width}x{Desktop.Height} screens=" +
            string.Join("; ", Screens.Select(screen =>
                $"({screen.Bounds.X},{screen.Bounds.Y},{screen.Bounds.Width},{screen.Bounds.Height})@{screen.Scaling:0.##}")));

        foreach (var window in _windows)
        {
            window.ResetDiagnostics();
        }
    }

    public void UpdateDrag()
    {
        if (!_dragging || PlatformServices.ForegroundApp.GetCursorPosition() is not { } cursor)
        {
            return;
        }

        // 3px 内不算拖：保持悬停框（单击截窗口/整桌面就靠这条）
        if (Math.Abs(cursor.X - _start.X) < 3 && Math.Abs(cursor.Y - _start.Y) < 3)
        {
            return;
        }

        _dragged = true;

        var left = Math.Min(_start.X, cursor.X);
        var top = Math.Min(_start.Y, cursor.Y);

        Selection = new PixelRect(
            left,
            top,
            Math.Max(1, Math.Abs(cursor.X - _start.X)),
            Math.Max(1, Math.Abs(cursor.Y - _start.Y)));

        RefreshAll();
    }

    /// <summary>
    /// 光标移动（未拖动）时识别它下面的窗口：有窗口就框那个窗口，空白处就框整个虚拟桌面。
    /// <para>
    /// 40ms 节流 + 光标没动就跳过：命中测试本身很便宜（<c>WindowFromPoint</c>），
    /// 但没有必要时不必每像素都算一遍；<b>不做元素识别</b>（UIA 才是慢的那部分）。
    /// </para>
    /// </summary>
    public void UpdateHover(IntPtr overlayHandle, bool force = false)
    {
        if (_done || _dragging || !_windowDetection)
        {
            return;
        }

        if (PlatformServices.ForegroundApp.GetCursorPosition() is not { } cursor)
        {
            return;
        }

        // 调用方没给遮罩句柄（例如 ShowAll 时）：按光标位置自己找那块屏的遮罩，
        // 否则命中测试会命中我们自己的遮罩，把"遮罩"当成用户窗口框出来。
        if (overlayHandle == IntPtr.Zero)
        {
            overlayHandle = OverlayAt(cursor.X, cursor.Y);
        }

        var now = Environment.TickCount64;

        if (!force)
        {
            if (now - _lastHoverTicks < HoverIntervalMs)
            {
                return;
            }

            // 光标几乎没动、且已经框着东西：直接沿用（遮罩上的手抖不该让框闪）
            if (HoverWindow is not null
                && Math.Abs(cursor.X - _lastHoverX) < 2
                && Math.Abs(cursor.Y - _lastHoverY) < 2)
            {
                _lastHoverTicks = now;
                return;
            }
        }

        _lastHoverTicks = now;
        _lastHoverX = cursor.X;
        _lastHoverY = cursor.Y;

        var next = DetectWindowAt(cursor, overlayHandle);
        var nextSelection = next?.Rect ?? VirtualRect;

        // 框没变就别重画（空白处移动鼠标时框一直是整个桌面，重画只会白烧 CPU）
        if (next?.Rect == HoverWindow && Selection == nextSelection)
        {
            _hoverTarget = next;
            return;
        }

        if (next?.Rect != HoverWindow)
        {
            // 只在变化时记一行：用户报"框不对"时，看日志就知道当时认到了什么
            GuiLog.Write(next is { } window
                ? $"multiscreen: hover window=({window.Rect.X},{window.Rect.Y},{window.Rect.Width},{window.Rect.Height})"
                : "multiscreen: hover blank → whole virtual desktop");
        }

        _hoverTarget = next;

        // 光标在空白处（没有窗口）→ 框整个虚拟桌面
        Selection = nextSelection;

        RefreshAll();
    }

    /// <summary>光标下最上层的窗口（矩形夹进虚拟桌面）；命中桌面/任务栏/自己的遮罩 → <c>null</c>。</summary>
    private HoverTarget? DetectWindowAt(Kscan.Platform.Abstractions.ScreenPoint cursor, IntPtr overlayHandle)
    {
        var foreground = PlatformServices.ForegroundApp;

        var handle = foreground.GetWindowHandleAt(cursor.X, cursor.Y, overlayHandle);

        // 命中到自己某一块遮罩（多屏时有多个句柄，只忽略一个不够）→ 当作空白
        if (handle == IntPtr.Zero || _windows.Any(window => window.OverlayHandle == handle))
        {
            return null;
        }

        if (foreground.IsDesktopSurface(handle) || foreground.IsTaskbar(handle))
        {
            return null;
        }

        if (foreground.GetRectOf(handle) is not { } rect || rect.Width < 16 || rect.Height < 16)
        {
            return null;
        }

        // 夹进虚拟桌面：窗口可能有一半在屏幕外
        var left = Math.Max(rect.X, VirtualRect.X);
        var top = Math.Max(rect.Y, VirtualRect.Y);
        var right = Math.Min(rect.X + rect.Width, VirtualRect.X + VirtualRect.Width);
        var bottom = Math.Min(rect.Y + rect.Height, VirtualRect.Y + VirtualRect.Height);

        return right - left >= 16 && bottom - top >= 16
            ? new HoverTarget(
                new PixelRect(left, top, right - left, bottom - top),
                new PixelRect(rect.X, rect.Y, rect.Width, rect.Height),
                handle,
                overlayHandle)
            : null;
    }

    /// <summary>刷新缩略图（节流）并重画所有遮罩窗口。</summary>
    private void RefreshAll()
    {
        var now = Environment.TickCount64;

        if (now - _lastPreviewTicks >= PreviewIntervalMs)
        {
            _lastPreviewTicks = now;
            UpdatePreview();
        }

        foreach (var window in _windows)
        {
            window.RefreshMask();
        }
    }

    /// <summary>光标位置落在哪块屏的遮罩上；找不到返回 <see cref="IntPtr.Zero"/>。</summary>
    private IntPtr OverlayAt(int x, int y) =>
        _windows.FirstOrDefault(window => window.ScreenContains(x, y))?.OverlayHandle ?? IntPtr.Zero;

    /// <summary>
    /// 整窗离屏渲染（<c>PrintWindow</c>）：拿被遮挡 / 超出屏幕那部分内容。
    /// <para>
    /// 开关沿用设置里单屏那两个：<c>captureWholeWindowOccluded</c>（被遮挡时）与
    /// <c>captureWholeWindowOffScreen</c>（超出屏幕时）。都关掉就退回"截屏幕上看得见的样子"。
    /// </para>
    /// </summary>
    private bool TryCaptureWholeWindow(HoverTarget target, out ImageFrame frame, out PixelRect coverage)
    {
        frame = null!;
        coverage = default;

        var settings = AppSettingsService.Current.Settings;

        var offScreen = target.FullRect != target.Rect;

        var allowed = offScreen
            ? settings.CaptureWholeWindowOffScreen
            : settings.CaptureWholeWindowOccluded
              && PlatformServices.ForegroundApp.IsWindowCovered(target.Handle, target.Overlay);

        if (!allowed || !PlatformServices.WindowCapture.IsSupported)
        {
            return false;
        }

        var captured = PlatformServices.WindowCapture.CaptureWhole(target.Handle);

        if (captured is not { } result)
        {
            return false;
        }

        if (result.Image.Width < 4
            || result.Image.Height < 4
            || RegionSelectorWindow.IsNearlyBlank(result.Image))
        {
            return false;
        }

        frame = result.Image;

        // 平台给的 coverage 是"这张图对应屏幕上哪一块"；拿不到就退回窗口矩形
        coverage = result.Coverage.Width > 0 && result.Coverage.Height > 0
            ? new PixelRect(result.Coverage.X, result.Coverage.Y, result.Coverage.Width, result.Coverage.Height)
            : target.Rect;

        GuiLog.Write(
            $"multiscreen: whole-window {(offScreen ? "off-screen" : "occluded")} " +
            $"handle=0x{target.Handle:X} image={frame.Width}x{frame.Height} coverage=({coverage.X},{coverage.Y},{coverage.Width},{coverage.Height})");

        return true;
    }

    /// <summary>最近邻降采样出"最终结果"的缩略图（代价只与缩略图尺寸有关）。</summary>
    private void UpdatePreview()
    {
        if (Selection is not { } selection || Desktop.Width <= 0 || Desktop.Height <= 0)
        {
            return;
        }

        var x = selection.X - VirtualRect.X;
        var y = selection.Y - VirtualRect.Y;
        var width = Math.Min(selection.Width, Desktop.Width - Math.Max(0, x));
        var height = Math.Min(selection.Height, Desktop.Height - Math.Max(0, y));

        if (x < 0 || y < 0 || width <= 0 || height <= 0)
        {
            return;
        }

        var scale = Math.Min(1.0, Math.Min((double)PreviewMaxWidth / width, (double)PreviewMaxHeight / height));
        var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(height * scale));

        var source = Desktop.Pixels;
        var stride = Desktop.Stride;
        var bytesPerPixel = ImageFrame.BytesPerPixel;

        var scaled = new byte[targetWidth * targetHeight * bytesPerPixel];

        for (var row = 0; row < targetHeight; row++)
        {
            var sourceRow = y + (row * height / targetHeight);

            for (var column = 0; column < targetWidth; column++)
            {
                var sourceColumn = x + (column * width / targetWidth);
                var sourceOffset = (sourceRow * stride) + (sourceColumn * bytesPerPixel);
                var targetOffset = ((row * targetWidth) + column) * bytesPerPixel;

                scaled[targetOffset] = source[sourceOffset];
                scaled[targetOffset + 1] = source[sourceOffset + 1];
                scaled[targetOffset + 2] = source[sourceOffset + 2];
                scaled[targetOffset + 3] = source[sourceOffset + 3];
            }
        }

        var previous = Preview;
        Preview = ResultActions.ToBitmap(new ImageFrame(targetWidth, targetHeight, scaled));
        previous?.Dispose();
    }

    /// <summary>
    /// 打开遮罩之后显示器布局是否变了（缩放、分辨率、数量）。
    /// 变了就不能沿用旧的窗口几何，否则压暗与选框都会错位。
    /// </summary>
    private bool ScreensChangedSinceOpen()
    {
        var screens = Services.TopLevelAccessor.GetScreens();

        if (screens is null)
        {
            return false;
        }

        var current = screens.All.ToList();

        if (current.Count != Screens.Count)
        {
            return true;
        }

        for (var index = 0; index < current.Count; index++)
        {
            var a = current[index];
            var b = Screens[index];
            var bounds = a.Bounds;

            if (bounds != b.Bounds || Math.Abs(a.Scaling - b.Scaling) > 0.001)
            {
                return true;
            }
        }

        return false;
    }

    public void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;

        if (Selection is not { } selection || selection.Width < 3 || selection.Height < 3)
        {
            Cancel();
            return;
        }

        // 单击（没拖动）停在某个窗口上：和单屏一样，被挡住 / 超出屏幕时用 PrintWindow 拿**完整窗口**。
        // 不这样做的话跨屏这边只能截"屏幕上露出来的那一块"，表现就是"按住 Ctrl 也没法防遮挡"。
        if (!_dragged && _hoverTarget is { } target && selection == target.Rect
            && TryCaptureWholeWindow(target, out var whole, out var coverage))
        {
            _done = true;
            _completion.TrySetResult(new Services.RegionSelection(whole, coverage));
            DisposeAll();
            return;
        }

        var x = selection.X - VirtualRect.X;
        var y = selection.Y - VirtualRect.Y;
        var width = Math.Min(selection.Width, Desktop.Width - Math.Max(0, x));
        var height = Math.Min(selection.Height, Desktop.Height - Math.Max(0, y));

        if (x < 0 || y < 0 || width <= 0 || height <= 0)
        {
            Cancel();
            return;
        }

        _done = true;

        var cropped = Desktop.Crop(x, y, width, height);

        // 显示器之间的空档在系统截图里是黑色：按设置填成透明或黑色
        FrameMask.ClearOutside(
            cropped,
            selection.X,
            selection.Y,
            Screens
                .Select(screen => new KeepRect(
                    screen.Bounds.X,
                    screen.Bounds.Y,
                    screen.Bounds.Width,
                    screen.Bounds.Height))
                .ToList(),
            transparent: !GapFill.IsBlack(AppSettingsService.Current.Settings.MultiScreenGapFill));

        _completion.TrySetResult(new Services.RegionSelection(cropped, selection));
        DisposeAll();
    }

    public void Cancel()
    {
        if (_done)
        {
            return;
        }

        _done = true;
        _completion.TrySetResult(null);
        DisposeAll();
    }

    private void DisposeAll()
    {
        Preview?.Dispose();
        Preview = null;

        foreach (var window in _windows.ToList())
        {
            try
            {
                window.Close();
            }
            catch (Exception)
            {
                // 已经关掉的忽略
            }
        }

        _windows.Clear();
    }
}

/// <summary>
/// 一块显示器上的遮罩窗口：显示这一屏的切片 + 四周压暗 + 选区边框 + 并集外轮廓 + 结果缩略图。
/// </summary>
internal sealed class ShadeWindow : Window
{
    private const double OutlineThickness = 2;

    /// <summary>选区边线粗细（DIP）；高 DPI 屏上看起来会细一点，但不会糊。</summary>
    private const double SelectionLineThickness = 2;

    private readonly MultiScreenSession _session;
    private readonly ScreenInfo _screen;

    private readonly Image _slice = new() { Stretch = Stretch.Fill };

    private readonly Border _dimTop = Dim();
    private readonly Border _dimBottom = Dim();
    private readonly Border _dimLeft = Dim();
    private readonly Border _dimRight = Dim();

    private readonly Border _outlineTop = Outline();
    private readonly Border _outlineBottom = Outline();
    private readonly Border _outlineLeft = Outline();
    private readonly Border _outlineRight = Outline();

    /// <summary>
    /// 选区的四条边<b>分开画</b>（不是一个四边框）。
    /// <para>
    /// 原因：一块屏只画选区落在自己这块屏里的那部分，用四边框等于在"被屏幕边界裁断的地方"
    /// 也画线 —— 两块屏的拼接缝上就多出一条竖线（用户报的"两个屏连接的地方不画"）。
    /// 拆成四条线之后，只有"选区的真实边"落在这块屏上时才画，被裁断的那条不画。
    /// </para>
    /// </summary>
    private readonly Border _selTop = SelLine();
    private readonly Border _selBottom = SelLine();
    private readonly Border _selLeft = SelLine();
    private readonly Border _selRight = SelLine();

    /// <summary>悬停框住的是"某个窗口"时，给这块区域加一层很淡的黄色底色（整桌面不铺）。</summary>
    private readonly Border _windowTint = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(20, 0xFF, 0xCC, 0x00)),
        IsVisible = false,
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private readonly Border _previewPanel = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Avalonia.Media.Colors.White, 0.65),
        Background = new SolidColorBrush(Avalonia.Media.Colors.Black, 0.35),
        Padding = new Thickness(4),
        CornerRadius = new CornerRadius(4),
        // 半透明：既看得清缩略图，也能看到下面的桌面
        Opacity = 0.75,
        IsVisible = false,
        IsHitTestVisible = false,
    };

    private readonly Image _previewImage = new() { Stretch = Stretch.Uniform };

    private readonly TextBlock _previewLabel = new()
    {
        FontSize = 12,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 2, 0, 0),
    };

    private Bitmap? _sliceBitmap;

    /// <summary>本轮拖动是否已经打过诊断日志（每次拖动每块屏只打一次）。</summary>
    private bool _diagnosed;

    /// <summary>开始新一轮拖动：允许再打一次诊断。</summary>
    public void ResetDiagnostics() => _diagnosed = false;

    public ShadeWindow(MultiScreenSession session, ScreenInfo screen)
    {
        _session = session;
        _screen = screen;

        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Focusable = true;

        // 底色用主题的遮罩色，和单屏框选一致
        Background = ThemeService.Current.MaskBrush;

        _sliceBitmap = Slice(session.Desktop, session.VirtualRect, screen.Bounds);
        _slice.Source = _sliceBitmap;

        _previewPanel.Child = new StackPanel { Children = { _previewImage, _previewLabel } };

        Content = new Grid
        {
            Children =
            {
                _slice,
                _dimTop, _dimBottom, _dimLeft, _dimRight,
                _windowTint,
                _outlineTop, _outlineBottom, _outlineLeft, _outlineRight,
                _selTop, _selBottom, _selLeft, _selRight,
                _previewPanel,
            },
        };

        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;

        // 首次 RefreshMask 时窗口往往还没完成布局（Bounds=0），压暗/选框的尺寸就全是 0，
        // 表现为"某块屏根本没被压暗"或"框线错位"。尺寸一变就重算一次。
        SizeChanged += (_, _) => RefreshMask();
        Opened += (_, _) => RefreshMask();

        Closed += (_, _) =>
        {
            _slice.Source = null;
            _sliceBitmap?.Dispose();
            _sliceBitmap = null;
        };
    }

    /// <summary>
    /// 把窗口精确摆到这块显示器上：<b>只用 Avalonia 的 DIP 尺寸 + 物理 Position</b>。
    /// <para>
    /// 不要再调平台 <c>SetBounds</c>：两套设定会打架。实测副屏 125% 时，平台把窗口设成物理
    /// 2560×1440，Avalonia 又把这 2560 当 DIP 再放大 1.25 倍 → 窗口实际 3200×1800（比屏幕还大），
    /// 内容被放大、选框不跟鼠标。只留 DIP 一套，物理尺寸由 Avalonia 自己按该屏缩放换算。
    /// </para>
    /// </summary>
    public void Place()
    {
        var scaling = _screen.Scaling > 0 ? _screen.Scaling : 1.0;
        var bounds = _screen.Bounds;

        SizeToContent = SizeToContent.Manual;

        Position = new PixelPoint(bounds.X, bounds.Y);
        Width = bounds.Width / scaling;
        Height = bounds.Height / scaling;
    }

    /// <summary>
    /// 把 Avalonia 侧的 DIP 尺寸对齐到"平台尺寸"。
    /// <para>
    /// 缩放取 <see cref="ScreenInfo.Scaling"/>（来自 <c>Screens</c>，是这块屏的权威值），
    /// <b>不能</b>用窗口自己的 <see cref="Window.RenderScaling"/> ——
    /// 刚 Show 出来时窗口可能还没挂到目标屏上，读到的是主屏缩放，算出来的 DIP 尺寸于是错掉
    /// （表现就是画面被放大/缩小，选框也因此不跟手）。
    /// </para>
    /// </summary>
    private static Border Dim() => new()
    {
        Background = new SolidColorBrush(Avalonia.Media.Colors.Black, 0.45),
        IsHitTestVisible = false,
        IsVisible = false,
        // 关键：显式设 Width/Height 时，默认 Stretch 会被当成 Center，Margin 就不生效
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private static Border Outline() => new()
    {
        Background = new SolidColorBrush(Avalonia.Media.Colors.White, 0.55),
        IsHitTestVisible = false,
        IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    /// <summary>选区的边线（2px 实色，逐边摆放）。</summary>
    private static Border SelLine() => new()
    {
        Background = Brushes.DeepSkyBlue,
        IsHitTestVisible = false,
        IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private static Bitmap? Slice(ImageFrame desktop, PixelRect virtualRect, PixelRect screen)
    {
        var x = screen.X - virtualRect.X;
        var y = screen.Y - virtualRect.Y;

        if (x < 0 || y < 0 || x >= desktop.Width || y >= desktop.Height)
        {
            GuiLog.Write(
                $"multiscreen: slice out of range screen={screen} desktop={desktop.Width}x{desktop.Height} origin={virtualRect}");
            return null;
        }

        var width = Math.Min(screen.Width, desktop.Width - x);
        var height = Math.Min(screen.Height, desktop.Height - y);

        GuiLog.Write(
            $"multiscreen: slice screen=({screen.X},{screen.Y},{screen.Width},{screen.Height}) crop=({x},{y},{width},{height}) desktop={desktop.Width}x{desktop.Height} origin=({virtualRect.X},{virtualRect.Y})");

        return width <= 0 || height <= 0
            ? null
            : ResultActions.ToBitmap(desktop.Crop(x, y, width, height));
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            _session.Cancel();
            return;
        }

        Focus();

        // 按下前再取一次光标下的窗口：鼠标停在原地直接点（没有 move 事件）也要框对
        if (_session.WindowDetectionEnabled)
        {
            _session.UpdateHover(HandleForOverlay(), force: true);
        }

        _session.BeginDrag();
        e.Pointer.Capture(this);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        // 没拖动时是在"挑窗口"：把光标下的窗口框出来（空白处 = 整个虚拟桌面）
        if (_session.IsDragging)
        {
            _session.UpdateDrag();
            return;
        }

        _session.UpdateHover(HandleForOverlay());
    }

    /// <summary>本遮罩窗口的原生句柄（命中测试时要跳过它，否则永远命中遮罩自己）。</summary>
    private IntPtr HandleForOverlay() => OverlayHandle;

    /// <summary>本遮罩窗口的原生句柄。</summary>
    internal IntPtr OverlayHandle => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    /// <summary>这个物理坐标点是不是落在这块屏上。</summary>
    internal bool ScreenContains(int x, int y) =>
        x >= _screen.Bounds.X
        && x < _screen.Bounds.X + _screen.Bounds.Width
        && y >= _screen.Bounds.Y
        && y < _screen.Bounds.Y + _screen.Bounds.Height;

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        _session.EndDrag();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _session.Cancel();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>按会话里的选区重画压暗、选框、外轮廓与结果缩略图。</summary>
    public void RefreshMask()
    {
        // 布局用 **DIP 尺寸（Bounds）**，不要用 ClientSize：
        // 实测在 125% 的屏上，ClientSize 报的是物理像素（2560x1440），
        // 而 Margin/Width 这些属性是按 DIP 解释的 —— 拿物理值直接设会让整块绘制放大 1.25 倍，
        // 表现就是"选框不跟鼠标、框乱画"。副屏 125% → 这里应是 2048x1152。
        var viewWidth = Bounds.Width > 0 ? Bounds.Width : ClientSize.Width;
        var viewHeight = Bounds.Height > 0 ? Bounds.Height : ClientSize.Height;

        RefreshOutline(viewWidth, viewHeight);
        RefreshPreview();

        var screen = _screen.Bounds;

        if (_session.Selection is not { } selection)
        {
            // 还没开始拖：整块压暗
            HideSelection();
            SetDim(_dimTop, 0, 0, viewWidth, viewHeight);
            _dimTop.IsVisible = true;
            _dimBottom.IsVisible = _dimLeft.IsVisible = _dimRight.IsVisible = false;
            return;
        }

        var left = Math.Max(selection.X, screen.X);
        var top = Math.Max(selection.Y, screen.Y);
        var right = Math.Min(selection.X + selection.Width, screen.X + screen.Width);
        var bottom = Math.Min(selection.Y + selection.Height, screen.Y + screen.Height);

        // 物理 → 本窗口逻辑坐标：**不要用 RenderScaling**。
        // 用"实际客户端尺寸 / 该屏物理尺寸"得到的比例，才是这一块窗口真实的铺放比例；
        // 混合 DPI 下 RenderScaling 可能滞后或与屏幕实际缩放不一致，那会让选框和鼠标错位。
        var scaleX = screen.Width > 0 ? viewWidth / screen.Width : 1.0;
        var scaleY = screen.Height > 0 ? viewHeight / screen.Height : 1.0;

        if (scaleX <= 0 || scaleY <= 0)
        {
            scaleX = scaleY = 1.0;
        }

        double LocalX(double value) => (value - screen.X) * scaleX;
        double LocalY(double value) => (value - screen.Y) * scaleY;

        if (right <= left || bottom <= top)
        {
            // 选区完全不在这块屏：整块压暗
            HideSelection();
            SetDim(_dimTop, 0, 0, viewWidth, viewHeight);
            _dimTop.IsVisible = true;
            _dimBottom.IsVisible = _dimLeft.IsVisible = _dimRight.IsVisible = false;
            return;
        }

        var l = LocalX(left);
        var t = LocalY(top);
        var r = LocalX(right);
        var b = LocalY(bottom);

        // 诊断：每次拖动每块屏只记一次，用来核对"物理→本地"的换算是否成立
        if (!_diagnosed)
        {
            _diagnosed = true;
            GuiLog.Write(
                $"multiscreen: shade screen=({screen.X},{screen.Y},{screen.Width},{screen.Height}) dip={viewWidth:0.#}x{viewHeight:0.#} client={ClientSize.Width:0.#}x{ClientSize.Height:0.#} renderScaling={RenderScaling:0.##} scale=({scaleX:0.####},{scaleY:0.####}) " +
                $"sel=({selection.X},{selection.Y},{selection.Width},{selection.Height}) local=({l:0.#},{t:0.#},{r - l:0.#},{b - t:0.#})");
        }

        // 四条边分别判定：只有"选区的真实边"落在这块屏上才画 ——
        // 被屏幕边界裁断的那条（= 两屏接缝处）不画，否则接缝上会多出一条竖线/横线。
        var screenRect = new PixelRect(screen.X, screen.Y, screen.Width, screen.Height);

        var drawTop = SelectionEdgeRules.ShouldDraw(selection, screenRect, SelectionEdge.Top);
        var drawBottom = SelectionEdgeRules.ShouldDraw(selection, screenRect, SelectionEdge.Bottom);
        var drawLeft = SelectionEdgeRules.ShouldDraw(selection, screenRect, SelectionEdge.Left);
        var drawRight = SelectionEdgeRules.ShouldDraw(selection, screenRect, SelectionEdge.Right);

        const double line = SelectionLineThickness;

        SetDim(_selTop, l, t, r - l, line);
        SetDim(_selBottom, l, Math.Max(t, b - line), r - l, line);
        SetDim(_selLeft, l, t, line, b - t);
        SetDim(_selRight, Math.Max(l, r - line), t, line, b - t);

        _selTop.IsVisible = drawTop;
        _selBottom.IsVisible = drawBottom;
        _selLeft.IsVisible = drawLeft;
        _selRight.IsVisible = drawRight;

        // 悬停框住的是具体窗口时，加一层很淡的黄底（和单屏"窗口高亮"一个语言）；
        // 框的是整个虚拟桌面时不铺，免得整屏发黄。
        var isWindowFrame = _session.HoverWindow is { } hover
            && hover == selection;

        if (isWindowFrame)
        {
            SetDim(_windowTint, l, t, r - l, b - t);
            _windowTint.IsVisible = true;
        }
        else
        {
            _windowTint.IsVisible = false;
        }

        SetDim(_dimTop, 0, 0, viewWidth, Math.Max(0, t));
        SetDim(_dimBottom, 0, b, viewWidth, Math.Max(0, viewHeight - b));
        SetDim(_dimLeft, 0, t, Math.Max(0, l), Math.Max(0, b - t));
        SetDim(_dimRight, r, t, Math.Max(0, viewWidth - r), Math.Max(0, b - t));

        _dimTop.IsVisible = _dimBottom.IsVisible = _dimLeft.IsVisible = _dimRight.IsVisible = true;
    }

    /// <summary>藏掉所有选区图形（压暗之外的框线 / 黄底）。</summary>
    private void HideSelection()
    {
        _selTop.IsVisible = false;
        _selBottom.IsVisible = false;
        _selLeft.IsVisible = false;
        _selRight.IsVisible = false;
        _windowTint.IsVisible = false;
    }

    /// <summary>屏幕正中的"最终结果"缩略图（拖动时实时更新，让用户边拖边看会截出什么）。</summary>
    private void RefreshPreview()
    {
        if (_session.Preview is not { } preview)
        {
            _previewPanel.IsVisible = false;
            return;
        }

        _previewImage.Source = preview;
        _previewImage.MaxWidth = 260;
        _previewImage.MaxHeight = 170;
        _previewPanel.IsVisible = true;

        _previewLabel.Text = _session.Selection is { } rect ? $"{rect.Width} x {rect.Height}" : string.Empty;
    }

    /// <summary>
    /// 画"整个虚拟桌面并集的外轮廓"：只在这一屏位于外沿的边上画，相邻显示器之间的拼接缝不画；
    /// 线向内缩 2px，保证一定看得见。
    /// </summary>
    private void RefreshOutline(double viewWidth, double viewHeight)
    {
        var me = _screen.Bounds;

        var topExternal = IsExternalEdge(me.X, me.Y, me.Width, horizontal: true, edgeIsLow: true);
        var bottomExternal = IsExternalEdge(me.X, me.Y + me.Height, me.Width, horizontal: true, edgeIsLow: false);
        var leftExternal = IsExternalEdge(me.Y, me.X, me.Height, horizontal: false, edgeIsLow: true);
        var rightExternal = IsExternalEdge(me.Y, me.X + me.Width, me.Height, horizontal: false, edgeIsLow: false);

        ShowEdge(_outlineTop, topExternal, horizontal: true, lowSide: true, viewWidth, viewHeight);
        ShowEdge(_outlineBottom, bottomExternal, horizontal: true, lowSide: false, viewWidth, viewHeight);
        ShowEdge(_outlineLeft, leftExternal, horizontal: false, lowSide: true, viewWidth, viewHeight);
        ShowEdge(_outlineRight, rightExternal, horizontal: false, lowSide: false, viewWidth, viewHeight);
    }

    private static void ShowEdge(
        Border border,
        bool visible,
        bool horizontal,
        bool lowSide,
        double viewWidth,
        double viewHeight)
    {
        border.IsVisible = visible;

        if (!visible)
        {
            return;
        }

        if (horizontal)
        {
            border.Width = Math.Max(0, viewWidth);
            border.Height = OutlineThickness;
            border.Margin = new Thickness(0, lowSide ? 0 : Math.Max(0, viewHeight - OutlineThickness), 0, 0);
        }
        else
        {
            border.Width = OutlineThickness;
            border.Height = Math.Max(0, viewHeight);
            border.Margin = new Thickness(lowSide ? 0 : Math.Max(0, viewWidth - OutlineThickness), 0, 0, 0);
        }
    }

    /// <summary>
    /// 这条边是不是"外沿"：只有当**没有别的显示器**贴着这条边（且沿边方向有重叠）时才算外沿。
    /// <para>
    /// 相邻判定留 <see cref="EdgeTolerance"/> 像素容差：混合 DPI 下两块屏的边界可能差一两个像素
    /// （如 2559 与 2560），严格相等会让"内部拼接缝"被判成外沿 —— 于是每块屏四周都画满线，
    /// 看起来就是一个十字格。
    /// </para>
    /// </summary>
    private bool IsExternalEdge(int along, int line, int length, bool horizontal, bool edgeIsLow)
    {
        foreach (var other in _session.Screens)
        {
            if (other.Bounds == _screen.Bounds)
            {
                continue;
            }

            var otherEdge = horizontal
                ? (edgeIsLow ? other.Bounds.Y + other.Bounds.Height : other.Bounds.Y)
                : (edgeIsLow ? other.Bounds.X + other.Bounds.Width : other.Bounds.X);

            if (Math.Abs(otherEdge - line) > EdgeTolerance)
            {
                continue;
            }

            var otherAlong = horizontal ? other.Bounds.X : other.Bounds.Y;
            var otherLength = horizontal ? other.Bounds.Width : other.Bounds.Height;

            if (otherAlong < along + length - EdgeTolerance && otherAlong + otherLength > along + EdgeTolerance)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>相邻判定的像素容差（混合 DPI 下边界可能差 1~2 像素）。</summary>
    private const int EdgeTolerance = 2;

    private static void SetDim(Border border, double x, double y, double width, double height)
    {
        border.Margin = new Thickness(x, y, 0, 0);
        border.Width = Math.Max(0, width);
        border.Height = Math.Max(0, height);
    }
}
