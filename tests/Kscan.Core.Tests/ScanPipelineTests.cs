using Kscan.Core.Abstractions;
using Kscan.Core.Engines;
using Kscan.Core.Imaging;
using Kscan.Core.Model;
using Kscan.Core.Pipeline;

namespace Kscan.Core.Tests;

public class ScanPipelineTests
{
    private static ImageFrame QrFrame(string text) =>
        SkiaImageCodec.FromEncoded(BarcodeImageFactory.CreateQrCodePng(text));

    [Fact]
    public void Denoise_rescues_a_qr_with_bright_lines_across_it()
    {
        // 识别页的高亮框就是这种"亮色细线"。实测它能把二维码的定位图形切断，
        // 让原图彻底读不出来 —— 这里把那个场景钉住：原图失败，去噪后成功。
        var marked = DrawFrameLines(QrFrame("DENOISE-RESCUE"));

        var strict = ScanPipeline.CreateDefault().Scan(marked, new ScanOptions { TryDenoise = false });
        Assert.False(strict.Success);

        var outcome = ScanPipeline.CreateDefault().Scan(marked);
        Assert.True(outcome.Success);
        Assert.Equal("DENOISE-RESCUE", outcome.FirstText);
    }

    /// <summary>在图上画一圈亮蓝色细线（跟识别页画的凸包框同色同粗细）。</summary>
    private static ImageFrame DrawFrameLines(ImageFrame source)
    {
        var pixels = (byte[])source.Pixels.Clone();
        var width = source.Width;
        var height = source.Height;

        // 二维码的三个定位中心大致在这里；识别页的凸包正是连这三点
        var left = width * 0.12;
        var top = height * 0.12;
        var right = width * 0.88;
        var bottom = height * 0.88;

        DrawLine(pixels, width, height, left, top, right, top);       // 上边
        DrawLine(pixels, width, height, right, top, left, bottom);    // 斜边
        DrawLine(pixels, width, height, left, bottom, left, top);     // 左边

        return new ImageFrame(width, height, pixels);
    }

    private static void DrawLine(byte[] pixels, int width, int height, double x1, double y1, double x2, double y2)
    {
        const int thickness = 1;   // 3px 宽的线
        var steps = (int)(Math.Max(Math.Abs(x2 - x1), Math.Abs(y2 - y1)) * 2) + 1;

        for (var i = 0; i <= steps; i++)
        {
            var t = i / (double)steps;
            var x = (int)Math.Round(x1 + ((x2 - x1) * t));
            var y = (int)Math.Round(y1 + ((y2 - y1) * t));

            for (var dy = -thickness; dy <= thickness; dy++)
            {
                for (var dx = -thickness; dx <= thickness; dx++)
                {
                    var px = x + dx;
                    var py = y + dy;

                    if (px < 0 || py < 0 || px >= width || py >= height)
                    {
                        continue;
                    }

                    var offset = ((py * width) + px) * 4;
                    pixels[offset] = 245;       // B
                    pixels[offset + 1] = 139;   // G
                    pixels[offset + 2] = 76;    // R   —— #4C8BF5
                    pixels[offset + 3] = 255;
                }
            }
        }
    }

    [Fact]
    public void Stops_at_first_engine_that_hits()
    {
        var pipeline = new ScanPipeline([new ThrowingEngine(), new ZxingEngine()]);

        var outcome = pipeline.Scan(QrFrame("PIPELINE"));

        Assert.True(outcome.Success);
        Assert.Equal("PIPELINE", outcome.FirstText);
        Assert.Equal(2, outcome.Attempts.Count);
        Assert.False(outcome.Attempts[0].Succeeded);
        Assert.NotNull(outcome.Attempts[0].Error);
        Assert.True(outcome.Attempts[1].Succeeded);
        Assert.Equal("zxing", outcome.Attempts[1].EngineId);
    }

    [Fact]
    public void Reports_failure_without_throwing()
    {
        var pipeline = ScanPipeline.CreateDefault();

        var outcome = pipeline.Scan(new ImageFrame(120, 120, new byte[120 * 120 * ImageFrame.BytesPerPixel]));

        Assert.False(outcome.Success);
        Assert.Null(outcome.FirstText);
        Assert.Empty(outcome.Results);
        // 均匀空白图的滤镜均无变化，不能重复把同一幅图交给引擎。
        Assert.Single(outcome.Attempts);
        Assert.All(outcome.Attempts, attempt => Assert.False(attempt.Succeeded));
    }

    [Fact]
    public void Unavailable_engines_are_filtered_out()
    {
        var pipeline = new ScanPipeline([new UnavailableEngine(), new ZxingEngine()]);

        var engine = Assert.Single(pipeline.Engines);
        Assert.Equal("zxing", engine.Id);
    }

    [Fact]
    public void Default_pipeline_contains_zxing_only()
    {
        var pipeline = ScanPipeline.CreateDefault();

        var engine = Assert.Single(pipeline.Engines);
        Assert.Equal("zxing", engine.Id);
        Assert.False(engine.Capabilities.RequiresNativeLibrary);
    }

    private sealed class ThrowingEngine : IBarcodeEngine
    {
        public string Id => "throwing";

        public string DisplayName => "Throwing";

        public bool IsAvailable => true;

        public EngineCapabilities Capabilities { get; } = new();

        public IReadOnlyList<ScanResult> Decode(ImageFrame frame, ScanOptions options) =>
            throw new InvalidOperationException("模拟引擎崩溃");
    }

    private sealed class UnavailableEngine : IBarcodeEngine
    {
        public string Id => "unavailable";

        public string DisplayName => "Unavailable";

        public bool IsAvailable => false;

        public EngineCapabilities Capabilities { get; } = new();

        public IReadOnlyList<ScanResult> Decode(ImageFrame frame, ScanOptions options) => [];
    }
}
