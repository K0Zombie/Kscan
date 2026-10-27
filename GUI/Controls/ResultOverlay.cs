using System.Collections;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Kscan.App.Services;
using Kscan.Core.Imaging;
using Kscan.Core.Model;

namespace Kscan.App.Controls;

/// <summary>结果面板里可以被高亮的一项（由 <c>ResultItem</c> 实现）。</summary>
public interface IResultMarker
{
    /// <summary>码在图像里的角点（像素坐标）。</summary>
    IReadOnlyList<PointD> Points { get; }

    /// <summary>列表里的序号，从 0 开始；界面显示时 +1。</summary>
    int Index { get; }

    string Text { get; }

    string Format { get; }
}

/// <summary>
/// 图片预览 + 多码高亮层：把引擎给出的码角点画成凸包，悬停时放大高亮并弹出内容浮窗，
/// <b>点在框上就直接把该码内容交给 <see cref="CopyCommand"/></b>（默认绑到"复制到剪贴板"）。
/// </summary>
/// <remarks>
/// 参考旧版的 <c>PicWindow</c>（凸包 + GoPanel），但这里：
/// <list type="bullet">
/// <item>浮窗是<b>只读</b>的（<c>IsHitTestVisible=false</c>），不与命中测试互相干扰，
/// 因此不存在"指针移进浮窗就丢掉高亮"的抖动；</item>
/// <item>坐标换算走 Core 的 <see cref="UniformFit"/>（以 <c>Image.Stretch=Uniform</c> 的实际几何为准），
/// 而不是按控件尺寸直接拉伸 —— 后者在图片长宽比与容器不一致时会让框整体偏移。</item>
/// </list>
/// 注意两个已踩过的布局陷阱：
/// <list type="number">
/// <item><c>Canvas</c> 的测量结果是 0，所以<b>不能</b>拿它的 <c>Bounds</c> 当渲染区尺寸
/// （首次布局后尺寸恒为 0，框一个也画不出来）；这里统一用控件自身的 <c>Bounds</c>。</item>
/// <item>结果数据可能在布局稳定之后才到达，控件尺寸<b>不会</b>再变，<c>SizeChanged</c> 于是不触发。
/// 因此重绘挂在 <c>LayoutUpdated</c> 上，并以尺寸变化做守卫（尺寸没变就直接返回，不会形成布局循环）。</item>
/// </list>
/// </remarks>
public sealed class ResultOverlay : Grid
{
    private const int MaxCalloutChars = 200;

    private static readonly IBrush IdleStroke = new SolidColorBrush(Color.Parse("#4C8BF5"));
    // 填充压得很淡：它会盖在二维码上，实测 22% 的填充足以让"把这张图再识别一次"失败
    // （Core 那边还有"去噪重试"兜底，两头一起降风险）
    private static readonly IBrush IdleFill = new SolidColorBrush(Color.Parse("#4C8BF5"), 0.08);
    private static readonly IBrush ActiveStroke = new SolidColorBrush(Color.Parse("#FFC107"));
    private static readonly IBrush ActiveFill = new SolidColorBrush(Color.Parse("#FFC107"), 0.12);

    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    private readonly Border _callout;
    private readonly TextBlock _calloutTitle = new() { FontSize = 11, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _calloutText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
    private readonly TextBlock _calloutHint = new() { FontSize = 11, Opacity = 0.65 };
    private readonly List<MarkerVisual> _markers = [];

    /// <summary>上一次重建时用的控件尺寸；(-1,-1) 表示已作废、等下一次布局回调重建。</summary>
    private Size _renderedFor = new(-1, -1);

    public ResultOverlay()
    {
        Children.Add(_image);
        Children.Add(_canvas);

        _callout = new Border
        {
            IsHitTestVisible = false,
            IsVisible = false,
            MaxWidth = 320,
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse("#F0181820")),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse("#4C8BF5")),
            Child = new StackPanel { Spacing = 4, Children = { _calloutTitle, _calloutText, _calloutHint } },
        };

        _canvas.Children.Add(_callout);

        LayoutUpdated += (_, _) => RebuildIfResized();

        // 指针离开整个预览区（含停在空白处）就清掉高亮
        _canvas.PointerExited += (_, _) => SetHighlight(-1);

