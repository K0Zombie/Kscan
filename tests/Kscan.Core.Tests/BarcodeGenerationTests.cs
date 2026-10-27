using Kscan.Core.Imaging;
using Kscan.Core.Model;
using Kscan.Core.Pipeline;

namespace Kscan.Core.Tests;

/// <summary>
/// 生成能力测试。分两类：
/// 1) 常规码制要求"生成 → 读回"闭环；
/// 2) ZXing.Net 自家 reader 读不回来的码制（有 writer 无可用 reader），只验证能生成出正常图形。
/// </summary>
public class BarcodeGenerationTests
{
    public static TheoryData<string, string> TwoDimensionalCases => new()
    {
        { "QR_CODE", "https://example.com/qrscan-gen" },
        { "DATA_MATRIX", "DM-1234567890" },
        { "PDF_417", "PDF417 content 12345" },
        { "AZTEC", "AZTEC-CONTENT-42" },
    };

    public static TheoryData<string, string> OneDimensionalCases => new()
    {
        { "CODE_128", "QRSCAN-2026-001" },
        { "CODE_39", "QRSCAN39" },
        { "CODE_93", "QRSCAN93" },
        { "CODABAR", "1234567890" },
        { "EAN_8", "96385074" },
        { "EAN_13", "5901234123457" },
        { "UPC_A", "036000291452" },
        { "ITF", "1234567890" },
    };

    /// <summary>
    /// 这三个码制 ZXing.Net 有 writer（能生成），但其 reader 读不回来
    /// （单码 <c>--single</c> 与多码模式均已实测；生成的图形本身正常，
    /// 用图像检查确认过条形结构无误）。原因见 DESIGN §13「已知限制」。
    /// </summary>
    public static TheoryData<string, string> ReaderUnsupportedCases => new()
    {
        { "UPC_E", "01234565" },
        { "MSI", "1234567" },
        { "PLESSEY", "1234ABCD" },
    };

    [Theory]
    [MemberData(nameof(TwoDimensionalCases))]
    public void Two_dimensional_formats_round_trip(string format, string text)
    {
        var outcome = GenerateAndScan(text, format, size: 400);

        Assert.True(outcome.Success, $"{format} 生成后读不回来");
        Assert.Equal(text, outcome.FirstText);
    }

    [Theory]
    [MemberData(nameof(OneDimensionalCases))]
    public void One_dimensional_formats_round_trip(string format, string text)
    {
        var outcome = GenerateAndScan(text, format, size: 600);

        Assert.True(outcome.Success, $"{format} 生成后读不回来");
        Assert.Equal(text, outcome.FirstText);
    }

    [Theory]
    [MemberData(nameof(ReaderUnsupportedCases))]
    public void Reader_unsupported_formats_still_generate_valid_images(string format, string text)
    {
        var request = Request(text, format, size: 600);
        var matrix = BarcodeEncoder.Encode(request);
        Assert.True(matrix.Width > 0 && matrix.Height > 0);

        var frame = SkiaImageCodec.FromEncoded(BarcodeImageFactory.CreatePng(request));
        Assert.True(frame.Width > 0 && frame.Height > 0);

        // 图形里必须真的画了前景（存在接近黑色的像素）
        Assert.Contains(frame.Pixels, value => value < 0x40);
    }

    [Fact]
    public void Reader_unsupported_list_is_declared_for_the_ui()
    {
        Assert.All(BarcodeFormats.ReaderUnsupported, format =>
        {
            Assert.True(BarcodeFormats.IsWritable(format));
            Assert.Contains(format, BarcodeFormats.OneDimensional);
        });
    }

    [Fact]
    public void Qr_code_keeps_chinese_content()
    {
        const string text = "http://例子.测试/扫码?参数=值";

        var outcome = GenerateAndScan(text, "QR_CODE", size: 400);

        Assert.Equal(text, outcome.FirstText);
    }

