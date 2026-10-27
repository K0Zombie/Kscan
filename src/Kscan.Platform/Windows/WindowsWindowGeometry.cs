using System.Runtime.InteropServices;
using System.Text;
using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Windows;

/// <summary>
/// 窗口矩形：区分「GetWindowRect 的窗口矩形」与「屏幕上真正看得见的那块」。
/// <para>
/// 为什么要分开：Vista 之后，一个带标准边框的窗口，<c>GetWindowRect</c> 会在<b>左 / 右 / 下</b>
/// 各多出 7~8 px 的"透明拖拽边框"（上面因为标题栏刚好没有）。用 GetWindowRect 去屏幕裁剪，
/// 截出来的图就会在左、右、下三边多出一圈桌面 —— 也就是"整窗截图多括了一点"。
/// </para>
/// <para>
/// <c>DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)</c> 返回的正是那圈透明边框<b>以内</b>的
/// 可见边框，和视觉上的窗口完全对齐；最大化窗口也一样（GetWindowRect 会朝屏幕外多算 7~8 px）。
/// DWM 不可用（老系统 / DWM 关闭 / 调用失败）时退回 GetWindowRect。
/// </para>
/// </summary>
internal static class WindowsWindowGeometry
{
    /// <summary><c>DWMWA_EXTENDED_FRAME_BOUNDS</c>（Windows Vista+）。</summary>
    private const int DwmwaExtendedFrameBounds = 9;

    /// <summary><c>GWL_STYLE</c> 与 <c>WS_THICKFRAME</c>（可调边框）。</summary>
    private const int GwlStyle = -16;
    private const long WsThickFrame = 0x00040000;

    private const uint MonitorDefaultToNearest = 2;

    /// <summary>
    /// 窗口在屏幕上真正看得见的矩形（像素坐标，物理像素）。
    /// 拿不到（句柄无效 / 窗口已销毁）返回 <c>null</c>。
    /// </summary>
    public static ScreenRect? VisibleRect(IntPtr handle)
    {
        if (!OperatingSystem.IsWindows() || handle == IntPtr.Zero || !IsWindow(handle))
        {
            return null;
        }

        // 桌面本体（Progman / WorkerW / 无标题的桌面壳）按**工作区**算：
        // 它的原始矩形是整块屏幕、盖住任务栏，高亮/截图都多出任务栏那一条（用户明确说不要）。
        if (IsDesktopLayer(handle) && WorkArea(handle) is { } workArea)
        {
            return workArea;
        }

        // 任务栏：光标停在任务栏上时框**整块屏幕**（用户要的行为：任务栏区域 = 整屏），
        // 而停在桌面（其它地方）时框工作区 —— 两者配合起来，点哪都能截到想要的范围。
        if (IsTaskbar(handle) && FullScreen(handle) is { } fullScreen)
        {
            return fullScreen;
        }

        // 只有"带可调边框"的窗口才有 Vista 之后那圈透明拖拽边框。桌面 / Progman / 任务栏这类
        // 无边框窗口，DWM 的 EXTENDED_FRAME_BOUNDS 也会把矩形往里缩 7~8px —— 拿它去屏幕裁剪就会
        // 把真实内容切掉（用户报的"桌面截图除了上方都被切了"）。所以这类窗口一律用原始窗口矩形。
        if (!HasResizableFrame(handle))
        {
            return RawRect(handle);
        }

        // 原始矩形正好等于所在显示器的工作区 → 这是无边框全屏 / 边框屏窗口
        // （浏览器 F11、UWP、播放器全屏）。它的 7~8px 是 DWM 画在**窗口外面**的边框，
        // 拿 DWM 的可见矩形去裁就会把画面切掉（用户报的"桌面 / 全屏被切"）。
        if (RawRect(handle) is { } raw && EqualsWorkArea(handle, raw))
        {
            return raw;
        }

        if (TryGetDwmFrameBounds(handle) is { } bounds && IsInside(bounds, RawRect(handle)))
        {
            return bounds;
        }

        // 退回 GetWindowRect：比可见区域大一圈，但总比没有强
        return RawRect(handle);
    }

    /// <summary>
    /// 命中测试用的矩形（判断"光标是不是在这个窗口上"）。
    /// 和 <see cref="VisibleRect"/> 的区别：任务栏的**高亮矩形**是整屏，但它实际只占屏幕底部一条 ——
    /// 命中测试必须按它真正的条状矩形算，否则它会命中屏幕上任何一点，整个遮罩就永远是全屏框。
    /// </summary>
    public static ScreenRect? HitRect(IntPtr handle)
    {
        if (!OperatingSystem.IsWindows() || handle == IntPtr.Zero || !IsWindow(handle))
        {
            return null;
        }

        if (IsTaskbar(handle))
        {
            return RawRect(handle);
        }

        return VisibleRect(handle);
    }

