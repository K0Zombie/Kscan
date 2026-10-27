using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Kscan.App.Services;
using Kscan.Core.Imaging;

namespace Kscan.App.Views;

/// <summary>按原始行号裁切长图，只复制像素，不缩放或重采样。</summary>
internal sealed class ScrollTrimWindow : Window
{
    private readonly ImageFrame _source;
    private readonly NumericUpDown _start;
    private readonly NumericUpDown _end;
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Border _before = new() { Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)) };
    private readonly Border _after = new() { Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)) };
    private readonly Border _range = new() { BorderBrush = Brushes.DeepSkyBlue, BorderThickness = new Thickness(0, 2) };
    private readonly TextBlock _size = new();
    private readonly TaskCompletionSource<ImageFrame?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<ImageFrame?> Result => _completion.Task;
    internal int Start => (int)(_start.Value ?? 1) - 1;
    internal int End => (int)(_end.Value ?? _source.Height);

    public ScrollTrimWindow(ImageFrame source)
    {
        _source = source;
        Title = "滚动截图 · 精细调整开头和结尾";
        Width = 900; Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Bind(BackgroundProperty, new DynamicResourceExtension("KscanBackgroundBrush"));
        Bind(ForegroundProperty, new DynamicResourceExtension("KscanForegroundBrush"));
        Bind(FontFamilyProperty, new DynamicResourceExtension("KscanFontFamily"));
        // Explicitly theme nested editors too: Fluent control templates may supply
        // their own foreground instead of inheriting the window's foreground.
        foreach (var type in new[] { typeof(Button), typeof(NumericUpDown), typeof(TextBox) })
        {
            var style = new Style(selector => selector.OfType(type));
            style.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("KscanForegroundBrush")));
            style.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("KscanPanelBrush")));
            Styles.Add(style);
        }
        _start = new NumericUpDown { Minimum = 1, Maximum = source.Height, Value = 1, Increment = 1, FormatString = "0", Width = 130 };
        _end = new NumericUpDown { Minimum = 1, Maximum = source.Height, Value = source.Height, Increment = 1, FormatString = "0", Width = 130 };
        var bitmap = ResultActions.ToBitmap(source);
        var image = new Image { Source = bitmap, Stretch = Stretch.Fill };
        var preview = new Grid { Width = Math.Min(760, source.Width), HorizontalAlignment = HorizontalAlignment.Center };
        preview.Height = preview.Width * source.Height / source.Width;
        preview.Children.Add(image);
        preview.Children.Add(_overlay);
        _overlay.Children.Add(_before); _overlay.Children.Add(_after); _overlay.Children.Add(_range);
        var scroll = new ScrollViewer { Content = preview, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void Update(bool goToEnd)
        {
            var ratio = preview.Width / source.Width;
            var top = Start * ratio;
            var bottom = End * ratio;
            _before.Width = _after.Width = _range.Width = preview.Width;
            _before.Height = top;
            Canvas.SetTop(_after, bottom); _after.Height = Math.Max(0, preview.Height - bottom);
            Canvas.SetTop(_range, top); _range.Height = Math.Max(0, bottom - top);
            _size.Text = $"保留 {source.Width} × {End - Start} 像素 · 每次可微调 1 行 · 不缩放原图";
            scroll.Offset = new Vector(0, goToEnd ? Math.Max(0, bottom - scroll.Viewport.Height + 40) : Math.Max(0, top - 40));
        }
        _start.ValueChanged += (_, _) => { _end.Minimum = _start.Value ?? 1; Update(false); };
        _end.ValueChanged += (_, _) => { _start.Maximum = _end.Value ?? source.Height; Update(true); };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(12) };
        bar.Children.Add(new TextBlock { Text = "起始行", VerticalAlignment = VerticalAlignment.Center }); bar.Children.Add(_start);
        bar.Children.Add(new TextBlock { Text = "结束行（含）", VerticalAlignment = VerticalAlignment.Center }); bar.Children.Add(_end);
        var finish = new Button { Content = "确认并标注" };
        finish.Bind(BackgroundProperty, new DynamicResourceExtension("KscanAccentBrush"));
        finish.Bind(ForegroundProperty, new DynamicResourceExtension("KscanAccentForegroundBrush"));
        finish.Click += (_, _) => { _completion.TrySetResult(source.Crop(0, Start, source.Width, End - Start)); Close(); };
        bar.Children.Add(finish);
        var cancel = new Button { Content = "取消" }; cancel.Click += (_, _) => Close(); bar.Children.Add(cancel);
        var root = new DockPanel(); DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        _size.Margin = new Thickness(12, 4); DockPanel.SetDock(_size, Dock.Bottom); root.Children.Add(_size); root.Children.Add(scroll);
        Content = root;
        Opened += (_, _) => Update(false);
        Closed += (_, _) => { _completion.TrySetResult(null); image.Source = null; bitmap.Dispose(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    public static Task<ImageFrame?> EditAsync(ImageFrame source)
    {
        var window = new ScrollTrimWindow(source);
        window.Show(); window.Activate();
        return window._completion.Task;
    }
}