    [Fact]
    public void Svg_output_is_well_formed_and_uses_lf()
    {
        var svg = BarcodeImageFactory.CreateSvg(Request("SVG-TEST", "QR_CODE"));

        Assert.StartsWith("<?xml", svg, StringComparison.Ordinal);
        Assert.Contains("<svg", svg, StringComparison.Ordinal);
        Assert.Contains("<path", svg, StringComparison.Ordinal);
        Assert.EndsWith("</svg>\n", svg, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', svg);
    }

    [Fact]
    public void Ascii_half_blocks_matches_matrix_shape()
    {
        var request = Request("ASCII-HALF", "QR_CODE");
        var matrix = BarcodeEncoder.Encode(request);

        var art = BarcodeRenderer.ToAscii(matrix, new AsciiRenderOptions
        {
            Style = AsciiBarcodeStyle.HalfBlocks,
            Margin = 0,
        });

        var lines = art.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal((matrix.Height + 1) / 2, lines.Length);
        Assert.All(lines, line => Assert.Equal(matrix.Width, line.Length));
        Assert.Contains('█', art);
        Assert.All(art.Where(c => c != '\n'), c => Assert.Contains(c, "█▀▄ "));
    }

    [Fact]
    public void Ascii_blocks_double_width_and_hash_style()
    {
        var request = Request("ASCII-BLOCKS", "QR_CODE");
        var matrix = BarcodeEncoder.Encode(request);

        var blocks = BarcodeRenderer.ToAscii(matrix, new AsciiRenderOptions
        {
            Style = AsciiBarcodeStyle.Blocks,
            Margin = 0,
        });

        var lines = blocks.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(matrix.Height, lines.Length);
        Assert.All(lines, line => Assert.Equal(matrix.Width * 2, line.Length));

        var hash = BarcodeRenderer.ToAscii(matrix, new AsciiRenderOptions
        {
            Style = AsciiBarcodeStyle.Hash,
            Margin = 0,
        });
        Assert.Contains('#', hash);
    }

    [Fact]
    public void Ascii_custom_style_uses_given_characters()
    {
        var art = BarcodeRenderer.ToAscii(
            BarcodeEncoder.Encode(Request("CUSTOM", "QR_CODE")),
            new AsciiRenderOptions { Style = AsciiBarcodeStyle.Custom, OnChar = '十', OffChar = '・', Margin = 0 });

        Assert.Contains('十', art);
        Assert.Contains('・', art);
    }

    [Fact]
    public void Ascii_invert_swaps_foreground()
    {
        var matrix = BarcodeEncoder.Encode(Request("INVERT", "QR_CODE"));
        var normal = BarcodeRenderer.ToAscii(matrix, new AsciiRenderOptions { Style = AsciiBarcodeStyle.Hash, Margin = 0 });
        var inverted = BarcodeRenderer.ToAscii(matrix, new AsciiRenderOptions { Style = AsciiBarcodeStyle.Hash, Margin = 0, Invert = true });

        Assert.NotEqual(normal, inverted);
        Assert.Equal(normal.Count(c => c == '#'), inverted.Count(c => c == ' '));
    }

    [Fact]
    public void Colors_parse_supports_short_long_and_named()
    {
        Assert.Equal(0xFF000000u, Colors.Parse("#000000"));
        Assert.Equal(0xFFFFFFFFu, Colors.Parse("#FFF"));
        Assert.Equal(0x80FF0000u, Colors.Parse("#80FF0000"));
        Assert.Equal(0xFF000000u, Colors.Parse("black"));
        Assert.Throws<FormatException>(() => Colors.Parse("#12345"));
    }

    [Fact]
    public void Writable_formats_are_all_encodable()
    {
        Assert.Equal(15, BarcodeFormats.Writable.Count);
        Assert.All(BarcodeFormats.Writable, format => Assert.True(BarcodeEncoder.TryParseFormat(format, out _)));
        Assert.False(BarcodeEncoder.TryParseFormat("MAXICODE", out _));
    }

    [Theory]
    [InlineData("EAN_13", "ABC123456789", false)]
    [InlineData("EAN_13", "590123412345", true)]
    [InlineData("EAN_8", "12345678", true)]
    [InlineData("EAN_8", "1234567", true)]
    [InlineData("UPC_A", "12345", false)]
    [InlineData("ITF", "123456", true)]
    [InlineData("ITF", "1234567", false)]
    [InlineData("QR_CODE", "任意内容都行", true)]
    public void Content_validation_matches_format_rules(string format, string text, bool expectedValid)
    {
        var problem = BarcodeEncoder.ValidateContent(format, text);

        Assert.Equal(expectedValid, problem is null);
    }

    [Fact]
    public void Unsupported_format_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => BarcodeEncoder.Encode(Request("X", "MAXICODE")));
    }

    private static ScanOutcome GenerateAndScan(string text, string format, int size)
    {
        var png = BarcodeImageFactory.CreatePng(Request(text, format, size));
        return ScanPipeline.CreateDefault().Scan(SkiaImageCodec.FromEncoded(png));
    }

    private static BarcodeGenerationRequest Request(string text, string format, int size = 320) => new()
    {
        Text = text,
        Format = format,
        Width = size,
        Height = size,
        Margin = 4,
    };
}
