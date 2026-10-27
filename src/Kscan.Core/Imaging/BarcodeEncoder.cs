using Kscan.Core.Model;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace Kscan.Core.Imaging;

/// <summary>
/// 把文本编码成条码模块矩阵。支持 ZXing.Net 中**存在 writer 实现**的全部码制
/// （见 <see cref="BarcodeFormats.Writable"/>）：4 种二维 + 11 种一维。
/// </summary>
public static class BarcodeEncoder
{
    /// <summary>把格式名解析为 ZXing 的 <see cref="BarcodeFormat"/>。</summary>
    public static bool TryParseFormat(string? name, out BarcodeFormat format)
    {
        format = default;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = name.Trim().Replace('-', '_').ToUpperInvariant();
        return Enum.TryParse(normalized, ignoreCase: true, out format)
            && BarcodeFormats.IsWritable(normalized);
    }

    /// <summary>编码。内容与格式不匹配（例如 EAN-13 传了字母）时抛 <see cref="InvalidOperationException"/>。</summary>
    public static BarcodeMatrix Encode(BarcodeGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.Text);

        if (!TryParseFormat(request.Format, out var format))
        {
            throw new ArgumentException(
                $"不支持生成的码制：{request.Format}。可用：{string.Join(", ", BarcodeFormats.Writable)}", nameof(request));
        }

        var options = CreateOptions(request, format);

        try
        {
            var matrix = new MultiFormatWriter().encode(
                request.Text, format, request.Width, request.EffectiveHeight, options.Hints);
            return ToMatrix(matrix);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            throw new InvalidOperationException($"生成 {format} 失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 编码成**模块级**矩阵（每个模块占 1 个单位），用于等宽字符画这类需要紧凑输出的场景。
    /// <para>
    /// <see cref="Encode"/> 返回的是按请求像素尺寸渲染过的矩阵（适合再整数倍放大成图片）；
    /// 若直接拿去画字符画，会得到"一屏放不下"的结果，所以这里单独走一条最小尺寸路径。
    /// 静区仍由 <see cref="BarcodeGenerationRequest.Margin"/> 控制。
    /// </para>
    /// </summary>
    public static BarcodeMatrix EncodeModules(BarcodeGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.Text);

        if (!TryParseFormat(request.Format, out var format))
        {
            throw new ArgumentException(
                $"不支持生成的码制：{request.Format}。可用：{string.Join(", ", BarcodeFormats.Writable)}", nameof(request));
        }

        var options = CreateOptions(request, format);

        try
        {
            // width/height 都给 1：ZXing 会退回到模块级输出（不再放大），静区仍按 hints 生效
            var matrix = new MultiFormatWriter().encode(request.Text, format, 1, 1, options.Hints);
            return ToMatrix(matrix);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            throw new InvalidOperationException($"生成 {format} 失败：{ex.Message}", ex);
        }
    }

    /// <summary>应用内容级的合法性预检（在生成前给出更友好的中文提示）。</summary>
    public static string? ValidateContent(string format, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "内容不能为空。";
        }

        var normalized = format.Trim().Replace('-', '_').ToUpperInvariant();
        var digitsOnly = normalized switch
        {
            "EAN_8" => 8,
            "EAN_13" => 13,
            "UPC_A" => 12,
            "UPC_E" => 8,
            _ => 0,
        };

        if (digitsOnly > 0 && !text.All(char.IsAsciiDigit))
        {
            return $"{BarcodeFormats.DisplayName(normalized)} 的内容只能是数字。";
        }

        if (normalized == "EAN_8" && text.Length is not (7 or 8))
        {
            return "EAN-8 需要 7 位（校验位自动计算）或 8 位数字。";
        }

        if (normalized == "EAN_13" && text.Length is not (12 or 13))
        {
            return "EAN-13 需要 12 位（校验位自动计算）或 13 位数字。";
        }

        if (normalized == "UPC_A" && text.Length is not (11 or 12))
        {
            return "UPC-A 需要 11 位（校验位自动计算）或 12 位数字。";
        }

        if (normalized == "ITF" && (text.Length % 2 != 0 || !text.All(char.IsAsciiDigit)))
        {
            return "ITF 需要偶数位数字。";
        }

        return null;
    }

    private static EncodingOptions CreateOptions(BarcodeGenerationRequest request, BarcodeFormat format)
    {
        if (format == BarcodeFormat.QR_CODE)
        {
            var qr = new QrCodeEncodingOptions
            {
                Width = request.Width,
                Height = request.EffectiveHeight,
                Margin = request.Margin,
                ErrorCorrection = ParseErrorCorrection(request.ErrorCorrection),
                CharacterSet = "UTF-8",
            };
            return qr;
        }

        var options = new EncodingOptions
        {
            Width = request.Width,
            Height = request.EffectiveHeight,
            Margin = request.Margin,
            PureBarcode = request.PureBarcode,
        };
        options.Hints[EncodeHintType.CHARACTER_SET] = "UTF-8";
        return options;
    }

    private static ErrorCorrectionLevel ParseErrorCorrection(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "L" => ErrorCorrectionLevel.L,
        "Q" => ErrorCorrectionLevel.Q,
        "H" => ErrorCorrectionLevel.H,
        _ => ErrorCorrectionLevel.M,
    };

    private static BarcodeMatrix ToMatrix(BitMatrix source)
    {
        var modules = new bool[source.Width * source.Height];
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                modules[(y * source.Width) + x] = source[x, y];
            }
        }

        return new BarcodeMatrix(source.Width, source.Height, modules);
    }
}
