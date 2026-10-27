namespace Kscan.Core.Imaging;

/// <summary>
/// 与 UI/图形库无关的条码模块矩阵。<c>true</c> 表示深色（条/点）模块。
/// 生成路径上的所有渲染（位图 / SVG / 字符画）都从它出发，
/// 因此不需要把 ZXing 的 <c>BitMatrix</c> 泄漏到公共 API。
/// </summary>
public sealed class BarcodeMatrix
{
    private readonly bool[] _modules;

    public BarcodeMatrix(int width, int height, bool[] modules)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(modules);

        if (modules.Length != width * height)
        {
            throw new ArgumentException($"模块数组长度应为 {width * height}，实际 {modules.Length}。", nameof(modules));
        }

        Width = width;
        Height = height;
        _modules = modules;
    }

    /// <summary>模块数（宽）。</summary>
    public int Width { get; }

    /// <summary>模块数（高）。</summary>
    public int Height { get; }

    /// <summary>取某个模块；越界返回 <c>false</c>（当作静区，方便渲染时统一处理边界）。</summary>
    public bool this[int x, int y] =>
        x >= 0 && x < Width && y >= 0 && y < Height && _modules[(y * Width) + x];

    /// <summary>带上静区的总模块尺寸。</summary>
    public (int Width, int Height) WithMargin(int margin) => (Width + (margin * 2), Height + (margin * 2));
}
