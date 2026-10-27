using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.LogicalTree;
using ColorTextBlock.Avalonia;

namespace Kscan.App.Services;

/// <summary>把 Markdown 文档渲染为完整位图，复用图片贴图的全部交互。</summary>
public static class MarkdownPinRenderer
{
    /// <summary>
    /// 文档的排版宽度（逻辑单位）。位图就按这个宽度、以 96 DPI 渲染 —— 见 <see cref="RenderAsync"/> 里的说明。
    /// </summary>
    private const double DocumentWidth = 640;

    public static async Task<Bitmap> RenderAsync(string markdown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdown);
        var viewer = new Markdown.Avalonia.Full.MarkdownScrollViewer
        {
            SelectionEnabled = false,
        };
        // 直接渲染文档树，避免 ScrollViewer 的视口测量把长文变成空白或截断。
        var engine = new Markdown.Avalonia.Markdown { Plugins = viewer.Plugins };
        var content = engine.Transform(markdown);
        content.Classes.Add("Markdown_Avalonia_MarkdownViewer");
        // 图片加载完成后才测量；否则快照会永久保留占位图。
        var imageTasks = content.GetLogicalDescendants().OfType<CImage>()
            .Select(image => image.Task).OfType<Task<IImage?>>().Distinct().ToArray();
        try
        {
            await Task.WhenAll(imageTasks).WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch
        {
            // 超时后不阻塞 UI；后台加载若稍后结束，也要释放已经解码的图片。
            _ = ReleaseImagesAsync(imageTasks);
            throw;
        }
        var document = new ThemeVariantScope
        {
            Width = DocumentWidth,
            RequestedThemeVariant = ThemeVariant.Light,
            Child = new Border { Padding = new Thickness(24), Background = Brushes.White, Child = content },
        };
        var style = viewer.MarkdownStyle;
        viewer.Styles.Remove(style);
        document.Styles.Add(style);
        TextElement.SetForeground(document, Brushes.Black);
        TextElement.SetFontSize(document, 16);
        // 控件必须先接入样式宿主：否则字体在 Render 时才生效，行高仍是旧值，会逐行裁字。
        var host = new Window
        {
            Content = document, Width = DocumentWidth, Height = 480,
            ShowInTaskbar = false, ShowActivated = false, Opacity = 0,
            WindowDecorations = WindowDecorations.None,
        };
        try
        {
            host.Show();
            document.Measure(new Size(DocumentWidth, double.PositiveInfinity));
            var height = Math.Max(48, Math.Ceiling(document.DesiredSize.Height));
            if (height > 32768)
                throw new InvalidOperationException(I18n.T("Str.Pin.TextTooLong"));
            document.Arrange(new Rect(0, 0, DocumentWidth, height));
            // 位图必须按 **96 DPI** 渲染，位图的逻辑尺寸（Bitmap.Size）才等于像素尺寸。
            // 不要改成"2x 超采样"（DPI 192 + 双倍像素）：Avalonia 的 Image 绘制非 96 DPI 的
            // RenderTargetBitmap 时会按像素尺寸再缩放一次，贴到屏幕的窗口里就只剩文档开头一部分、
            // 其余被挤出窗口（实测 1280×682 / DPI 192 的位图在 1152×614 的窗口里只画出前 4 个块，
            // 而窗口、Grid、Image 的 Bounds 全是正确的 —— 问题只出在绘制这一步）。
            var bitmap = new RenderTargetBitmap(new PixelSize((int)DocumentWidth, (int)height), new Vector(96, 96));
            try
            {
                bitmap.Render(document);
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
        finally
        {
            host.Content = null;
            host.Close();
            await ReleaseImagesAsync(imageTasks);
        }
    }

    private static async Task ReleaseImagesAsync(IEnumerable<Task<IImage?>> tasks)
    {
        var released = new HashSet<IDisposable>(ReferenceEqualityComparer.Instance);
        foreach (var task in tasks)
        {
            try
            {
                if (await task is IDisposable image && released.Add(image)) image.Dispose();
            }
            catch (Exception)
            {
                // 观察已失败的加载任务；原始错误已由调用方显示。
            }
        }
    }
}
