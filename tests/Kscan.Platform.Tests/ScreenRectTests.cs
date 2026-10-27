using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Tests;

/// <summary>屏幕矩形的几何判定（遮挡检测靠 <see cref="ScreenRect.IntersectsWith"/>）。</summary>
public sealed class ScreenRectTests
{
    [Fact]
    public void Overlapping_rects_intersect()
    {
        var target = new ScreenRect(100, 100, 200, 100);

        Assert.True(target.IntersectsWith(new ScreenRect(150, 120, 100, 100)));
        Assert.True(target.IntersectsWith(new ScreenRect(50, 50, 100, 100)));
        Assert.True(new ScreenRect(50, 50, 100, 100).IntersectsWith(target));
    }

    [Fact]
    public void Rect_fully_inside_another_intersects()
    {
        var target = new ScreenRect(0, 0, 1000, 800);

        Assert.True(target.IntersectsWith(new ScreenRect(10, 10, 20, 20)));
    }

    [Fact]
    public void Edge_touching_does_not_count_as_intersection()
    {
        var target = new ScreenRect(0, 0, 100, 100);

        // 只挨着右边 / 下边
        Assert.False(target.IntersectsWith(new ScreenRect(100, 0, 50, 50)));
        Assert.False(target.IntersectsWith(new ScreenRect(0, 100, 50, 50)));
    }

    [Fact]
    public void Disjoint_rects_do_not_intersect()
    {
        var target = new ScreenRect(0, 0, 100, 100);

        Assert.False(target.IntersectsWith(new ScreenRect(500, 500, 50, 50)));
    }

    [Fact]
    public void Minimized_window_offscreen_rect_does_not_intersect_visible_window()
    {
        // 最小化窗口的矩形是 (-32000,-32000,160,28)，不能算成"挡住"了别的窗口
        var minimized = new ScreenRect(-32000, -32000, 160, 28);
        var normal = new ScreenRect(100, 100, 800, 600);

        Assert.False(minimized.IntersectsWith(normal));
        Assert.False(normal.IntersectsWith(minimized));
    }

    [Fact]
    public void Inflate_grows_the_rect_by_padding_on_every_side()
    {
        var rect = new ScreenRect(100, 100, 200, 100);
        var grown = rect.Inflate(2);

        Assert.NotNull(grown);
        Assert.Equal(new ScreenRect(98, 98, 204, 104), grown!.Value);
    }

    [Fact]
    public void Inflate_with_negative_padding_shrinks_the_rect()
    {
        var rect = new ScreenRect(100, 100, 200, 100);
        var shrunk = rect.Inflate(-10);

        Assert.NotNull(shrunk);
        Assert.Equal(new ScreenRect(110, 110, 180, 80), shrunk!.Value);
    }

    [Fact]
    public void Inflate_returns_null_when_the_rect_shrinks_away()
    {
        var rect = new ScreenRect(0, 0, 10, 10);

        Assert.Null(rect.Inflate(-5));
        Assert.Null(rect.Inflate(-20));
    }

    [Fact]
    public void Inflate_with_zero_padding_keeps_the_rect()
    {
        var rect = new ScreenRect(1, 2, 3, 4);

        Assert.Equal(rect, rect.Inflate(0));
    }
}