        // 浮窗里的"点击复制"提示也要跟着语言走（重建时会重新取文案）
        I18n.Current.Changed += (_, _) => Invalidate();
    }

    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<ResultOverlay, IImage?>(nameof(Source));

    /// <summary>原图像素宽（不是控件宽）——高亮框的坐标以它为准。</summary>
    public static readonly StyledProperty<double> ImageWidthProperty =
        AvaloniaProperty.Register<ResultOverlay, double>(nameof(ImageWidth));

    public static readonly StyledProperty<double> ImageHeightProperty =
        AvaloniaProperty.Register<ResultOverlay, double>(nameof(ImageHeight));

    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<ResultOverlay, IEnumerable?>(nameof(Items));

    /// <summary>当前高亮的项：-1 表示无。控件内部悬停会写回该值，列表悬停也可以从外部写。</summary>
    public static readonly StyledProperty<int> HighlightIndexProperty =
        AvaloniaProperty.Register<ResultOverlay, int>(nameof(HighlightIndex), -1);

    /// <summary>点击高亮框时要执行的动作，参数是被点中的那一项（界面上绑到"复制"命令）。</summary>
    public static readonly StyledProperty<ICommand?> CopyCommandProperty =
        AvaloniaProperty.Register<ResultOverlay, ICommand?>(nameof(CopyCommand));

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double ImageWidth
    {
        get => GetValue(ImageWidthProperty);
        set => SetValue(ImageWidthProperty, value);
    }

    public double ImageHeight
    {
        get => GetValue(ImageHeightProperty);
        set => SetValue(ImageHeightProperty, value);
    }

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public int HighlightIndex
    {
        get => GetValue(HighlightIndexProperty);
        set => SetValue(HighlightIndexProperty, value);
    }

    public ICommand? CopyCommand
    {
        get => GetValue(CopyCommandProperty);
        set => SetValue(CopyCommandProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty)
        {
            _image.Source = change.GetNewValue<IImage?>();
            Invalidate();
        }
        else if (change.Property == ImageWidthProperty || change.Property == ImageHeightProperty)
        {
            Invalidate();
        }
        else if (change.Property == ItemsProperty)
        {
            Resubscribe(change.GetOldValue<IEnumerable?>(), change.GetNewValue<IEnumerable?>());
            Invalidate();
        }
        else if (change.Property == HighlightIndexProperty)
        {
            ApplyHighlight();
        }
    }

    /// <summary>请求重绘：作废缓存尺寸后立刻试一次，若此时还没布局则等 <c>LayoutUpdated</c> 兜底。</summary>
    private void Invalidate()
    {
        _renderedFor = new Size(-1, -1);
        RebuildIfResized();
    }

    private void RebuildIfResized()
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0 || size == _renderedFor)
        {
            return;
        }

        _renderedFor = size;
        Rebuild();
    }

    private void Resubscribe(IEnumerable? oldItems, IEnumerable? newItems)
    {
        if (oldItems is INotifyCollectionChanged oldNotify)
        {
            oldNotify.CollectionChanged -= OnItemsChanged;
        }

        if (newItems is INotifyCollectionChanged newNotify)
        {
            newNotify.CollectionChanged += OnItemsChanged;
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Invalidate();

    /// <summary>按当前图片与控件尺寸重建所有高亮图形。项数很少，全量重建最简单也最不容易出错。</summary>
    private void Rebuild()
    {
        _canvas.Children.Clear();
        _markers.Clear();
        _callout.IsVisible = false;
        _canvas.Children.Add(_callout);

        if (Items is null)
        {
            return;
        }

        var box = Bounds;
        var fit = UniformFit.Create(ImageWidth, ImageHeight, box.Width, box.Height);
        if (!fit.IsUsable)
        {
            return;
        }

        foreach (var item in Items)
        {
            if (item is not IResultMarker marker || marker.Points.Count == 0)
            {
                continue;
            }

            var hull = HullInControl(marker, fit);
            var shape = CreateShape(hull);
            if (shape is null)
            {
                continue;
            }

            shape.Tag = marker;

            // 框本身可点：手型光标 + 左键按下即触发绑定的命令（默认"复制该码内容"）
            shape.Cursor = new Cursor(StandardCursorType.Hand);
            shape.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    return;
                }

                e.Handled = true;
                Activate(marker);
            };

            shape.PointerEntered += (_, _) => SetHighlight(marker.Index);
            shape.PointerExited += (_, _) =>
            {
                // 只在仍高亮这一项时清掉，避免相邻图形之间切换时闪一下
                if (HighlightIndex == marker.Index)
                {
                    SetHighlight(-1);
                }
            };

            _canvas.Children.Add(shape);
            _markers.Add(new MarkerVisual(marker, shape, hull));
        }

        ApplyHighlight();
    }

    /// <summary>点中某个框 → 执行绑定的命令，参数是被点中的那一项。</summary>
    private void Activate(IResultMarker marker)
    {
        var command = CopyCommand;

        if (command?.CanExecute(marker) == true)
        {
            command.Execute(marker);
        }
    }

    private static Shape? CreateShape(IReadOnlyList<Point> hull) => hull.Count switch
    {
        0 => null,
        1 => CreateDot(hull[0]),
        // 1D 码常常只给两个端点（码的中心线），画线段而不是多边形
        2 => new Line
        {
            StartPoint = hull[0],
            EndPoint = hull[1],
            Stroke = IdleStroke,
            StrokeThickness = 4,
            StrokeLineCap = PenLineCap.Round,
        },
        _ => new Polygon
        {
            Points = new AvaloniaList<Point>(hull),
            Stroke = IdleStroke,
            StrokeThickness = 2,
            Fill = IdleFill,
        },
    };

    /// <summary>引擎只给了一个定位点时的兜底：画一个小圆点。</summary>
    private static Shape CreateDot(Point center)
    {
        var dot = new Ellipse
        {
            Width = 14,
            Height = 14,
            Stroke = IdleStroke,
            StrokeThickness = 2,
            Fill = IdleFill,
        };

        Canvas.SetLeft(dot, center.X - 7);
        Canvas.SetTop(dot, center.Y - 7);
        return dot;
    }

    private static IReadOnlyList<Point> HullInControl(IResultMarker marker, UniformFit fit)
    {
        var hull = ConvexHull.Compute(marker.Points);
        var mapped = new Point[hull.Count];
        for (var i = 0; i < hull.Count; i++)
        {
            var point = fit.Map(hull[i]);
            mapped[i] = new Point(point.X, point.Y);
        }

        return mapped;
    }

    private void SetHighlight(int index)
    {
        if (HighlightIndex != index)
        {
            SetCurrentValue(HighlightIndexProperty, index);
        }
    }

    private void ApplyHighlight()
    {
        var index = HighlightIndex;

        foreach (var marker in _markers)
        {
            var active = marker.Item.Index == index;
            marker.Shape.Stroke = active ? ActiveStroke : IdleStroke;
            marker.Shape.StrokeThickness = marker.Shape switch
            {
                Line => active ? 7 : 4,
                _ => active ? 4 : 2,
            };
            marker.Shape.Fill = active ? ActiveFill : IdleFill;
        }

        var current = _markers.FirstOrDefault(m => m.Item.Index == index);
        if (current is null || current.HullPoints.Count == 0)
        {
            _callout.IsVisible = false;
            return;
        }

        _calloutTitle.Text = $"#{current.Item.Index + 1} · {current.Item.Format}";
        _calloutText.Text = Excerpt(current.Item.Text);
        _calloutHint.Text = I18n.T("Str.Preview.ClickToCopy");
        _callout.IsVisible = true;

        // 浮窗尺寸只有测量后才知道；量完再夹在可视区内，避免贴边被裁
        _callout.Measure(Size.Infinity);
        var size = _callout.DesiredSize;
        var center = Centroid(current.HullPoints);
        var limitX = Math.Max(0, Bounds.Width - size.Width);
        var limitY = Math.Max(0, Bounds.Height - size.Height);

        var x = Math.Clamp(center.X - (size.Width / 2), 0, limitX);
        var y = center.Y - size.Height - 12;
        if (y < 0)
        {
            y = center.Y + 12;
        }

        Canvas.SetLeft(_callout, x);
        Canvas.SetTop(_callout, Math.Clamp(y, 0, limitY));
    }

    private static Point Centroid(IReadOnlyList<Point> points)
    {
        double x = 0, y = 0;
        foreach (var point in points)
        {
            x += point.X;
            y += point.Y;
        }

        return new Point(x / points.Count, y / points.Count);
    }

    private static string Excerpt(string text) =>
        text.Length <= MaxCalloutChars ? text : string.Concat(text.AsSpan(0, MaxCalloutChars), "…");

    private sealed record MarkerVisual(IResultMarker Item, Shape Shape, IReadOnlyList<Point> HullPoints);
}
