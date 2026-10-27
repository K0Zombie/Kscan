using Kscan.Core.Imaging;
using Kscan.Core.Model;
using Kscan.Core.Pipeline;

namespace Kscan.Core.Tests;

/// <summary>
/// 渲染层的两块补充：中心 Logo 叠加、字符画行重复。
/// 最关键的断言是"叠了 Logo 之后二维码仍然扫得出来"。
/// </summary>
public class LogoOverlayTests
{
    [Fact]
    public void Overlay_keeps_barcode_size_and_changes_center()
    {
        var barcode = RenderQr("LOGO-SIZE", 400);
        var logo = SolidFrame(64, 64, 0xFFFF0000);

        var composed = LogoOverlay.Apply(barcode, logo, 0.25);

        Assert.Equal(barcode.Width, composed.Width);
        Assert.Equal(barcode.Height, composed.Height);

        // 注意：Pixels 是字节数组，索引要乘 BytesPerPixel，否则比的是完全不相干的像素
        var centerByte = (((composed.Height / 2) * composed.Width) + (composed.Width / 2)) * ImageFrame.BytesPerPixel;
        var originalByte = (((barcode.Height / 2) * barcode.Width) + (barcode.Width / 2)) * ImageFrame.BytesPerPixel;

        Assert.NotEqual(
            barcode.Pixels[originalByte..(originalByte + 4)],
            composed.Pixels[centerByte..(centerByte + 4)]);

        // Logo 是纯红：中心像素应变成红色（BGRA 存储 → B=0、G=0、R=255）
        Assert.Equal(0, composed.Pixels[centerByte]);
        Assert.Equal(0, composed.Pixels[centerByte + 1]);
        Assert.Equal(255, composed.Pixels[centerByte + 2]);
    }

    [Fact]
    public void Overlay_leaves_corners_untouched()
    {
        var barcode = RenderQr("LOGO-CORNERS", 400);
        var composed = LogoOverlay.Apply(barcode, SolidFrame(64, 64, 0xFF00FF00), 0.2);

        // 左上角（静区）必须原样保留，否则静区被破坏会影响识别
        Assert.Equal(barcode.Pixels[0..16], composed.Pixels[0..16]);
    }

    [Fact]
    public void Barcode_with_logo_still_decodes()
    {
        var request = new BarcodeGenerationRequest
        {
            Text = "LOGO-ROUND-TRIP-内容",
            Format = "QR_CODE",
            Width = 480,
            Height = 480,
            ErrorCorrection = "H",   // 叠 Logo 建议用高纠错级别
        };

        var matrix = BarcodeEncoder.Encode(request);
        var frame = BarcodeRenderer.ToImageFrame(matrix, request);
        var composed = LogoOverlay.Apply(frame, SolidFrame(96, 96, 0xFF1E88E5), 0.2);

        var decoded = ScanPipeline.CreateDefault().Scan(composed);

        Assert.True(decoded.Success, "叠加 Logo 后二维码读不回来了（Logo 太大或衬底不足）");
        Assert.Equal("LOGO-ROUND-TRIP-内容", decoded.FirstText);
    }

    [Fact]
    public void Overlay_rejects_absurd_ratios()
    {
        var barcode = RenderQr("RATIO", 200);

        Assert.Throws<ArgumentOutOfRangeException>(() => LogoOverlay.Apply(barcode, SolidFrame(8, 8, 0xFF000000), 0.9));
        Assert.Throws<ArgumentOutOfRangeException>(() => LogoOverlay.Apply(barcode, SolidFrame(8, 8, 0xFF000000), 0.001));
    }

    [Fact]
    public void Ascii_row_repeat_makes_one_dimensional_codes_tall_enough()
    {
        // 1D 码的**模块级**矩阵只有 1 行高 —— 不重复的话扫描器根本扫不到
        var request = new BarcodeGenerationRequest { Text = "12345678", Format = "ITF", Width = 300 };
        var matrix = BarcodeEncoder.EncodeModules(request);
        Assert.Equal(1, matrix.Height);

        var art = BarcodeRenderer.ToAscii(matrix, new AsciiRenderOptions
        {
            Style = AsciiBarcodeStyle.Hash,
            Margin = 0,
            RowRepeat = 6,
        });

        var lines = art.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(6, lines.Length);
        Assert.All(lines, line => Assert.Equal(line, lines[0]));
    }

    private static ImageFrame RenderQr(string text, int size) =>
        BarcodeRenderer.ToImageFrame(
            BarcodeEncoder.Encode(new BarcodeGenerationRequest
            {
                Text = text,
                Format = "QR_CODE",
                Width = size,
                Height = size,
            }),
            new BarcodeGenerationRequest { Text = text, Format = "QR_CODE", Width = size, Height = size });

    private static ImageFrame SolidFrame(int width, int height, uint argb)
    {
        var frame = new ImageFrame(width, height, new byte[width * height * ImageFrame.BytesPerPixel]);
        var b = (byte)(argb & 0xFF);
        var g = (byte)((argb >> 8) & 0xFF);
        var r = (byte)((argb >> 16) & 0xFF);
        var a = (byte)((argb >> 24) & 0xFF);

        for (var i = 0; i < frame.Pixels.Length; i += ImageFrame.BytesPerPixel)
        {
            frame.Pixels[i] = b;
            frame.Pixels[i + 1] = g;
            frame.Pixels[i + 2] = r;
            frame.Pixels[i + 3] = a;
        }

        return frame;
    }
}
