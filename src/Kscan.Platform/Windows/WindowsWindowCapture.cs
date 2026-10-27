using System.Runtime.InteropServices;
using System.Text;
using Kscan.Core.Imaging;
using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Windows;

/// <summary>
/// Windows 整窗离屏渲染：<c>PrintWindow</c> + <c>PW_RENDERFULLCONTENT</c>。
/// <para>
/// 用途：窗口比屏幕大（拖出屏幕外）时，屏幕截图只拍到露出的部分；
/// PrintWindow 让窗口把自己画到我们给的 DC 上，能拿到完整内容。
/// </para>
/// <para>
/// 已知限制：部分用 GPU / UWP 绘制的窗口会返回全黑（Win11 上比 Win10 少很多）。
/// 调用方应检查结果是否全黑再决定回退到屏幕裁剪。
/// </para>
/// </summary>
internal sealed class WindowsWindowCapture : IWindowCapture
{
    /// <summary>PW_RENDERFULLCONTENT：Win8.1+ 才有，能抓到 DirectComposition 内容。</summary>
    private const uint PwRenderFullContent = 0x00000002;

    private const uint DibRgbColors = 0;

    public bool IsSupported => OperatingSystem.IsWindows();

    public ImageFrame? Capture(IntPtr handle) => CaptureWhole(handle)?.Image;

    public WindowCapture? CaptureWhole(IntPtr handle)
    {
        if (!OperatingSystem.IsWindows() || handle == IntPtr.Zero || !IsWindow(handle))
        {
            return null;
        }

        // 桌面 / 任务栏这类 shell 窗口：PrintWindow 只会给一张全黑图（用户报的"点桌面截图是黑屏"）。
        // 直接返回 null，让调用方回退到"抓屏裁剪"——那才是桌面上真正看得到的样子。
        if (IsShellSurface(handle))
        {
            return null;
        }

        if (!GetWindowRect(handle, out var rect))
        {
            return null;
        }

        var renderWidth = rect.Right - rect.Left;
        var renderHeight = rect.Bottom - rect.Top;
        if (renderWidth <= 0 || renderHeight <= 0)
        {
            return null;
        }

        // PrintWindow 的坐标系从「窗口矩形」左上角开始，而窗口**可见**的那块从透明拖拽边框
        // （左 / 右 / 下各 7~8 px）之后才开始。这里按窗口矩形渲染，再把可见部分裁出来 ——
        // 否则整窗截图会多出一圈桌面像素（用户看到的"整窗多括了一点"）。
        var windowRect = new ScreenRect(rect.Left, rect.Top, renderWidth, renderHeight);
        var visible = WindowsWindowGeometry.VisibleRect(handle) ?? windowRect;
        var (offsetX, offsetY, width, height) = WindowsWindowGeometry.ClipToVisible(windowRect, visible);
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

        // 负数高度 = 自上而下存，与 ImageFrame 的行序一致
        var header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = renderWidth,
            Height = -renderHeight,
            Planes = 1,
            BitCount = 32,
            Compression = 0,
        };

        var dib = CreateDIBSection(memoryDc, ref header, DibRgbColors, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return null;
        }

        try
        {
            var previous = SelectObject(memoryDc, dib);

            // 图像 alpha 通道在 GDI 里是"未定义"的，先铺白底，避免半透明窗口留下垃圾像素
            var brush = CreateSolidBrush(0x00FFFFFF);
            var fill = new Rect { Left = 0, Top = 0, Right = renderWidth, Bottom = renderHeight };
            FillRect(memoryDc, ref fill, brush);
            DeleteObject(brush);

            var rendered = PrintWindow(handle, memoryDc, PwRenderFullContent)
                           || PrintWindow(handle, memoryDc, 0);

            SelectObject(memoryDc, previous);

            if (!rendered)
            {
                return null;
            }

            var pixels = new byte[renderWidth * renderHeight * ImageFrame.BytesPerPixel];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            for (var i = 3; i < pixels.Length; i += ImageFrame.BytesPerPixel)
            {
                pixels[i] = 255;
            }

            var frameImage = new ImageFrame(renderWidth, renderHeight, pixels);

            // 裁到「可见窗口」：直接用 DWM 可见边框给出的偏移做裁剪（几何裁，不猜像素）。
            //
            // 以前用"这条边是不是没画上去"的像素判据，遇到会把透明边框那圈也刷上颜色的窗口就判成
            // "不用裁" → 整窗截图比窗口多出左 / 右 / 下各 7~8px（用户报的"截被遮挡窗口时多截了一圈"）。
            // 可见边框本来就是 Windows 认为"这个窗口露出来的那块"（Alt+Tab 缩略图也按它），
            // 按它裁既准又稳定；无边框窗口 / 桌面这些 VisibleRect 直接等于窗口矩形，偏移为 0，不裁。
            var cropX = Math.Clamp(offsetX, 0, renderWidth - 1);
            var cropY = Math.Clamp(offsetY, 0, renderHeight - 1);
            var cropWidth = Math.Clamp(width, 1, renderWidth - cropX);
            var cropHeight = Math.Clamp(height, 1, renderHeight - cropY);

            var cropped = cropX == 0 && cropY == 0 && cropWidth == renderWidth && cropHeight == renderHeight
                ? frameImage
                : frameImage.Crop(cropX, cropY, cropWidth, cropHeight);

            // PrintWindow 对某些窗口给的是全黑图：别把它当结果，交给调用方回退屏幕裁剪
            if (IsNearlyBlank(cropped))
            {
                return null;
            }

            var coverage = new ScreenRect(rect.Left + cropX, rect.Top + cropY, cropped.Width, cropped.Height);
            return new WindowCapture(cropped, coverage);
        }
        finally
        {
            DeleteObject(dib);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>窗口类名是不是"桌面 / 任务栏"这类 shell 表面（它们没有可渲染的窗口内容）。</summary>
    private static bool IsShellSurface(IntPtr handle)
    {
        var name = new StringBuilder(64);
        if (GetClassName(handle, name, name.Capacity) <= 0)
        {
            return false;
        }

        return name.ToString() switch
        {
            "Progman" or "WorkerW" or "SysListView32" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" => true,
            _ => false,
        };
    }

    /// <summary>整张图基本是同一个颜色（PrintWindow 对某些窗口返回全黑 / 空白）。</summary>
    private static bool IsNearlyBlank(ImageFrame frame)
    {
        const int samples = 64;

        var total = frame.Width * frame.Height;
        var stride = Math.Max(1, total / samples);
        var first = -1;
        var same = 0;
        var counted = 0;

        for (var index = 0; index < total; index += stride)
        {
            var offset = index * ImageFrame.BytesPerPixel;
            var value = frame.Pixels[offset] | (frame.Pixels[offset + 1] << 8) | (frame.Pixels[offset + 2] << 16);
            counted++;

            if (first < 0)
            {
                first = value;
                same = 1;
                continue;
            }

            if (value == first)
            {
                same++;
            }
        }

        return counted > 0 && same * 100 >= counted * 95;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder name, int max);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr handle, IntPtr dc, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);
}
