using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Kscan.App.Services;
using Kscan.Core.Imaging;

namespace Kscan.App.Views;

/// <summary>选区外常驻实时预览；生成受窗口尺寸限制的缩略图，不复制完整长图。</summary>
internal sealed class ScrollLivePreviewWindow : Window
{
    private readonly PixelRect _placement;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _size = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
    private Bitmap? _bitmap;
    internal Image PreviewImageForTest => _image;

    public ScrollLivePreviewWindow(PixelRect placement, Action openFullPreview)
    {
        _placement = placement;
        Title = "滚动截图 · 实时预览";
        WindowDecorations = WindowDecorations.None; CanResize = false;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        MinWidth = MinHeight = 0;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Bind(BackgroundProperty, new DynamicResourceExtension("KscanBackgroundBrush"));
        Bind(ForegroundProperty, new DynamicResourceExtension("KscanForegroundBrush"));
        Bind(FontFamilyProperty, new DynamicResourceExtension("KscanFontFamily"));
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(8) };
        root.Children.Add(new TextBlock { Text = "实时预览 · 点击查看原图", FontSize = 12, Margin = new Thickness(2, 2, 2, 8) });
        var view = new Button { Content = _image, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        view.Click += (_, _) => openFullPreview();
        Grid.SetRow(view, 1); root.Children.Add(view);
        _size.Margin = new Thickness(2, 8, 2, 2); Grid.SetRow(_size, 2); root.Children.Add(_size);
        Content = root;
        Closed += (_, _) => { _image.Source = null; _bitmap?.Dispose(); _bitmap = null; };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var scale = Screens.ScreenFromPoint(_placement.Position)?.Scaling ?? 1;
        Width = _placement.Width / scale; Height = _placement.Height / scale; Position = _placement.Position;
    }

    public void Refresh(ScrollStitcher stitcher)
    {
        // 扣掉标题/尺寸栏后按物理像素采样，避免高 DPI 下额外模糊。
        var width = Math.Max(1, _placement.Width - (int)Math.Ceiling(20 * RenderScaling));
        var height = Math.Max(1, _placement.Height - (int)Math.Ceiling(70 * RenderScaling));
        var next = ResultActions.ToBitmap(stitcher.Preview(width, height));
        var old = _bitmap; _bitmap = next; _image.Source = next; old?.Dispose();
        _size.Text = $"{stitcher.Width} × {stitcher.Height} 像素 · {stitcher.PartCount} 段";
    }
}
