using Kscan.Core.Model;

namespace Kscan.Core.Imaging;

/// <summary>
/// 条码生成门面：把「文本 + 参数」一步变成 PNG / SVG / 等宽字符画。
/// 底层是 <see cref="BarcodeEncoder"/>（文本 → 模块矩阵）与
/// <see cref="BarcodeRenderer"/>（矩阵 → 各⾳输出）。
/// </summary>
public static class BarcodeImageFactory
{
    /// <summary>编码并渲染成 PNG 字节。</summary>
    public static byte[] CreatePng(BarcodeGenerationRequest request) =>
        BarcodeRenderer.ToPng(BarcodeEncoder.Encode(request), request);

    /// <summary>编码并渲染成 SVG 文本。</summary>
    public static string CreateSvg(BarcodeGenerationRequest request) =>
        BarcodeRenderer.ToSvg(BarcodeEncoder.Encode(request), request);

    /// <summary>编码并渲染成等宽字符画。</summary>
    public static string CreateAscii(BarcodeGenerationRequest request, AsciiRenderOptions? options = null) =>
        BarcodeRenderer.ToAscii(BarcodeEncoder.Encode(request), options);

    /// <summary>生成二维码 PNG（便捷重载，供测试夹具与 <c>kscan gen</c> 使用）。</summary>
    public static byte[] CreateQrCodePng(string text, int size = 320, int margin = 2, string errorCorrection = "M") =>
        CreatePng(new BarcodeGenerationRequest
        {
            Text = text,
            Format = "QR_CODE",
            Width = size,
            Height = size,
            Margin = margin,
            ErrorCorrection = errorCorrection,
        });
}
