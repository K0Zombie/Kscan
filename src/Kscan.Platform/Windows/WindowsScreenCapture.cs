using System.Runtime.InteropServices;
using Kscan.Core.Imaging;
using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Windows;

/// <summary>
/// Windows 屏幕采集：用 GDI 的 <c>BitBlt</c> 一次性抓整个虚拟桌面（多显示器拼在一起）。
/// 框选不在这里做 —— 由 UI 把这张整屏图铺满窗口、让用户拖出选区再裁剪，
/// 这样选区反馈、放大镜之类都可以做得很自然。
/// </summary>
internal sealed class WindowsScreenCapture : IScreenCapture
{
    /// <summary>PerMonitorV2（Win10 1703 起可用）。</summary>
    private const int DpiAwarenessContextPerMonitorAwareV2 = -4;

    /// <summary>
    /// 进程必须是 DPI 感知的，否则 <c>GetSystemMetrics</c> 返回的是缩放后的逻辑像素
    /// （实测 150% 缩放下 2560x1440 会变成 1707x960），BitBlt 抓出来的图也是错的。
    /// 首次使用本类时声明 PerMonitorV2；老系统没有这个 API 就忽略（退化为 manifest 设置）。
    /// </summary>
    static WindowsScreenCapture()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                SetProcessDpiAwarenessContext(new IntPtr(DpiAwarenessContextPerMonitorAwareV2));
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
                // Win10 1703 之前没有该 API
            }
        }
    }

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private const int Srccopy = 0x00CC0020;

    /// <summary>带上 CAPTUREBLT 才能抓到分层窗口（否则某些悬浮窗会缺失）。</summary>
    private const int Captureblt = 0x40000000;

    private const uint DibRgbColors = 0;

    /// <summary>只抓这一块（物理像素）；null = 整个虚拟桌面。</summary>
    /// <remarks>
    /// 以前这是<b>静态字段</b>：<c>CaptureDesktopAsync(region)</c> 把 region 存在这里，再调无参版本。
    /// 无参版本不会把它清掉 —— 一旦先做过"单屏抓图"（会用 region 版本），之后切回跨屏（无参版本）
    /// 就仍然按那块旧 region 抓，抓到的是主屏那一块，拼图尺寸变成 1920x1080：
    /// 副屏拿到的是主屏内容（用户实测"副屏显示了主屏的内容"），主屏反而没图。
    /// 现在 region 只作为参数往下传，没有跨调用的共享状态。
    /// </remarks>

    public bool IsSupported => OperatingSystem.IsWindows();

    public string Description => "Windows BitBlt（整个虚拟桌面）";

    public string? DescriptionKey => "Platform.Capture.WindowsBitBlt";

    /// <summary>Windows 上没有"外部命令框选"这条路，返回 null 表示由 UI 自绘选区。</summary>
    public Task<ImageFrame?> CaptureRegionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<ImageFrame?>(null);

    public Task<ImageFrame?> CaptureDesktopAsync(ScreenRect? region, CancellationToken cancellationToken = default) =>
        Task.Run(() => CaptureVirtualDesktop(region), cancellationToken);

    public Task<ImageFrame?> CaptureDesktopAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => CaptureVirtualDesktop(null), cancellationToken);

    private static ImageFrame? CaptureVirtualDesktop(ScreenRect? region)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var (left, top, width, height) = region is { } r
            ? (r.X, r.Y, r.Width, r.Height)
            : (GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen),
               GetSystemMetrics(SmCxVirtualScreen), GetSystemMetrics(SmCyVirtualScreen));

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }

        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var previous = SelectObject(memoryDc, bitmap);

        try
        {
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, left, top, Srccopy | Captureblt))
            {
                return null;
            }

            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                // 负数高度 = 自上而下，省得再翻转一遍
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
            };

            var pixels = new byte[width * height * 4];

            // 32bpp 的 BI_RGB 不需要颜色表，直接传 BITMAPINFOHEADER 即可
            if (GetDIBits(memoryDc, bitmap, 0, (uint)height, pixels, ref header, DibRgbColors) == 0)
            {
                return null;
            }

            // BitBlt 给的是 BGRX，第 4 字节未定义；统一成不透明
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }

            return new ImageFrame(width, height, pixels);
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hDest, int x, int y, int width, int height, IntPtr hSource, int sourceX, int sourceY, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hDc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDc);
}
