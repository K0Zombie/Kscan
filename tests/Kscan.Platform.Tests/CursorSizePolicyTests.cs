using Kscan.Platform.Windows;

namespace Kscan.Platform.Tests;

/// <summary>
/// 光标物理尺寸的判定规则（<see cref="CursorSizePolicy"/>）。
/// <para>
/// 这里的数字都有现实依据：默认光标位图 32×32、系统度量 <c>SM_CXCURSOR</c> 也是 32，
/// 但 Windows「辅助功能 → 鼠标指针大小」调大后 <c>CursorBaseSize</c> 会变成 48 / 64 / 80…
/// —— 而 <c>SM_CXCURSOR</c> **不跟着变**。只看系统度量就会把光标画成真实大小的 40%。
/// </para>
/// </summary>
public sealed class CursorSizePolicyTests
{
    /// <summary>默认设置：位图 32、系统 32、基准 32、96 DPI → 32。</summary>
    [Fact]
    public void Default_settings_keep_the_bitmap_size()
    {
        var (width, height) = CursorSizePolicy.Resolve(96, 32, 32, 32, 32, 32);

        Assert.Equal(32, width);
        Assert.Equal(32, height);
    }

    /// <summary>用户把指针调大（CursorBaseSize = 80）：屏幕上是 80，不是 32。</summary>
    [Fact]
    public void Enlarged_pointer_size_from_accessibility_wins()
    {
        var (width, height) = CursorSizePolicy.Resolve(96, 32, 32, 32, 32, 80);

        Assert.Equal(80, width);
        Assert.Equal(80, height);
    }

    /// <summary>调大的指针在 150% 屏上还要再乘 DPI：80 × 1.5 = 120。</summary>
    [Fact]
    public void Enlarged_pointer_size_is_scaled_by_the_monitor_dpi()
    {
        var (width, height) = CursorSizePolicy.Resolve(144, 32, 32, 48, 48, 80);

        Assert.Equal(120, width);
        Assert.Equal(120, height);
    }

    /// <summary>读不到辅助功能基准值（老系统）时退回系统度量（这里已按 DPI 缩放成 48）。</summary>
    [Fact]
    public void Missing_base_size_falls_back_to_system_metrics()
    {
        var (width, height) = CursorSizePolicy.Resolve(144, 32, 32, 48, 48, 0);

        Assert.Equal(48, width);
        Assert.Equal(48, height);
    }

    /// <summary>位图本身比系统度量还大（大号光标方案）：不能缩小。</summary>
    [Fact]
    public void Bitmap_larger_than_metrics_is_never_shrunk()
    {
        var (width, height) = CursorSizePolicy.Resolve(96, 64, 64, 32, 32, 0);

        Assert.Equal(64, width);
        Assert.Equal(64, height);
    }

    /// <summary>没有位图尺寸时以系统度量为准。</summary>
    [Fact]
    public void Zero_bitmap_size_uses_system_metrics()
    {
        var (width, height) = CursorSizePolicy.Resolve(96, 0, 0, 32, 32, 0);

        Assert.Equal(32, width);
        Assert.Equal(32, height);
    }

    /// <summary>注册表被写成离谱的值也不能把 DIB 撑爆。</summary>
    [Fact]
    public void Absurd_sizes_are_clamped()
    {
        var (huge, _) = CursorSizePolicy.Resolve(96, 32, 32, 32, 32, 100_000);
        Assert.Equal(CursorSizePolicy.MaxSize, huge);

        var (tiny, _) = CursorSizePolicy.Resolve(96, 1, 1, 1, 1, 1);
        Assert.Equal(CursorSizePolicy.MinSize, tiny);
    }

    /// <summary>DPI 拿不到（0）时按 100% 处理，不能变成 0 像素。</summary>
    [Fact]
    public void Unknown_dpi_is_treated_as_100_percent()
    {
        var (width, height) = CursorSizePolicy.Resolve(0, 32, 32, 32, 32, 80);

        Assert.Equal(80, width);
        Assert.Equal(80, height);
    }
}
