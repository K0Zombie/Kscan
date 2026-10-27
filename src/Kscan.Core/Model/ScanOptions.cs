namespace Kscan.Core.Model;

/// <summary>识别参数。</summary>
public sealed record ScanOptions
{
    public static ScanOptions Default { get; } = new();

    /// <summary>是否尝试识别多个码（图中有多个二维码时）。</summary>
    public bool MultiCode { get; init; } = true;

    /// <summary>是否启用"更努力地尝试"（提升识别率，牺牲速度）。</summary>
    public bool TryHarder { get; init; } = true;

    /// <summary>是否尝试反色码（浅色码深色底）。</summary>
    public bool TryInverted { get; init; }

    /// <summary>
    /// 原图读不出来时，是否启用预处理回退（彩色叠加修复、低对比度增强、中值去噪与局部阈值）。
    /// 默认开：实测 1px 的亮色框线就能横穿二维码的定位图形、让它彻底读不出来。
    /// 只在原图失败后才跑，所以正常情况下不增加耗时。
    /// </summary>
    public bool TryDenoise { get; init; } = true;

    /// <summary>是否对内容相同的码去重。</summary>
    public bool IgnoreDuplicates { get; init; } = true;

    /// <summary>限制只识别这些格式（ZXing 格式名，如 <c>QR_CODE</c>）；<c>null</c> 表示不限制。</summary>
    public IReadOnlyList<string>? RestrictFormats { get; init; }
}
