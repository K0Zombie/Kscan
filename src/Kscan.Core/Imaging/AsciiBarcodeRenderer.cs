using System.Text;
using Kscan.Core.Model;

namespace Kscan.Core.Imaging;

/// <summary>字符画风格。</summary>
public enum AsciiBarcodeStyle
{
    /// <summary>半块字符（<c>█ ▀ ▄</c>），一行承载两行模块，终端里显示比例正确。</summary>
    HalfBlocks,

    /// <summary>整块字符（<c>██</c> / 空格），每模块 2 字符宽。</summary>
    Blocks,

    /// <summary>井号（<c>##</c> / 空格），兼容性最好。</summary>
    Hash,

    /// <summary>用 <see cref="AsciiRenderOptions.OnChar"/> / <see cref="AsciiRenderOptions.OffChar"/> 自定义。</summary>
    Custom,
}

public sealed record AsciiRenderOptions
{
    public AsciiBarcodeStyle Style { get; init; } = AsciiBarcodeStyle.HalfBlocks;

    /// <summary>额外静区（模块数）。0 表示不加（矩阵本身通常已含 ZXing 的静区）。</summary>
    public int Margin { get; init; } = 1;

    /// <summary>自定义风格使用的深色字符。</summary>
    public char OnChar { get; init; } = '█';

    /// <summary>自定义风格使用的浅色字符。</summary>
    public char OffChar { get; init; } = ' ';

    /// <summary>是否反色（浅色终端需要）。</summary>
    public bool Invert { get; init; }

    /// <summary>
    /// 每行重复多少遍。1D 码的模块矩阵只有 1 行高，重复若干遍才够高、扫描器才扫得到。
    /// </summary>
    public int RowRepeat { get; init; } = 1;
}

/// <summary>
/// 把条码渲染成"用等宽字符拼出来的"文本，可直接在终端里显示与扫描，
/// 也可以写进纯文本文件。不依赖任何字体/图形库。
/// </summary>
public static class AsciiBarcodeRenderer
{
    /// <summary>半块字符（上、下都亮）。</summary>
    private const char FullBlock = '█';

    /// <summary>上半块。</summary>
    private const char UpperHalf = '▀';

    /// <summary>下半块。</summary>
    private const char LowerHalf = '▄';

    public static string Render(BarcodeMatrix matrix, AsciiRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        options ??= new AsciiRenderOptions();

        var text = options.Style switch
        {
            AsciiBarcodeStyle.HalfBlocks => RenderHalfBlocks(matrix, options),
            AsciiBarcodeStyle.Blocks => RenderDoubleWidth(matrix, options, FullBlock, ' '),
            AsciiBarcodeStyle.Hash => RenderDoubleWidth(matrix, options, '#', ' '),
            AsciiBarcodeStyle.Custom => RenderSingleWidth(matrix, options),
            _ => RenderHalfBlocks(matrix, options),
        };

        return options.RowRepeat > 1 ? RepeatLines(text, options.RowRepeat) : text;
    }

    /// <summary>把每一行重复若干遍（1D 码只有 1 行模块，重复后才有足够条高供扫描器识别）。</summary>
    private static string RepeatLines(string text, int repeat)
    {
        var builder = new StringBuilder(text.Length * repeat);
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            for (var i = 0; i < repeat; i++)
            {
                builder.Append(line).Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>用两个字符表示一个模块，保持宽高比。</summary>
    private static string RenderDoubleWidth(BarcodeMatrix matrix, AsciiRenderOptions options, char on, char off)
    {
        if (options.Invert)
        {
            (on, off) = (off, on);
        }

        var builder = new StringBuilder();
        ForEachRow(matrix, options.Margin, (x, y, isDark, isLastInRow) =>
        {
            var c = isDark ? on : off;
            builder.Append(c).Append(c);
            if (isLastInRow)
            {
                builder.Append('\n');
            }
        });
        return builder.ToString();
    }

    /// <summary>一个模块一个字符（自定义字符集）。</summary>
    private static string RenderSingleWidth(BarcodeMatrix matrix, AsciiRenderOptions options)
    {
        var on = options.Invert ? options.OffChar : options.OnChar;
        var off = options.Invert ? options.OnChar : options.OffChar;

        var builder = new StringBuilder();
        ForEachRow(matrix, options.Margin, (x, y, isDark, isLastInRow) =>
        {
            builder.Append(isDark ? on : off);
            if (isLastInRow)
            {
                builder.Append('\n');
            }
        });
        return builder.ToString();
    }

    /// <summary>每行承载两行模块：上/下模块的四种组合映射到 <c>█ ▀ ▄</c> 与空格。</summary>
    private static string RenderHalfBlocks(BarcodeMatrix matrix, AsciiRenderOptions options)
    {
        var margin = Math.Max(0, options.Margin);
        var totalWidth = matrix.Width + (margin * 2);
        var totalHeight = matrix.Height + (margin * 2);
        var builder = new StringBuilder();

        for (var y = 0; y < totalHeight; y += 2)
        {
            for (var x = 0; x < totalWidth; x++)
            {
                var top = IsDark(matrix, options, x - margin, y - margin);
                var bottom = y + 1 < totalHeight && IsDark(matrix, options, x - margin, y + 1 - margin);
                builder.Append((top, bottom) switch
                {
                    (true, true) => FullBlock,
                    (true, false) => UpperHalf,
                    (false, true) => LowerHalf,
                    _ => ' ',
                });
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static bool IsDark(BarcodeMatrix matrix, AsciiRenderOptions options, int x, int y) =>
        options.Invert ? !matrix[x, y] : matrix[x, y];

    private static void ForEachRow(BarcodeMatrix matrix, int margin, Action<int, int, bool, bool> emit)
    {
        margin = Math.Max(0, margin);
        var totalWidth = matrix.Width + (margin * 2);
        var totalHeight = matrix.Height + (margin * 2);

        for (var y = 0; y < totalHeight; y++)
        {
            for (var x = 0; x < totalWidth; x++)
            {
                emit(x, y, matrix[x - margin, y - margin], x == totalWidth - 1);
            }
        }
    }
}
