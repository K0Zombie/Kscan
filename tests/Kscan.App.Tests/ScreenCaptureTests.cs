using Avalonia;
using Avalonia.Headless.XUnit;
using Kscan.Platform;
using Kscan.Platform.Abstractions;

namespace Kscan.App.Tests;

/// <summary>
/// 抓屏的回归：**无参抓图必须抓整个虚拟桌面，不能沿用上一次的 region**。
/// <para>
/// 曾经的 bug：<c>WindowsScreenCapture</c> 用一个静态字段 <c>Region</c> 把"只抓这一块"传下去，
/// 无参版本不清它。于是先做过一次单屏抓图（region = 主屏矩形）之后，切回跨屏时仍然只抓主屏那一块，
/// 拼图尺寸变成 1920x1080 —— 副屏拿到的是主屏内容、主屏反而没图
/// （用户实测："触发截图之后副屏显示了主屏的内容"）。
/// </para>
/// </summary>
public sealed class ScreenCaptureTests
{
    [AvaloniaFact]
    public async Task Full_desktop_capture_ignores_a_previous_region_capture()
    {
        var capture = PlatformServices.ScreenCapture;

        if (!capture.IsSupported)
        {
            return;   // 非 Windows：这条路本来就不走
        }

        var region = new ScreenRect(0, 0, 64, 64);
        var small = await capture.CaptureDesktopAsync(region);

        Assert.NotNull(small);
        Assert.Equal(64, small!.Width);
        Assert.Equal(64, small.Height);

        // 同一进程里紧接着抓"整个桌面"：不能再是 64x64（那说明沿用了上一次的 region）
        var full = await capture.CaptureDesktopAsync();
        Assert.NotNull(full);

        Assert.True(
            full!.Width > 64 && full.Height > 64,
            $"无参抓图应抓整个虚拟桌面，实际 {full.Width}x{full.Height}（看起来沿用了上一次的 region）");
    }
}
