namespace Kscan.Core.Model;

/// <summary>
/// 码制的名字与分类。列表来自 ZXing.Net 中确实存在 writer 实现的格式
/// （即"能生成"的集合），读取端支持的格式比这里多。
/// </summary>
public static class BarcodeFormats
{
    /// <summary>二维码（方形或矩形矩阵）。</summary>
    public static IReadOnlyList<string> TwoDimensional { get; } =
    [
        "QR_CODE",
        "DATA_MATRIX",
        "PDF_417",
        "AZTEC",
    ];

    /// <summary>一维码（条码）。</summary>
    public static IReadOnlyList<string> OneDimensional { get; } =
    [
        "CODE_128",
        "CODE_39",
        "CODE_93",
        "CODABAR",
        "EAN_8",
        "EAN_13",
        "UPC_A",
        "UPC_E",
        "ITF",
        "MSI",
        "PLESSEY",
    ];

    /// <summary>全部可生成的码制。</summary>
    public static IReadOnlyList<string> Writable { get; } = [.. TwoDimensional, .. OneDimensional];

    /// <summary>
    /// 能生成、但**本项目读不回来**的码制：ZXing.Net 有 writer 却没有可用的 reader
    /// （已实测，单码与多码模式都识别不到；生成的图形本身正常，用图像检查确认过结构无误）。
    /// 界面在用这些码制生成时应提示"请用专用扫描设备验证"。
    /// </summary>
    public static IReadOnlyList<string> ReaderUnsupported { get; } =
    [
        "UPC_E",
        "MSI",
        "PLESSEY",
    ];

    /// <summary>常用码制的显示名（用于界面下拉框）。</summary>
    public static IReadOnlyDictionary<string, string> DisplayNames { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["QR_CODE"] = "QR 码",
        ["DATA_MATRIX"] = "Data Matrix",
        ["PDF_417"] = "PDF417",
        ["AZTEC"] = "Aztec",
        ["CODE_128"] = "Code 128",
        ["CODE_39"] = "Code 39",
        ["CODE_93"] = "Code 93",
        ["CODABAR"] = "Codabar",
        ["EAN_8"] = "EAN-8",
        ["EAN_13"] = "EAN-13",
        ["UPC_A"] = "UPC-A",
        ["UPC_E"] = "UPC-E",
        ["ITF"] = "ITF（Interleaved 2 of 5）",
        ["MSI"] = "MSI",
        ["PLESSEY"] = "Plessey",
    };

    public static bool IsTwoDimensional(string format) =>
        TwoDimensional.Contains(format, StringComparer.OrdinalIgnoreCase);

    public static bool IsWritable(string format) =>
        Writable.Contains(format, StringComparer.OrdinalIgnoreCase);

    /// <summary>取显示名；未知格式原样返回。</summary>
    public static string DisplayName(string format) =>
        DisplayNames.TryGetValue(format, out var name) ? name : format;
}