    /// <summary>任务栏（主 / 副屏）。</summary>
    internal static bool IsTaskbar(IntPtr handle)
    {
        var name = new StringBuilder(64);
        if (GetClassName(handle, name, name.Capacity) <= 0)
        {
            return false;
        }

        return name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary>窗口所在显示器的整块屏幕（含任务栏）。</summary>
    private static ScreenRect? FullScreen(IntPtr handle)
    {
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return null;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return null;
        }

        var bounds = info.Monitor;
        return new ScreenRect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }

    /// <summary>桌面本体：Progman / WorkerW / 无标题的 ApplicationFrameWindow（Win11 的桌面壳）。</summary>
    private static bool IsDesktopLayer(IntPtr handle)
    {
        var name = new StringBuilder(64);
        if (GetClassName(handle, name, name.Capacity) <= 0)
        {
            return false;
        }

        return name.ToString() switch
        {
            "Progman" or "WorkerW" => true,
            "ApplicationFrameWindow" => GetWindowTextLength(handle) == 0,
            _ => false,
        };
    }

    /// <summary>窗口所在显示器的工作区（不含任务栏）。</summary>
    private static ScreenRect? WorkArea(IntPtr handle)
    {
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return null;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return null;
        }

        var work = info.Work;
        return new ScreenRect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
    }

    /// <summary>不看 DWM 的原始窗口矩形（含透明拖拽边框）。</summary>
    public static ScreenRect? RawRect(IntPtr handle) =>        OperatingSystem.IsWindows() && handle != IntPtr.Zero && GetWindowRect(handle, out var rect)
            ? ToScreenRect(rect)
            : null;

    /// <summary>
    /// 窗口矩形是不是"正好铺满所在显示器的工作区"：这种窗口（无边框全屏 / 边框屏）DWM 的可见矩形
    /// 会往里缩 7~8px，但那圈是画在窗口**外面**的边框，不能用来裁内容。
    /// </summary>
    internal static bool EqualsWorkArea(IntPtr handle, ScreenRect window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var work = info.Work;
        return Math.Abs(window.X - work.Left) <= 2
               && Math.Abs(window.Y - work.Top) <= 2
               && Math.Abs(window.X + window.Width - work.Right) <= 2
               && Math.Abs(window.Y + window.Height - work.Bottom) <= 2;
    }

    /// <summary>窗口有没有「可调边框」（<c>WS_THICKFRAME</c>）：有它才存在那圈透明拖拽边框。</summary>
    internal static bool HasResizableFrame(IntPtr handle)
    {
        if (!OperatingSystem.IsWindows() || handle == IntPtr.Zero)
        {
            return false;
        }

        var style = GetWindowLongPtr(handle, GwlStyle).ToInt64();
        return (style & WsThickFrame) != 0;
    }

    /// <summary>可见矩形是不是落在窗口矩形里面（DWM 偶发返回怪值时的护栏）。</summary>
    private static bool IsInside(ScreenRect visible, ScreenRect? window) =>
        window is not { } outer
        || (visible.X >= outer.X - 1
            && visible.Y >= outer.Y - 1
            && visible.X + visible.Width <= outer.X + outer.Width + 1
            && visible.Y + visible.Height <= outer.Y + outer.Height + 1);

    /// <summary>DWM 扩展边框（失败返回 null，不抛异常）。</summary>
    internal static ScreenRect? TryGetDwmFrameBounds(IntPtr handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var size = Marshal.SizeOf<NativeRect>();
        if (DwmGetWindowAttribute(handle, DwmwaExtendedFrameBounds, out var bounds, size) != 0)
        {
            return null;
        }

        var rect = ToScreenRect(bounds);

        // 有些窗口（例如刚创建 / 正在动画的）会返回空矩形，这时候宁可退回 GetWindowRect
        return rect.Width > 0 && rect.Height > 0 ? rect : null;
    }

    /// <summary>
    /// 「按窗口矩形渲染出来的图」里，可见区域对应哪一块：返回要裁的 (x, y, 宽, 高)。
    /// <para>
    /// PrintWindow 按窗口矩形出图，可见内容从透明边框之后开始，所以要把这块裁掉。
    /// 可见矩形恰好等于窗口矩形时返回整图；数据反常（空矩形 / 跑到窗口外）时也安全地退回整图。
    /// </para>
    /// </summary>
    internal static (int X, int Y, int Width, int Height) ClipToVisible(ScreenRect window, ScreenRect visible)
    {
        if (window.Width <= 0 || window.Height <= 0 || visible.Width <= 0 || visible.Height <= 0)
        {
            return (0, 0, Math.Max(window.Width, 0), Math.Max(window.Height, 0));
        }

        var x = Math.Clamp(visible.X - window.X, 0, window.Width - 1);
        var y = Math.Clamp(visible.Y - window.Y, 0, window.Height - 1);
        var width = Math.Clamp(visible.Width, 1, window.Width - x);
        var height = Math.Clamp(visible.Height, 1, window.Height - y);

        return (x, y, width, height);
    }

    private static ScreenRect ToScreenRect(NativeRect rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out NativeRect value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
