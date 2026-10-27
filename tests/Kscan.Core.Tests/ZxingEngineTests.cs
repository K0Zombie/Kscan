using Kscan.Core.Engines;
using Kscan.Core.Imaging;
using Kscan.Core.Model;

namespace Kscan.Core.Tests;

public class ZxingEngineTests
{
    private static ImageFrame QrFrame(string text) =>
        SkiaImageCodec.FromEncoded(BarcodeImageFactory.CreateQrCodePng(text));

    [Theory]
    [InlineData("HELLO-QRSCAN")]
    [InlineData("https://example.com/qrscan?from=test")]
    [InlineData("中文内容测试：扫码器")]
    public void Qr_code_round_trip_returns_original_text(string text)
    {
        var results = new ZxingEngine().Decode(QrFrame(text), ScanOptions.Default);

        var result = Assert.Single(results);
        Assert.Equal(text, result.Text);
        Assert.Equal("QR_CODE", result.Format);
        Assert.Equal("zxing", result.EngineId);
        Assert.True(result.HasLocation);
        Assert.NotNull(result.Center);
    }

    [Fact]
    public void Blank_image_yields_no_result()
    {
        var frame = new ImageFrame(200, 200, new byte[200 * 200 * ImageFrame.BytesPerPixel]);

        var results = new ZxingEngine().Decode(frame, ScanOptions.Default);

        Assert.Empty(results);
    }

    [Fact]
    public void Cropped_region_still_decodes()
    {
        var frame = QrFrame("CROP-ME");
        var cropped = frame.Crop(frame.Width / 8, frame.Height / 8, frame.Width * 3 / 4, frame.Height * 3 / 4);

        var results = new ZxingEngine().Decode(cropped, ScanOptions.Default);

        Assert.Equal("CROP-ME", Assert.Single(results).Text);
    }

    [Fact]
    public void Duplicate_text_is_deduplicated()
    {
        var frame = QrFrame("DUP");
        var options = new ScanOptions { IgnoreDuplicates = true, MultiCode = true };

        var results = new ZxingEngine().Decode(frame, options);

        Assert.Single(results);
    }
}
