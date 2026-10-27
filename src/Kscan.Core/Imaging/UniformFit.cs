using Kscan.Core.Model;

namespace Kscan.Core.Imaging;

/// <summary>
/// 图像以「等比缩放 + 居中」（Uniform）方式放进显示容器时的坐标映射。
/// 界面用它把图像像素坐标（引擎返回的码角点）换算成控件坐标；
/// 单独抽出来是因为 <c>Image.Stretch=Uniform</c> 的实际几何只能自己算，
/// 而算错了框就会整体偏移（本项目在自绘选区窗口上踩过一次类似的坑）。
/// </summary>
public readonly record struct UniformFit(double Scale, double OffsetX, double OffsetY)
{
    /// <summary>无法映射（任一边长非正 / 非有限值）时返回 Scale = 0 的空映射。</summary>
    public static UniformFit Create(double imageWidth, double imageHeight, double boxWidth, double boxHeight)
    {
        if (!IsPositive(imageWidth) || !IsPositive(imageHeight) ||
            !IsPositive(boxWidth) || !IsPositive(boxHeight))
        {
            return new UniformFit(0, 0, 0);
        }

        var scale = Math.Min(boxWidth / imageWidth, boxHeight / imageHeight);
        return new UniformFit(
            scale,
            (boxWidth - (imageWidth * scale)) / 2,
            (boxHeight - (imageHeight * scale)) / 2);
    }

    public bool IsUsable => Scale > 0;

    /// <summary>图像像素坐标 → 容器坐标。</summary>
    public PointD Map(PointD point) => new((point.X * Scale) + OffsetX, (point.Y * Scale) + OffsetY);

    /// <summary>图像像素坐标集合 → 容器坐标集合。</summary>
    public IReadOnlyList<PointD> MapAll(IReadOnlyList<PointD> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        var mapped = new PointD[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            mapped[i] = Map(points[i]);
        }

        return mapped;
    }

    /// <summary>图像里的长度（如线宽）→ 容器里的长度。</summary>
    public double MapLength(double length) => length * Scale;

    private static bool IsPositive(double value) => double.IsFinite(value) && value > 0;
}
