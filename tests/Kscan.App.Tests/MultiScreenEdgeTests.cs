using Avalonia;
using Kscan.App.Views;

namespace Kscan.App.Tests;

/// <summary>
/// 跨屏选区边框的判定：**两屏接缝处不画线**。
/// <para>
/// 一块屏只画选区落在自己这块屏里的那部分，如果用四边框，被屏幕边界裁断的地方也会画线，
/// 接缝上就多出内部竖线。这里的规则是"只有选区的真实边才画"。
/// </para>
/// </summary>
public sealed class MultiScreenEdgeTests
{
    // 用户的实际布局：主屏 (0,0,1920,1080)@100%，副屏在左边且更高 (-2560,-365,2560,1440)
    private static readonly PixelRect Primary = new(0, 0, 1920, 1080);

    private static readonly PixelRect Secondary = new(-2560, -365, 2560, 1440);

    private static readonly PixelRect Desktop = new(-2560, -365, 4480, 1445);

    [Fact]
    public void Whole_desktop_draws_only_the_real_outer_edges()
    {
        // 副屏更高更宽：桌面的上边、左边在副屏上；下边、右边在主屏上
        Assert.True(Should(Secondary, SelectionEdge.Top));
        Assert.True(Should(Secondary, SelectionEdge.Left));
        Assert.False(Should(Secondary, SelectionEdge.Bottom));

        // 接缝（副屏右边 / 主屏左边）不画
        Assert.False(Should(Secondary, SelectionEdge.Right));

        Assert.False(Should(Primary, SelectionEdge.Left));
        Assert.True(Should(Primary, SelectionEdge.Right));

        // 主屏的上边不是桌面的上边（副屏更高）→ 不画；下边就是桌面的下边 → 画
        Assert.False(Should(Primary, SelectionEdge.Top));
        Assert.True(Should(Primary, SelectionEdge.Bottom));
    }

    [Fact]
    public void Selection_inside_one_screen_draws_all_four_edges()
    {
        var selection = new PixelRect(300, 200, 400, 300);

        foreach (var edge in Enum.GetValues<SelectionEdge>())
        {
            Assert.True(Should(Primary, edge, selection), $"{edge} 应该画");
        }
    }

    [Fact]
    public void Window_spanning_both_screens_has_no_seam_line()
    {
        // 窗口从副屏伸到主屏，右边在主屏内 → 两块屏上都不该出现接缝线
        var window = new PixelRect(-1000, 100, 2900, 600);

        Assert.False(Should(Secondary, SelectionEdge.Right, window), "副屏右边被裁断，不该画线");
        Assert.False(Should(Primary, SelectionEdge.Left, window), "主屏左边被裁断，不该画线");

        // 但窗口自己的上/下边在两块屏上都要各画一段
        Assert.True(Should(Secondary, SelectionEdge.Top, window));
        Assert.True(Should(Primary, SelectionEdge.Top, window));
        Assert.True(Should(Secondary, SelectionEdge.Bottom, window));
        Assert.True(Should(Primary, SelectionEdge.Bottom, window));
    }

    [Fact]
    public void Tolerance_accepts_one_or_two_pixel_boundary_drift()
    {
        // 混合 DPI 下屏边界可能差 1~2 像素：不能因此把真实边判成假边
        var drifted = new PixelRect(-2561, -365, 4481, 1445);

        Assert.True(Should(Secondary, SelectionEdge.Left, drifted));
        Assert.True(SelectionEdgeRules.ShouldDraw(drifted, Primary, SelectionEdge.Right, tolerance: 2));
    }

    private static bool Should(PixelRect screen, SelectionEdge edge, PixelRect? selection = null) =>
        SelectionEdgeRules.ShouldDraw(selection ?? Desktop, screen, edge);
}
