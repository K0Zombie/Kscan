using Kscan.Platform.Abstractions;
using Kscan.Platform.Windows;

namespace Kscan.Platform.Tests;

/// <summary>
/// UIA 逐层下降时"选哪个子元素"的纯逻辑：包含光标点的候选里取面积最小的。
/// 这里只测纯函数，不碰 COM（COM 部分只能实机验证）。
/// </summary>
public sealed class WindowsUiAutomationTests
{
    private static ScreenRect Rect(int x, int y, int width, int height) => new(x, y, width, height);

    [Fact]
    public void Picks_the_smallest_child_containing_the_point()
    {
        var rects = new[]
        {
            Rect(0, 0, 800, 600),      // 窗口
            Rect(100, 100, 300, 200),  // 面板
            Rect(120, 120, 60, 30),    // 按钮（最小）
        };

        Assert.Equal(2, WindowsUiAutomation.PickSmallestContaining(rects, 130, 130));
    }

    [Fact]
    public void Ignores_children_that_do_not_contain_the_point()
    {
        var rects = new[]
        {
            Rect(100, 100, 300, 200),
            Rect(500, 500, 40, 40),   // 不含点，虽然更小
        };

        Assert.Equal(0, WindowsUiAutomation.PickSmallestContaining(rects, 110, 110));
    }

    [Fact]
    public void Returns_minus_one_when_no_child_contains_the_point()
    {
        var rects = new[] { Rect(0, 0, 10, 10), Rect(20, 20, 10, 10) };

        Assert.Equal(-1, WindowsUiAutomation.PickSmallestContaining(rects, 100, 100));
        Assert.Equal(-1, WindowsUiAutomation.PickSmallestContaining([], 0, 0));
    }

    [Fact]
    public void Later_child_wins_when_areas_are_equal()
    {
        // 同一层里两块同大的区域叠在一起（例如两个同尺寸的按钮 / 浮层），后者通常在上
        var rects = new[] { Rect(10, 10, 50, 20), Rect(10, 10, 50, 20) };

        Assert.Equal(1, WindowsUiAutomation.PickSmallestContaining(rects, 20, 15));
    }

    [Fact]
    public void Point_on_the_border_counts_as_inside_only_on_the_top_left_edge()
    {
        var rects = new[] { Rect(10, 10, 50, 20) };

        // ScreenRect.Contains 是左闭右开：右边 / 下边不算
        Assert.Equal(0, WindowsUiAutomation.PickSmallestContaining(rects, 10, 10));
        Assert.Equal(-1, WindowsUiAutomation.PickSmallestContaining(rects, 60, 15));
        Assert.Equal(-1, WindowsUiAutomation.PickSmallestContaining(rects, 20, 30));
    }
}
