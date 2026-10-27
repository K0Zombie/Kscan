using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Svg;

namespace Kscan.App.Services;

/// <summary>复用现有 SVG 绘制后端，生成透明贴图；不加载 SVG 外部资源。</summary>
internal static class SvgPinRenderer
{
    private const int MaxCharacters = 8 * 1024 * 1024;
    public static bool IsSvgFile(string path) => string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase);
    public static bool LooksLikeSvg(string? text) => text is not null && Regex.IsMatch(text.TrimStart('\uFEFF'),
        @"^\s*(?:<\?xml[^>]*\?>\s*)?(?:<!--[\s\S]*?-->\s*)*<svg(?:\s|>)", RegexOptions.IgnoreCase);

    internal static string Validate(string text)
    {
        if (text.Length > MaxCharacters) throw new InvalidDataException("SVG 文件过大（上限 8 MB）。");
        using var reader = XmlReader.Create(new StringReader(text.TrimStart('\uFEFF')), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = MaxCharacters });
        var doc = XDocument.Load(reader);
        if (doc.Root?.Name.LocalName != "svg") throw new InvalidDataException("不是有效的 SVG 图片。");
        foreach (var element in doc.Descendants())
        {
            if (element.Name.LocalName is "script" or "foreignObject") throw new InvalidDataException("SVG 贴图不支持脚本或嵌入网页。");
            foreach (var attribute in element.Attributes())
            {
                if (attribute.Name.LocalName == "href" && !LocalReference(attribute.Value))
                    throw new InvalidDataException("SVG 贴图暂不支持外部图片或文件引用，请先嵌入资源。");
            }
        }
        if (doc.Nodes().OfType<XProcessingInstruction>().Any(p => p.Target != "xml"))
            throw new InvalidDataException("SVG 贴图不支持外部样式表。");
        foreach (var css in doc.Descendants().Where(e => e.Name.LocalName == "style").Select(e => e.Value)
            .Concat(doc.Descendants().Attributes().Where(a => a.Name.LocalName == "style").Select(a => a.Value)))
        {
            if (css.Contains("@import", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SVG 贴图不支持外部样式表。");
        }
        // fill/stroke 等属性同样允许 url(#gradient)，但不能发起外部请求。
        foreach (Match match in Regex.Matches(doc.ToString(), @"url\(\s*['"" ]?([^)'""\s]+)", RegexOptions.IgnoreCase))
            if (!LocalReference(match.Groups[1].Value)) throw new InvalidDataException("SVG 贴图不支持外部资源。");
        return doc.ToString();
    }

    private static bool LocalReference(string value) => value.StartsWith('#') ||
        Regex.IsMatch(value, @"^data:image/(png|jpeg|gif|webp);base64,", RegexOptions.IgnoreCase);

    public static Bitmap Render(string text, int maxDimension = 4096)
    {
        var source = SvgSource.LoadFromSvg(Validate(text));
        var svg = new SvgImage { Source = source };
        var size = svg.Size;
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
            throw new InvalidDataException("SVG 缺少有效的尺寸或 viewBox。");
        var scale = Math.Min(1, maxDimension / Math.Max(size.Width, size.Height));
        var width = Math.Max(1, (int)Math.Ceiling(size.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(size.Height * scale));
        var control = new Image { Source = svg, Width = width, Height = height, Stretch = Stretch.Fill };
        control.Measure(new Size(width, height)); control.Arrange(new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        try { bitmap.Render(control); return bitmap; }
        catch { bitmap.Dispose(); throw; }
        finally { control.Source = null; }
    }

    public static Bitmap Load(string path, int maxDimension = 4096)
    {
        if (new FileInfo(path).Length > MaxCharacters) throw new InvalidDataException("SVG 文件过大（上限 8 MB）。");
        return Render(File.ReadAllText(path), maxDimension);
    }

    internal static async Task<string?> ReadClipboardAsync(IClipboard clipboard)
    {
        using (var data = await clipboard.TryGetDataAsync())
        {
            if (data is not null)
            {
                foreach (var name in new[] { "image/svg+xml", "image/x-inkscape-svg", "image/svg", "SVG" })
                {
                    var format = DataFormat.CreateBytesPlatformFormat(name);
                    if (!data.Contains(format)) continue;
                    var raw = await data.TryGetValueAsync(format);
                    var text = (object?)raw switch
                    {
                        byte[] bytes => Encoding.UTF8.GetString(bytes).TrimEnd('\0'),
                        ReadOnlyMemory<byte> bytes => Encoding.UTF8.GetString(bytes.Span).TrimEnd('\0'),
                        _ => null,
                    };
                    if (LooksLikeSvg(text)) return text;
                }
                var files = await data.TryGetFilesAsync();
                if (files is not null)
                    foreach (var file in files)
                        if (file.TryGetLocalPath() is { } path && IsSvgFile(path))
                        {
                            if (new FileInfo(path).Length > MaxCharacters) throw new InvalidDataException("SVG 文件过大（上限 8 MB）。");
                            return await File.ReadAllTextAsync(path);
                        }
            }
        }
        var clipboardText = await clipboard.TryGetTextAsync();
        return LooksLikeSvg(clipboardText) ? clipboardText : null;
    }
}
