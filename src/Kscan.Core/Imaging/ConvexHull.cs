using Kscan.Core.Model;

namespace Kscan.Core.Imaging;

/// <summary>
/// 点集凸包（Andrew 单调链）。用来把引擎给出的码角点围成一个多边形供界面高亮。
/// 纯几何、与 UI 框架无关，因此可以在无 GUI 环境里测试。
/// </summary>
public static class ConvexHull
{
    /// <summary>
    /// 计算凸包顶点：无重复点、不含共线的中间点。
    /// 输入不足 3 个不同点时原样返回（去重后）——1D 码常常只给 2 个端点，
    /// 此时调用方按线段处理，而不是多边形。
    /// </summary>
    public static IReadOnlyList<PointD> Compute(IReadOnlyList<PointD> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        if (sorted.Length < 3)
        {
            return sorted;
        }

        var lower = new List<PointD>(sorted.Length);
        foreach (var point in sorted)
        {
            Push(lower, point);
        }

        var upper = new List<PointD>(sorted.Length);
        for (var i = sorted.Length - 1; i >= 0; i--)
        {
            Push(upper, sorted[i]);
        }

        // 两段的端点都重复了一次（最左/最右点），各去掉末位再拼接
        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);

        // 全部点共线时退化成两个端点，交给调用方按线段画（而不是当成多边形）
        return lower;
    }

    /// <summary>凸包上是否有足够围成多边形的点（否则只能画线段）。</summary>
    public static bool IsPolygon(IReadOnlyList<PointD> hull) =>
        hull is { Count: >= 3 };

    private static void Push(List<PointD> hull, PointD point)
    {
        // 叉积 <= 0：右转或共线 → 栈顶点不在凸包上
        while (hull.Count >= 2 && Cross(hull[^2], hull[^1], point) <= 0)
        {
            hull.RemoveAt(hull.Count - 1);
        }

        hull.Add(point);
    }

    private static double Cross(PointD o, PointD a, PointD b) =>
        ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));
}
