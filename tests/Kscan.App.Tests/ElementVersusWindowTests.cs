using Kscan.App.Services;
using Kscan.Platform.Abstractions;

namespace Kscan.App.Tests;

/// <summary>
/// 框选遮罩里的"这到底是窗口还是元素"判断。
/// <para>
/// 回归背景：UIA 给窗口本身的那个元素矩形，通常正好是 <c>GetWindowRect</c>（比 DWM 可见边框大
/// 7~8px）。原来只容忍 3px，于是它没被当成"窗口"过滤掉，用户看到的明明只是**一个窗口高亮**，
/// 单击却走了"元素截图"（屏幕裁剪）—— 被别的窗口挡住的部分拍到的是挡住它的那个窗口。
/// 按住 Ctrl 会忽略元素、退回整窗渲染，所以同一位置 Ctrl+单击反而正常。
/// </para>
/// </summary>
public sealed class ElementVersusWindowTests
{
    /// <summary>完全一样：是窗口。</summary>
    [Fact]
    public void Same_rect_is_a_window() =>
        Assert.True(RegionSelectorWindow.NearlySameWindow(
            new ScreenRect(100, 100, 800, 600),
            new ScreenRect(100, 100, 800, 600)));

    /// <summary>UIA 给的是 GetWindowRect：比可见边框左/右/下各多 7px（800×600 的窗口容差 12px）。</summary>
    [Fact]
    public void Uia_window_rect_with_invisible_border_still_counts_as_window() =>
        Assert.True(RegionSelectorWindow.NearlySameWindow(
            new ScreenRect(93, 100, 814, 607),
            new ScreenRect(100, 100, 800, 600)));

    /// <summary>只把"标题栏以内的内容区"当窗口是不对的：那确实是更小的区域，应该能单独截。</summary>
    [Fact]
    public void Client_area_without_the_title_bar_is_not_a_window() =>
        Assert.False(RegionSelectorWindow.NearlySameWindow(
            new ScreenRect(108, 140, 784, 552),
            new ScreenRect(100, 100, 800, 600)));

    /// <summary>真正的子元素（明显更小 / 位置不同）不能被误判成窗口。</summary>
    [Theory]
    [InlineData(300, 250, 200, 80)]     // 一个按钮
    [InlineData(100, 100, 400, 600)]    // 左半边
    [InlineData(600, 400, 300, 200)]    // 右下角一块
    public void Smaller_elements_are_not_a_window(int x, int y, int width, int height) =>
        Assert.False(RegionSelectorWindow.NearlySameWindow(
            new ScreenRect(x, y, width, height),
            new ScreenRect(100, 100, 800, 600)));

    /// <summary>容差有上下限：小窗口 4px、大窗口最多 16px。</summary>
    [Fact]
    public void Tolerance_is_clamped()
    {
        // 20×20 的小窗口：7px 的差超出容差（4px）→ 不算窗口
        Assert.False(RegionSelectorWindow.NearlySameWindow(
            new ScreenRect(0, 0, 27, 27),
            new ScreenRect(0, 0, 20, 20)));

        // 2000×2000 的大窗口：上下限 16px，四边各差 7px 仍算窗口
        Assert.True(RegionSelectorWindow.NearlySameWindow(
            new ScreenRect(-7, 0, 2014, 2007),
            new ScreenRect(0, 0, 2000, 2000)));
    }
}
