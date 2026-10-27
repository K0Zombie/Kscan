using System.Globalization;
using System.Text;
using Kscan.Core.Model;

namespace Kscan.Core.Imaging;

/// <summary>
/// 把 <see cref="BarcodeMatrix"/> 渲染成 PNG / SVG / 字符画。
/// <para>
/// 缩放一律取**整数倍**：非整数倍会让模块宽窄不一，直接就扫不出来。
/// 因此实际输出尺寸可能略大于请求值（宁可大一点，不可模糊）。
/// </para>
/// </summary>
public static class BarcodeRenderer
{
    /// <summary>渲染成像素帧（BGRA32）。</summary>
    public static ImageFrame ToImageFrame(BarcodeMatrix matrix, BarcodeGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(request);

        var (totalWidth, totalHeight, scale) = Layout(matrix, request);
        var pixelWidth = totalWidth * scale;
        var pixelHeight = totalHeight * scale;

        var frame = new ImageFrame(pixelWidth, pixelHeight, new byte[pixelWidth * pixelHeight * ImageFrame.BytesPerPixel]);
        FillRect(frame, 0, 0, pixelWidth, pixelHeight, request.BackgroundArgb);

        var margin = Math.Max(0, request.Margin);
        for (var y = 0; y < matrix.Height; y++)
        {
            for (var x = 0; x < matrix.Width; x++)
            {
                if (matrix[x, y])
                {
                    FillRect(frame, (x + margin) * scale, (y + margin) * scale, scale, scale, request.ForegroundArgb);
                }
            }
        }

        return frame;
    }

    public static byte[] ToPng(BarcodeMatrix matrix, BarcodeGenerationRequest request) =>
        SkiaImageCodec.EncodePng(ToImageFrame(matrix, request));

    public static void SavePng(BarcodeMatrix matrix, BarcodeGenerationRequest request, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllBytes(path, ToPng(matrix, request));
    }

    /// <summary>渲染成 SVG（矢量，印刷与缩放不失真）。</summary>
    public static string ToSvg(BarcodeMatrix matrix, BarcodeGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(request);

        var (totalWidth, totalHeight, scale) = Layout(matrix, request);
        var margin = Math.Max(0, request.Margin);

        var builder = new StringBuilder();

        // 显式写 '\n'：生成的 SVG 是跨平台产物，不应随宿主平台变成 \r\n
        builder.Append("""<?xml version="1.0" encoding="UTF-8"?>""").Append('\n');
        builder.Append(FormattableString.Invariant(
            $"""<svg xmlns="http://www.w3.org/2000/svg" width="{totalWidth * scale}" height="{totalHeight * scale}" viewBox="0 0 {totalWidth} {totalHeight}" shape-rendering="crispEdges">""")).Append('\n');
        builder.Append(FormattableString.Invariant(
            $"""  <rect width="{totalWidth}" height="{totalHeight}" fill="{ToSvgColor(request.BackgroundArgb)}"/>""")).Append('\n');

        // 同一行里连续模块合并成一条子路径，体积比逐模块 rect 小很多
        var path = new StringBuilder();
        for (var y = 0; y < matrix.Height; y++)
        {
            var x = 0;
            while (x < matrix.Width)
            {
                if (!matrix[x, y])
                {
                    x++;
                    continue;
                }

                var start = x;
                while (x < matrix.Width && matrix[x, y])
                {
                    x++;
                }

                var length = x - start;
                path.Append(CultureInfo.InvariantCulture, $"M{start + margin} {y + margin}h{length}v1h-{length}z");
            }
        }

        builder.Append(FormattableString.Invariant($"""  <path d="{path}" fill="{ToSvgColor(request.ForegroundArgb)}"/>""")).Append('\n');
        builder.Append("</svg>").Append('\n');
        return builder.ToString();
    }

    /// <summary>渲染成等宽字符画（可直接终端显示/扫描）。</summary>
    public static string ToAscii(BarcodeMatrix matrix, AsciiRenderOptions? options = null) =>
        AsciiBarcodeRenderer.Render(matrix, options);

    private static (int TotalWidth, int TotalHeight, int Scale) Layout(BarcodeMatrix matrix, BarcodeGenerationRequest request)
    {
        var margin = Math.Max(0, request.Margin);
        var totalWidth = matrix.Width + (margin * 2);
        var totalHeight = matrix.Height + (margin * 2);

        var scale = Math.Max(1, Math.Min(
            Math.Max(1, request.Width) / totalWidth,
            Math.Max(1, request.EffectiveHeight) / totalHeight));

        return (totalWidth, totalHeight, scale);
    }

    private static void FillRect(ImageFrame frame, int x0, int y0, int width, int height, uint argb)
    {
        var b = (byte)(argb & 0xFF);
        var g = (byte)((argb >> 8) & 0xFF);
        var r = (byte)((argb >> 16) & 0xFF);
        var a = (byte)((argb >> 24) & 0xFF);

        var maxY = Math.Min(y0 + height, frame.Height);
        var maxX = Math.Min(x0 + width, frame.Width);

        for (var y = Math.Max(0, y0); y < maxY; y++)
        {
            var rowOffset = y * frame.Width;
            for (var x = Math.Max(0, x0); x < maxX; x++)
            {
                var i = (rowOffset + x) * ImageFrame.BytesPerPixel;
                frame.Pixels[i] = b;
                frame.Pixels[i + 1] = g;
                frame.Pixels[i + 2] = r;
                frame.Pixels[i + 3] = a;
            }
        }
    }

    private static string ToSvgColor(uint argb)
    {
        var a = (argb >> 24) & 0xFF;
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;

        return a == 0xFF
            ? $"#{r:X2}{g:X2}{b:X2}"
            : $"rgba({r},{g},{b},{a / 255.0:F3})";
    }
}
