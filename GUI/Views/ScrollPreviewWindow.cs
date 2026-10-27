using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Kscan.App.Services;
using Kscan.Core.Imaging;

namespace Kscan.App.Views;

/// <summary>暂停采集后显示原始长图，缩放仅用于显示，不改变拼接像素。</summary>
internal sealed class ScrollPreviewWindow : Window
{
    private readonly Image _image;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _info = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ImageFrame _frame;
    private double _zoom = 1;
    internal double ZoomForTest => _zoom;

    public ScrollPreviewWindow(ImageFrame frame)
    {
        _frame = frame;
        Title = "滚动截图 · 大图预览（采集已暂停）";
        Width = 960; Height = 740; MinWidth = 520; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Topmost = true;
        Bind(BackgroundProperty, new DynamicResourceExtension("KscanBackgroundBrush"));
        Bind(ForegroundProperty, new DynamicResourceExtension("KscanForegroundBrush"));
        Bind(FontFamilyProperty, new DynamicResourceExtension("KscanFontFamily"));
        var bitmap = ResultActions.ToBitmap(frame);
        _image = new Image { Source = bitmap, Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _scroll = new ScrollViewer
        {
            Content = _image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top,
        };
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        void Add(string label, Action action)
        {
            var button = new Button { Content = label, Margin = new Thickness(3), Padding = new Thickness(10, 6) };
            button.Click += (_, _) => action(); bar.Children.Add(button);
        }
        Add("－", () => SetZoom(_zoom / 1.25));
        Add("＋", () => SetZoom(_zoom * 1.25));
        Add("100%", () => SetZoom(1));
        Add("适应宽度", () => SetZoom(Math.Min(1, Math.Max(1, _scroll.Viewport.Width - 24) * RenderScaling / frame.Width)));
        Add("关闭预览", Close);
        _info.Margin = new Thickness(12, 0); bar.Children.Add(_info);
        var root = new DockPanel(); DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar); root.Children.Add(_scroll); Content = root;
        Opened += (_, _) =>
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen is not null)
            {
                Width = Math.Min(960, screen.WorkingArea.Width / screen.Scaling * .94);
                Height = Math.Min(740, screen.WorkingArea.Height / screen.Scaling * .90);
                Position = new PixelPoint(screen.WorkingArea.X + (int)((screen.WorkingArea.Width - Width * screen.Scaling) / 2),
                    screen.WorkingArea.Y + (int)((screen.WorkingArea.Height - Height * screen.Scaling) / 2));
            }
            Dispatcher.UIThread.Post(() => SetZoom(1), DispatcherPriority.Loaded);
        };
        ScalingChanged += (_, _) => SetZoom(_zoom);
        Closed += (_, _) => { _image.Source = null; bitmap.Dispose(); };
    }

    internal void SetZoom(double zoom)
    {
        var oldScale = _zoom;
        var oldOffset = _scroll.Offset;
        _zoom = Math.Clamp(zoom, .05, 4);
        _image.Width = _frame.Width * _zoom / RenderScaling;
        _image.Height = _frame.Height * _zoom / RenderScaling;
        _scroll.Offset = oldOffset * (_zoom / oldScale);
        _info.Text = $"{_frame.Width} × {_frame.Height} 像素 · {_zoom * 100:0}% · 滚动查看";
    }
}
