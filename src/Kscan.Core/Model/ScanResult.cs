namespace Kscan.Core.Model;

/// <summary>一次成功识别出的码。</summary>
public sealed record ScanResult
{
    /// <summary>码的内容（已按 ZXing 的字符集处理结果解码）。</summary>
    public required string Text { get; init; }

    /// <summary>码的格式名，如 <c>QR_CODE</c>、<c>CODE_128</c>。</summary>
    public required string Format { get; init; }

    /// <summary>码在图像中的角点（像素坐标），可能为空（引擎不提供定位时）。</summary>
    public IReadOnlyList<PointD> Points { get; init; } = [];

    public bool HasLocation => Points.Count > 0;

    /// <summary>角点的几何中心；无定位信息时为 <c>null</c>。</summary>
    public PointD? Center => Points.Count switch
    {
        0 => null,
        1 => Points[0],
        2 => new PointD((Points[0].X + Points[1].X) / 2, (Points[0].Y + Points[1].Y) / 2),
        _ => new PointD(Points.Average(p => p.X), Points.Average(p => p.Y)),
    };

    /// <summary>产出该结果的引擎 Id。</summary>
    public string EngineId { get; init; } = "";

    /// <summary>该结果所属引擎本次识别的耗时（毫秒）。</summary>
    public long ElapsedMs { get; init; }
}
