namespace Kscan.Core.Model;

/// <summary>生成条码的参数。</summary>
public sealed record BarcodeGenerationRequest
{
    /// <summary>要编码的内容（UTF-8）。</summary>
    public required string Text { get; init; }

    /// <summary>
    /// ZXing 格式名，取值见 <see cref="BarcodeFormats.Writable"/>，
    /// 例如 <c>QR_CODE</c>、<c>CODE_128</c>、<c>DATA_MATRIX</c>。
    /// </summary>
    public required string Format { get; init; }

    /// <summary>像素宽度；对 2D 码是与 <see cref="Height"/> 一起决定目标尺寸，对 1D 码是整条宽度。</summary>
    public int Width { get; init; } = 320;

    /// <summary>像素高度；0 表示按格式自动（2D 取方形，1D 取 120）。</summary>
    public int Height { get; init; }

    /// <summary>静区宽度（模块数）。</summary>
    public int Margin { get; init; } = 4;

    /// <summary>纠错级别（<c>L</c>/<c>M</c>/<c>Q</c>/<c>H</c>），仅 QR 有效。</summary>
    public string ErrorCorrection { get; init; } = "M";

    /// <summary>1D 码是否只渲染条码、不带下方可读文本。</summary>
    public bool PureBarcode { get; init; } = true;

    /// <summary>前景色（ARGB）。</summary>
    public uint ForegroundArgb { get; init; } = 0xFF000000;

    /// <summary>背景色（ARGB）。</summary>
    public uint BackgroundArgb { get; init; } = 0xFFFFFFFF;

    /// <summary>实际生效的高度（<see cref="Height"/> 为 0 时按格式推断）。</summary>
    public int EffectiveHeight => Height > 0 ? Height : BarcodeFormats.IsTwoDimensional(Format) ? Width : 120;
}
