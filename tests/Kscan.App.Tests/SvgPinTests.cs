using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Kscan.App.Services;
using SkiaSharp;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Kscan.App.Tests;

public sealed class SvgPinTests
{
    [AvaloniaFact]
    public async Task Clipboard_svg_text_is_read_but_markdown_is_not_claimed_as_svg()
    {
        var window = new Window(); window.Show();
        try
        {
            var clipboard = window.Clipboard!;
            const string svg = "<svg width='10' height='10'><rect width='10' height='10'/></svg>";
            await clipboard.SetTextAsync(svg);
            Assert.Equal(svg, await SvgPinRenderer.ReadClipboardAsync(clipboard));
            await clipboard.SetTextAsync("# Markdown text");
            Assert.Null(await SvgPinRenderer.ReadClipboardAsync(clipboard));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Svg_renders_color_and_transparency_at_original_size()
    {
        using var bitmap = SvgPinRenderer.Render("""<svg xmlns="http://www.w3.org/2000/svg" width="64" height="48"><rect x="8" y="8" width="32" height="24" fill="#ff0000"/></svg>""");
        Assert.Equal(64, bitmap.PixelSize.Width); Assert.Equal(48, bitmap.PixelSize.Height);
        using var stream = new MemoryStream(); bitmap.Save(stream, new PngBitmapEncoderOptions());
        using var actual = SKBitmap.Decode(stream.ToArray());
        Assert.Equal((byte)0, actual.GetPixel(0, 0).Alpha);
        Assert.Equal(SKColors.Red, actual.GetPixel(16, 16));
    }

    [AvaloniaFact]
    public void Viewbox_gradients_and_size_limit_are_supported()
    {
        const string svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 50"><defs><linearGradient id="g"><stop stop-color="red"/><stop offset="1" stop-color="blue"/></linearGradient></defs><rect width="100" height="50" fill="url(#g)"/></svg>""";
        using var bitmap = SvgPinRenderer.Render(svg, 80);
        Assert.Equal(80, bitmap.PixelSize.Width); Assert.Equal(40, bitmap.PixelSize.Height);
        using var stream = new MemoryStream(); bitmap.Save(stream, new PngBitmapEncoderOptions());
        using var actual = SKBitmap.Decode(stream.ToArray());
        Assert.True(actual.GetPixel(10, 20).Red > actual.GetPixel(70, 20).Red);
    }

    [Theory]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'/>", true)]
    [InlineData("<?xml version='1.0'?><!-- logo --><svg width='10' height='10'/>", true)]
    [InlineData("<svg-icon>hello</svg-icon>", false)]
    [InlineData("# title\nordinary text", false)]
    public void Svg_text_is_distinguished_from_markdown(string text, bool expected) => Assert.Equal(expected, SvgPinRenderer.LooksLikeSvg(text));

    [Theory]
    [InlineData("<svg><image href='https://example.com/image.png'/></svg>")]
    [InlineData("<svg><rect fill='url(file:///secret)'/></svg>")]
    [InlineData("<svg><style>@import 'https://example.com/style.css';</style></svg>")]
    [InlineData("<svg><script>alert(1)</script></svg>")]
    public void External_resources_and_scripts_are_rejected(string svg) => Assert.Throws<InvalidDataException>(() => SvgPinRenderer.Validate(svg));
}
