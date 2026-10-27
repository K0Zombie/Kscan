using System.Runtime.InteropServices;
using Kscan.Core.Imaging;
using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Windows;

/// <summary>
/// Windows 光标采集：<c>GetCursorInfo</c> 拿到光标句柄与位置，
/// <c>DrawIconEx</c> 把它画到一块 32 位 DIB 上再读像素。
/// <para>
/// 用 <c>DrawIconEx</c> 而不是直接读 <c>GetIconInfo</c> 的掩码位图：现代 <c>.cur</c>
/// 带原生 alpha 通道，DrawIconEx 会把 alpha 一起画出来；单色光标则会在后处理里
/// 按"非黑即不透明"补一份 alpha（否则整张图 alpha=0，画出来什么也看不见）。
/// </para>
/// </summary>
internal sealed class WindowsCursorCapture : ICursorCapture
{
    private const uint CursorShowing = 0x00000001;
    private const uint DiNormal = 0x0003;
    private const int SmCxCursor = 13;
    private const int SmCyCursor = 14;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int MdtEffectiveDpi = 0;

    public bool IsSupported => OperatingSystem.IsWindows();

    public CursorSnapshot? Capture()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        // 位置用 GetCursorPos：PerMonitorV2 下返回物理像素（GetCursorInfo 的 ptScreenPos 在
        // 切过 DPI 上下文的线程上可能被虚拟化，这里不用它）。
        if (!GetCursorPos(out var position))
        {
            return null;
        }

