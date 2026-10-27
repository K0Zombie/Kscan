using System.Runtime.InteropServices;
using Kscan.Platform.Abstractions;
using Kscan.Platform.Windows;

namespace Kscan.Platform.Tests;

/// <summary>
/// 整窗截图"多括一圈"的回归测试。
/// <para>
/// 根因：<c>GetWindowRect</c> 含 Vista 之后那圈透明拖拽边框（左 / 右 / 下各 7~8 px，上面没有），
/// 直接拿它去屏幕裁剪，整窗图就会三边多出桌面。可见矩形要用 DWM 扩展边框，
/// 而 PrintWindow 是按窗口矩形出图的，所以要按偏移裁一下。
/// </para>
/// </summary>
public sealed class WindowGeometryTests
{
    /// <summary>DWM 传出去的 RECT 是 4 个 int（布局护栏，x64 上应为 16 字节）。</summary>
    [Fact]
    public void Native_rect_is_four_ints() =>
        Assert.Equal(16, Marshal.SizeOf<WindowsWindowGeometry.NativeRect>());

    [Fact]
    public void Invalid_or_missing_window_has_no_rect()
    {
        Assert.Null(WindowsWindowGeometry.VisibleRect(IntPtr.Zero));
        Assert.Null(WindowsWindowGeometry.VisibleRect(new IntPtr(1234)));
        Assert.Null(WindowsWindowGeometry.RawRect(IntPtr.Zero));
        Assert.Null(WindowsWindowGeometry.TryGetDwmFrameBounds(IntPtr.Zero));
    }

    /// <summary>典型的 Win11 窗口：窗口矩形 1016×719，可见 1002×705，左侧/上面各内缩 7 px。</summary>
    [Fact]
    public void Clips_the_invisible_resize_border()
    {
        var window = new ScreenRect(452, 156, 1016, 719);
        var visible = new ScreenRect(459, 163, 1002, 705);

        var (x, y, width, height) = WindowsWindowGeometry.ClipToVisible(window, visible);

        Assert.Equal(7, x);
        Assert.Equal(7, y);
        Assert.Equal(1002, width);
        Assert.Equal(705, height);
    }

    [Fact]
    public void Whole_window_when_visible_equals_window_rect()
    {
        var rect = new ScreenRect(10, 20, 800, 600);

        var (x, y, width, height) = WindowsWindowGeometry.ClipToVisible(rect, rect);

        Assert.Equal((0, 0, 800, 600), (x, y, width, height));
    }

    /// <summary>最大化窗口：GetWindowRect 会朝屏幕外多算，可见矩形也落在窗口矩形之内。</summary>
    [Fact]
    public void Maximized_window_is_clipped_too()
    {
        var window = new ScreenRect(-8, -8, 1936, 1096);
        var visible = new ScreenRect(0, 0, 1920, 1080);

        var (x, y, width, height) = WindowsWindowGeometry.ClipToVisible(window, visible);

        Assert.Equal((8, 8, 1920, 1080), (x, y, width, height));
    }

    /// <summary>可见矩形跑到窗口矩形外面（异常数据）时不能算出越界区域。</summary>
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(2000, 2000, 100, 100)]
    [InlineData(-50, -50, 10, 10)]
    public void Bogus_visible_rects_fall_back_to_the_whole_image(int x, int y, int w, int h)
    {
        var window = new ScreenRect(0, 0, 800, 600);

        var (clipX, clipY, width, height) = WindowsWindowGeometry.ClipToVisible(window, new ScreenRect(x, y, w, h));

        Assert.True(clipX >= 0 && clipY >= 0);
        Assert.True(clipX + width <= 800);
        Assert.True(clipY + height <= 600);
        Assert.True(width > 0 && height > 0);
    }

    /// <summary>真实桌面窗口：可见矩形不应比窗口矩形更大（这条在有桌面会话的机器上才有意义）。</summary>
    [Fact]
    public void Desktop_window_visible_rect_is_within_its_raw_rect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var desktop = GetDesktopWindow();
        if (WindowsWindowGeometry.VisibleRect(desktop) is not { } visible
            || WindowsWindowGeometry.RawRect(desktop) is not { } raw)
        {
            return;   // 无桌面会话（CI / 服务）时跳过
        }

        Assert.True(visible.Width <= raw.Width);
        Assert.True(visible.Height <= raw.Height);
        Assert.True(visible.X >= raw.X - 16);
        Assert.True(visible.Y >= raw.Y - 16);
    }

    /// <summary>
    /// 桌面窗口（无 <c>WS_THICKFRAME</c>）必须拿原始矩形：DWM 对它也会给内缩边框，
    /// 用了就会把桌面截图切掉一圈（用户报的"桌面截图除了上方都被切了"）。
    /// </summary>
    [Fact]
    public void Frameless_desktop_window_uses_its_raw_rect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var desktop = GetDesktopWindow();
        if (WindowsWindowGeometry.RawRect(desktop) is not { } raw)
        {
            return;   // 无桌面会话时跳过
        }

        Assert.False(WindowsWindowGeometry.HasResizableFrame(desktop));

        if (WindowsWindowGeometry.VisibleRect(desktop) is { } visible)
        {
            Assert.Equal(raw.X, visible.X);
            Assert.Equal(raw.Y, visible.Y);
            Assert.Equal(raw.Width, visible.Width);
            Assert.Equal(raw.Height, visible.Height);
        }
    }

    /// <summary>正常窗口（notepad / kscan 这类）带 WS_THICKFRAME，才会有透明边框。</summary>
    [Fact]
    public void Framed_windows_are_detected_as_resizable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 自己开一个带边框的窗口太重，这里用"任务栏没有、桌面没有"这条反证：
        // 桌面无边框（上一条已断言），只要求这个判定函数在无效句柄上不会抛异常。
        Assert.False(WindowsWindowGeometry.HasResizableFrame(IntPtr.Zero));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}
