using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Kscan.App.Services;

[assembly: AvaloniaTestApplication(typeof(Kscan.App.Tests.TestAppBuilder))]

namespace Kscan.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>()
        .UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class TestApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public sealed class MarkdownPinTests
{
    [AvaloniaFact]
    public async Task Rich_document_renders_visible_pixels_and_preserves_long_content()
    {
        using var shortImage = await MarkdownPinRenderer.RenderAsync("# 标题 Heading\n\n**粗体** and *italic*\n\n> 引用\n\n- first\n- second\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n```csharp\nvar x = 42;\n```\n\n最后一行");
        // 位图必须是 96 DPI：贴图窗口按"位图像素尺寸"来决定窗口尺寸，非 96 DPI 的位图
        // （逻辑尺寸只有像素尺寸的一半）会被 Avalonia 的 Image 在绘制时再缩放一次，
        // 贴到屏幕上就只剩文档开头一部分。这几条断言就是那次 bug 的护栏。
        Assert.Equal(96, shortImage.Dpi.X);
        Assert.Equal(shortImage.PixelSize.Width, (int)shortImage.Size.Width);
        Assert.Equal(shortImage.PixelSize.Height, (int)shortImage.Size.Height);
        Assert.Equal(640, shortImage.PixelSize.Width);
        Assert.True(shortImage.PixelSize.Height > 150);
        using var stream = new MemoryStream();
        shortImage.Save(stream, new PngBitmapEncoderOptions());
        if (Environment.GetEnvironmentVariable("QSCAN_RENDER_PREVIEW") is { Length: > 0 } preview)
            shortImage.Save(preview, new PngBitmapEncoderOptions());
        using var pixels = SkiaSharp.SKBitmap.Decode(stream.ToArray());
        var dark = 0;
        for (var y = 0; y < pixels.Height; y += 2)
        for (var x = 0; x < pixels.Width; x += 2)
        {
            var color = pixels.GetPixel(x, y);
            if (color.Alpha > 0 && color.Red < 180 && color.Green < 180 && color.Blue < 180) dark++;
        }
        Assert.True(dark > 200, $"No rendered text: dark pixels={dark}");
        using var longImage = await MarkdownPinRenderer.RenderAsync(string.Join("\n\n", Enumerable.Range(0, 80).Select(i => $"Paragraph {i} — 中文内容")));
        Assert.True(longImage.PixelSize.Height > shortImage.PixelSize.Height * 3);
    }

    [AvaloniaFact]
    public async Task Blank_text_is_rejected() => await Assert.ThrowsAsync<ArgumentException>(() => MarkdownPinRenderer.RenderAsync("  "));

    [AvaloniaFact]
    public async Task Local_markdown_image_is_included_in_the_snapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), "kscan-md-" + Guid.NewGuid().ToString("N") + ".png");
        using var source = new SkiaSharp.SKBitmap(40, 40);
        source.Erase(SkiaSharp.SKColors.Lime);
        using (var encoded = source.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
        using (var file = File.Create(path)) encoded.SaveTo(file);
        try
        {
            using var rendered = await MarkdownPinRenderer.RenderAsync($"# Image\n\n![green]({new Uri(path).AbsoluteUri})");
            using var stream = new MemoryStream();
            rendered.Save(stream, new PngBitmapEncoderOptions());
            using var pixels = SkiaSharp.SKBitmap.Decode(stream.ToArray());
            Assert.Contains(pixels.Pixels, pixel => pixel.Green > 230 && pixel.Red < 30 && pixel.Blue < 30);
        }
        finally { File.Delete(path); }
    }
}