        // cbSize 必须先填：不填 GetCursorInfo 直接返回 false（典型 Win32 坑）
        var cursor = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };

        if (!GetCursorInfo(ref cursor) || (cursor.flags & CursorShowing) == 0 || cursor.hCursor == IntPtr.Zero)
        {
            return null;
        }

        if (!GetIconInfo(cursor.hCursor, out var icon))
        {
            return null;
        }

        try
        {
            return Draw(cursor, icon, position.X, position.Y);
        }
        finally
        {
            if (icon.hbmMask != IntPtr.Zero)
            {
                DeleteObject(icon.hbmMask);
            }

            if (icon.hbmColor != IntPtr.Zero)
            {
                DeleteObject(icon.hbmColor);
            }
        }
    }

    private static CursorSnapshot? Draw(CURSORINFO cursor, ICONINFO icon, int screenX, int screenY)
    {
        var (iconWidth, iconHeight) = IconSize(icon);

        // 目标尺寸 = 屏幕上**真正画出来**的光标大小（物理像素），规则见 CursorSizePolicy：
        //   1) 光标所在显示器 DPI 下系统声明的大小（GetSystemMetricsForDpi(SM_CXCURSOR)）；
        //   2) 辅助功能「鼠标指针大小」的基准尺寸（CursorBaseSize，调大指针后只有它变大）；
        //   3) 位图尺寸 × 显示器 DPI / 96（只放大不缩小）。
        // 以前只看系统 DPI（而且是桌面窗口的系统 DPI），既漏了光标所在屏幕的缩放，
        // 也漏了辅助功能里的指针大小 —— 表现就是遮罩上的光标比真实光标小一圈。
        var dpi = MonitorDpi(screenX, screenY);
        var (width, height) = TargetSize(dpi, iconWidth, iconHeight);

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }

        var memoryDc = CreateCompatibleDC(screenDc);
        if (memoryDc == IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            return null;
        }

        // biHeight 用负数 = 自上而下存，读到像素顺序与 ImageFrame（行优先、左上原点）一致
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };

        var info = new BITMAPINFO { bmiHeader = header };
        var dib = CreateDIBSection(memoryDc, ref info, 0, out var bits, IntPtr.Zero, 0);

        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return null;
        }

        try
        {
            var old = SelectObject(memoryDc, dib);
            DrawIconEx(memoryDc, 0, 0, cursor.hCursor, width, height, 0, IntPtr.Zero, DiNormal);
            SelectObject(memoryDc, old);

            var pixels = new byte[width * height * ImageFrame.BytesPerPixel];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            EnsureAlpha(pixels);

            // 热点跟着缩放（横纵各自换算）：CursorSnapshot.Left = ScreenX - HotspotX，都必须是物理像素
            return new CursorSnapshot(
                new ImageFrame(width, height, pixels),
                ScaleHotspot(icon.xHotspot, iconWidth, width),
                ScaleHotspot(icon.yHotspot, iconHeight, height),
                screenX,
                screenY);
        }
        finally
        {
            DeleteObject(dib);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static int ScaleHotspot(int hotspot, int iconSize, int targetSize) =>
        iconSize <= 0 ? hotspot : (int)Math.Round(hotspot * (double)targetSize / iconSize);

    /// <summary>
    /// 光标在屏幕上画出来有多大（物理像素），规则见 <see cref="CursorSizePolicy"/>。
    /// <para>
    /// 三个来源互相取大：系统度量 <c>SM_CXCURSOR@DPI</c>、辅助功能里的光标基准尺寸
    /// （<c>HKCU\Control Panel\Cursors\CursorBaseSize</c>，调大指针后它才是真实大小，而
    /// <c>SM_CXCURSOR</c> 一直是 32）、光标位图尺寸 × DPI。
    /// </para>
    /// </summary>
    private static (int Width, int Height) TargetSize(uint dpi, int iconWidth, int iconHeight)
    {
        var (systemWidth, systemHeight) = SystemCursorSize(dpi);

        return CursorSizePolicy.Resolve(
            (int)dpi, iconWidth, iconHeight, systemWidth, systemHeight, WindowsCursorMetrics.CursorBaseSize());
    }

    /// <summary>系统声明的光标尺寸（已按该屏 DPI 缩放）；老系统上没有这组 API 时返回 (0, 0)。</summary>
    private static (int Width, int Height) SystemCursorSize(uint dpi)
    {
        try
        {
            return (GetSystemMetricsForDpi(SmCxCursor, dpi), GetSystemMetricsForDpi(SmCyCursor, dpi));
        }
        catch (EntryPointNotFoundException)
        {
            return (0, 0);   // Win10 1607 之前没有 GetSystemMetricsForDpi
        }
    }

    /// <summary>
    /// 光标所在显示器的 DPI（<c>GetDpiForMonitor</c>，<c>MDT_EFFECTIVE_DPI</c>）。
    /// 拿不到时依次退回系统 DPI、96。
    /// </summary>
    private static uint MonitorDpi(int screenX, int screenY)
    {
        try
        {
            var monitor = MonitorFromPoint(new NativePoint { X = screenX, Y = screenY }, MonitorDefaultToNearest);

            if (monitor != IntPtr.Zero &&
                GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 &&
                dpiX > 0)
            {
                return dpiX;
            }
        }
        catch (DllNotFoundException)
        {
            // 没有 shcore.dll（很老的系统）
        }
        catch (EntryPointNotFoundException)
        {
        }

        try
        {
            var dpi = GetDpiForWindow(GetDesktopWindow());
            if (dpi == 0)
            {
                dpi = GetDpiForSystem();
            }

            return dpi == 0 ? 96 : dpi;
        }
        catch (EntryPointNotFoundException)
        {
            return 96;   // Win10 1607 之前没有这组 API
        }
    }

    /// <summary>
    /// 光标图标位图的<b>真实尺寸</b>（像素）。
    /// <para>
    /// 不要用 <c>SM_CXCURSOR</c>：它只是"当前系统光标尺寸"的约定值，和高 DPI 缩放、
    /// 用户自定义光标（辅助功能里的指针大小）都不一定对得上。按它建 DIB 再 DrawIconEx
    /// 会把光标整体缩放到错误尺寸 —— 表现就是遮罩上的光标比真实光标偏大 / 偏小。
    /// </para>
    /// </summary>
    private static (int Width, int Height) IconSize(ICONINFO icon)
    {
        if (TryBitmapSize(icon.hbmColor) is { } color)
        {
            return color;
        }

        if (TryBitmapSize(icon.hbmMask) is { } mask)
        {
            // 单色光标的 mask 位图是 AND + XOR 两份上下叠在一起的，高度要减半
            return (mask.Width, Math.Max(1, mask.Height / 2));
        }

        return (Math.Max(1, GetSystemMetrics(SmCxCursor)), Math.Max(1, GetSystemMetrics(SmCyCursor)));
    }

    private static (int Width, int Height)? TryBitmapSize(IntPtr bitmap)
    {
        if (bitmap == IntPtr.Zero)
        {
            return null;
        }

        var info = default(BITMAP);
        if (GetObject(bitmap, Marshal.SizeOf<BITMAP>(), ref info) == 0 || info.bmWidth <= 0 || info.bmHeight <= 0)
        {
            return null;
        }

        return (info.bmWidth, info.bmHeight);
    }

    /// <summary>
    /// 单色光标的 DIB 里 alpha 全是 0；按"非纯黑即不透明"补上 alpha，
    /// 否则合成到截图上时整张光标都透明。
    /// </summary>
    private static void EnsureAlpha(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += ImageFrame.BytesPerPixel)
        {
            if (pixels[i] != 0)
            {
                return;   // 已经有 alpha（带通道的 .cur），不用动
            }
        }

        for (var i = 0; i < pixels.Length; i += ImageFrame.BytesPerPixel)
        {
            var b = pixels[i];
            var g = pixels[i + 1];
            var r = pixels[i + 2];

            if (r + g + b > 0)
            {
                pixels[i + 3] = 255;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hCursor;
        public NativePoint ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public short bmPlanes;
        public short bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(IntPtr obj, int size, ref BITMAP info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorInfo(ref CURSORINFO cursor);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(
        IntPtr dc, int x, int y, IntPtr icon, int width, int height, int step, IntPtr flickerFreeBrush, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
