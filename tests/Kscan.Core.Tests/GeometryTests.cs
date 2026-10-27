using Kscan.Core.Imaging;
using Kscan.Core.Model;

namespace Kscan.Core.Tests;

/// <summary>结果面板多码高亮依赖的两块几何：凸包与 Uniform 适配映射。</summary>
public class GeometryTests
{
    // ---------- ConvexHull ----------

    [Fact]
    public void Hull_stays_empty_for_empty_input()
    {
        Assert.Empty(ConvexHull.Compute([]));
    }

    [Fact]
    public void Hull_keeps_two_points_for_1d_codes()
    {
        PointD[] line = [new(10, 10), new(90, 30)];

        var hull = ConvexHull.Compute(line);

        Assert.Equal(2, hull.Count);
        Assert.False(ConvexHull.IsPolygon(hull));
    }

    [Fact]
    public void Hull_drops_the_interior_point_of_a_square()
    {
        PointD[] points = [new(0, 0), new(100, 0), new(100, 100), new(0, 100), new(50, 50)];

        var hull = ConvexHull.Compute(points);

        Assert.Equal(4, hull.Count);
        Assert.DoesNotContain(new PointD(50, 50), hull);
        Assert.True(ConvexHull.IsPolygon(hull));
    }

    [Fact]
    public void Hull_skips_collinear_points_on_the_edge()
    {
        // 正方形每条边中间都插一个点：凸包仍应只有 4 个顶点
        PointD[] points =
        [
            new(0, 0), new(50, 0), new(100, 0),
            new(100, 50), new(100, 100),
            new(50, 100), new(0, 100),
            new(0, 50),
        ];

        var hull = ConvexHull.Compute(points);

        Assert.Equal(4, hull.Count);
        Assert.Equal(
            [new PointD(0, 0), new PointD(0, 100), new PointD(100, 0), new PointD(100, 100)],
            hull.OrderBy(p => p.X).ThenBy(p => p.Y));
    }

    [Fact]
    public void Hull_of_three_collinear_points_is_not_a_polygon()
    {
        PointD[] points = [new(0, 0), new(10, 0), new(20, 0)];

        var hull = ConvexHull.Compute(points);

        // 共线 → 只剩两个端点（1D 码就靠这个走"画线段"的分支）
        Assert.Equal([new PointD(0, 0), new PointD(20, 0)], hull);
        Assert.False(ConvexHull.IsPolygon(hull));
    }

    [Fact]
    public void Hull_deduplicates_repeated_corners()
    {
        // QR 的角点常带重复（同一角在 4 个点上出现两次）
        PointD[] points = [new(0, 0), new(0, 0), new(0, 40), new(40, 40), new(40, 0), new(40, 0)];

        var hull = ConvexHull.Compute(points);

        Assert.Equal(4, hull.Count);
    }

    [Fact]
    public void Hull_is_built_from_input_points_only()
    {
        PointD[] points =
        [
            new(0, 0), new(60, 5), new(120, 0), new(118, 70), new(55, 64), new(0, 68), new(30, 30),
        ];

        var hull = ConvexHull.Compute(points);

        // (60,5) 与 (30,30) 都落在四边形内部 → 凸包只有 4 个顶点，且全部来自输入
        Assert.Equal(4, hull.Count);
        Assert.All(hull, p => Assert.Contains(p, points));
        Assert.DoesNotContain(new PointD(30, 30), hull);
        Assert.DoesNotContain(new PointD(60, 5), hull);
    }

    // ---------- UniformFit ----------

    [Fact]
    public void Fit_uses_the_tighter_axis_for_a_wide_image()
    {
        // 1000×500 图放进 500×500 容器：按宽度缩放 → 0.5，上下各留 125 空白
        var fit = UniformFit.Create(1000, 500, 500, 500);

        Assert.Equal(0.5, fit.Scale, 10);
        Assert.Equal(0, fit.OffsetX, 10);
        Assert.Equal(125, fit.OffsetY, 10);
        Assert.Equal(new PointD(500, 375), fit.Map(new PointD(1000, 500)));
    }

    [Fact]
    public void Fit_uses_the_tighter_axis_for_a_tall_image()
    {
        // 500×1000 图放进 500×500 容器：按高度缩放 → 0.5，左右各留 125 空白
        var fit = UniformFit.Create(500, 1000, 500, 500);

        Assert.Equal(0.5, fit.Scale, 10);
        Assert.Equal(125, fit.OffsetX, 10);
        Assert.Equal(0, fit.OffsetY, 10);
        Assert.Equal(new PointD(375, 500), fit.Map(new PointD(500, 1000)));
    }

    [Fact]
    public void Fit_maps_the_image_origin_to_the_letterbox_offset()
    {
        var fit = UniformFit.Create(200, 100, 400, 400);

        Assert.Equal(new PointD(0, 100), fit.Map(new PointD(0, 0)));
    }

    [Fact]
    public void Fit_maps_all_points_and_lengths()
    {
        var fit = UniformFit.Create(100, 100, 200, 200);

        var mapped = fit.MapAll([new PointD(10, 20), new PointD(30, 40)]);

        Assert.Equal(new PointD(20, 40), mapped[0]);
        Assert.Equal(new PointD(60, 80), mapped[1]);
        Assert.Equal(6, fit.MapLength(3), 10);
    }

    [Theory]
    [InlineData(0, 100, 200, 200)]
    [InlineData(100, 0, 200, 200)]
    [InlineData(100, 100, 0, 200)]
    [InlineData(100, 100, 200, 0)]
    [InlineData(double.NaN, 100, 200, 200)]
    public void Fit_degrades_to_empty_for_unusable_sizes(double iw, double ih, double bw, double bh)
    {
        var fit = UniformFit.Create(iw, ih, bw, bh);

        Assert.False(fit.IsUsable);
        Assert.Equal(new PointD(0, 0), fit.Map(new PointD(42, 42)));
    }
}
