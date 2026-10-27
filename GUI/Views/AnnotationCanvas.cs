using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Kscan.App.Services;
using Kscan.Core.Imaging;
using SkiaSharp;

namespace Kscan.App.Views;

/// <summary>只接收画布上的指针事件；工具栏与文字输入不参与绘图路由。</summary>
internal sealed class AnnotationCanvas : Control, IDisposable
{
    public static readonly StyledProperty<IBrush?> BackgroundBrushProperty = AvaloniaProperty.Register<AnnotationCanvas, IBrush?>(nameof(BackgroundBrush), Brushes.Transparent);
    public static readonly StyledProperty<IBrush?> AccentBrushProperty = AvaloniaProperty.Register<AnnotationCanvas, IBrush?>(nameof(AccentBrush), Brushes.DodgerBlue);
    public static readonly StyledProperty<IBrush?> SelectionBrushProperty = AvaloniaProperty.Register<AnnotationCanvas, IBrush?>(nameof(SelectionBrush), Brushes.Transparent);
    public static readonly StyledProperty<IBrush?> HandleBrushProperty = AvaloniaProperty.Register<AnnotationCanvas, IBrush?>(nameof(HandleBrush), Brushes.White);
    public IBrush? BackgroundBrush { get => GetValue(BackgroundBrushProperty); set => SetValue(BackgroundBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? SelectionBrush { get => GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    public IBrush? HandleBrush { get => GetValue(HandleBrushProperty); set => SetValue(HandleBrushProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BackgroundBrushProperty) Redraw();
        else if (change.Property == AccentBrushProperty || change.Property == SelectionBrushProperty || change.Property == HandleBrushProperty) InvalidateVisual();
    }
    private readonly AnnotationDocument _document;
    private readonly SKBitmap _source;
    private readonly ImageFrame _originalFrame;
    private WriteableBitmap? _preview;
    private SKBitmap? _surface;
    private AnnotationItem? _draft;
    private SKPoint? _hover;
    private SKPoint _start;
    private SKPoint _lastImage;
    private Point _lastView;
    private bool _drawing;
    private bool _moving;
    private bool _panning;
    private bool _marquee;
    private bool _addSelection;
    private int _resize = -1;
    private int _resizeItem = -1;
    private SKRect? _selectionBox;
    private bool _disposed;
    private bool _fit;
    private bool _actual = true;
    private AnnotationTool _tool = AnnotationTool.Rect;
    public AnnotationViewport Viewport { get; }
    public AnnotationTool Tool => _tool;
    public uint Color { get; set; } = 0xFFFF3B30;
    public float Thickness { get; set; } = 4;
    public int Block { get; set; } = 12;
    public SKPoint? TextAnchor { get; set; }
    public int? EditingTextIndex { get; set; }
    public bool AllowViewNavigation { get; set; } = true;
    public event Action? BeforePointerPressed;
    public event Action? ViewChanged;
    public event Action<SKPoint>? TextRequested;
    public event Action? TextEditRequested;
    public event Action<string>? Hint;

    public AnnotationCanvas(ImageFrame source, AnnotationDocument document)
    {
        _document = document;
        _originalFrame = source;
        _source = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        System.Runtime.InteropServices.Marshal.Copy(source.Pixels, 0, _source.GetPixels(), source.Pixels.Length);
        Viewport = new AnnotationViewport(source.Width, source.Height);
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        SizeChanged += (_, _) =>
        {
            if (_fit || Viewport.Scale == 0) Viewport.Fit(Bounds.Size, !_fit, DeviceScale);
            Redraw();
        };
        _document.Changed += DocumentChanged;
    }

    private void DocumentChanged()
    {
        if (IsEffectivelyVisible) Redraw();
    }

    public void SetTool(AnnotationTool tool)
    {
        CancelGesture();
        _tool = tool;
        _document.Select(-1);
        Cursor = new Cursor(tool == AnnotationTool.Select ? StandardCursorType.Arrow : StandardCursorType.Cross);
        Focus();
    }

    public void Fit(bool actual = false)
    {
        _fit = !actual;
        _actual = actual;
        Viewport.Fit(Bounds.Size, actual, DeviceScale);
        Redraw();
        Focus();
    }

    public void AlignTop()
    {
        Viewport.Pan(new Vector(0, 24 - Viewport.Offset.Y));
        Redraw();
    }

    public void Zoom(double factor)
    {
        _fit = false;
        _actual = false;
        Viewport.Zoom(new Point(Bounds.Width / 2, Bounds.Height / 2), factor);
        Redraw();
    }

    public bool CancelGesture()
    {
        var active = _draft is not null || _moving || _drawing || _marquee || _resize >= 0;
        _drawing = _moving = _panning = _marquee = false;
        _resize = _resizeItem = -1;
        _draft = null;
        _selectionBox = null;
        _document.Cancel();
        Redraw();
        return active;
    }

    public bool FinishPolyline()
    {
        if (_draft?.Tool != AnnotationTool.Polyline) return false;
        var item = _draft;
        _draft = null;
        if (item.Points.Count >= 2) _document.Add(item);
        Redraw();
        return true;
    }

    public bool HasGesture => _draft is not null || _moving || _drawing || _marquee || _resize >= 0;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        BeforePointerPressed?.Invoke();
        Focus();
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed) { CancelGesture(); e.Handled = true; return; }
        if (props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            if (!AllowViewNavigation) { e.Handled = true; return; }
            _panning = true;
            _lastView = p;
            _fit = false;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        var point = Viewport.ToImage(p, true);
        if (_tool == AnnotationTool.Select)
        {
            _lastImage = point;
            _addSelection = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (_document.Selection.Count == 1)
            {
                var index = _document.Selection.Single();
                var item = _document.Items[index];
                if (item.Points.Count == 2)
                    for (var handle = 0; handle < 2; handle++)
                        if (Distance(Viewport.ToView(item.Points[handle]), p) <= 9)
                        {
                            _resize = handle;
                            _resizeItem = index;
                            _document.Begin();
                            e.Pointer.Capture(this);
                            e.Handled = true;
                            return;
                        }
            }
            var hit = _document.HitTest(Viewport.ToImage(p), (float)(6 / Viewport.Scale));
            if (hit >= 0)
            {
                if (_addSelection || !_document.Selection.Contains(hit)) _document.Select(hit, _addSelection);
                if (e.ClickCount == 2 && _document.Items[hit].Tool == AnnotationTool.Text)
                {
                    TextEditRequested?.Invoke();
                    e.Handled = true;
                    return;
                }
                _moving = _document.Selection.Contains(hit);
                _document.Begin();
            }
            else
            {
                if (!_addSelection) _document.Select(-1);
                _start = point;
                _marquee = true;
            }
            e.Pointer.Capture(this);
        }
        else if (Viewport.Contains(p))
        {
            if (_tool == AnnotationTool.Text) TextRequested?.Invoke(point);
            else if (_tool == AnnotationTool.Number)
                _document.Add(NewItem([point], _document.NextNumber));
            else if (_tool == AnnotationTool.Polyline)
            {
                if (_draft is null) _draft = NewItem([point]);
                else if (SKPoint.Distance(_draft.Points[^1], point) > .5f) _draft.Points.Add(point);
                if (e.ClickCount == 2) FinishPolyline();
                else { Hint?.Invoke("单击添加节点 · 双击或 Enter 结束折线 · Esc 取消"); Redraw(); }
            }
            else
            {
                _start = point;
                _draft = NewItem([point, point]);
                _drawing = true;
                e.Pointer.Capture(this);
            }
        }
        e.Handled = true;
    }

    private AnnotationItem NewItem(List<SKPoint> points, int number = 0) => new()
    {
        Tool = _tool, Points = points, Color = Color, Thickness = Thickness, Block = Block, Number = number,
    };

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        var point = Viewport.ToImage(p, true);
        if (_panning)
        {
            Viewport.Pan(p - _lastView);
            _lastView = p;
            Redraw();
        }
        else if (_resize >= 0) _document.Resize(_resizeItem, _resize, point);
        else if (_moving)
        {
            _document.Move(point.X - _lastImage.X, point.Y - _lastImage.Y);
            _lastImage = point;
        }
        else if (_marquee)
        {
            _selectionBox = Rectangle(_start, point);
            InvalidateVisual();
        }
        else if (_drawing && _draft is not null)
        {
            if (_tool is AnnotationTool.Pen or AnnotationTool.Highlight)
            {
                if (SKPoint.Distance(_draft.Points[^1], point) >= .5f) _draft.Points.Add(point);
            }
            else
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) point = Constrain(_start, point, _tool);
                _draft.Points[^1] = point;
            }
            Redraw();
        }
        else if (_draft?.Tool == AnnotationTool.Polyline)
        {
            _hover = point;
            Redraw();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_panning) _panning = false;
        else if (_moving || _resize >= 0)
        {
            _moving = false;
            _resize = _resizeItem = -1;
            _document.Commit();
        }
        else if (_marquee)
        {
            _marquee = false;
            if (_selectionBox is { } box) _document.SelectArea(box, _addSelection);
            _selectionBox = null;
        }
        else if (_drawing && _draft is { } draft)
        {
            _drawing = false;
            _draft = null;
            var b = draft.Bounds();
            var line = draft.Tool is AnnotationTool.Line or AnnotationTool.Arrow or AnnotationTool.Dimension or AnnotationTool.Pen or AnnotationTool.Highlight;
            if (line ? draft.Points.Any(x => SKPoint.Distance(x, draft.Points[0]) >= 1) : b.Width >= 1 && b.Height >= 1)
                _document.Add(draft);
        }
        e.Pointer.Capture(null);
        Redraw();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drawing || _moving || _marquee || _resize >= 0 || _panning) CancelGesture();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!AllowViewNavigation)
        {
            Hint?.Invoke("原位标注保持原始大小；需要缩放或平移请点击「独立编辑」");
            e.Handled = true;
            return;
        }
        _fit = false;
        _actual = false;
        Viewport.Zoom(e.GetPosition(this), Math.Pow(1.15, e.Delta.Y));
        Redraw();
        e.Handled = true;
    }

    private static SKPoint Constrain(SKPoint a, SKPoint b, AnnotationTool tool)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (tool is AnnotationTool.Line or AnnotationTool.Arrow or AnnotationTool.Dimension)
        {
            var length = Math.Sqrt(dx * dx + dy * dy);
            var angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * Math.PI / 4;
            return new SKPoint(a.X + (float)(length * Math.Cos(angle)), a.Y + (float)(length * Math.Sin(angle)));
        }
        var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new SKPoint(a.X + Math.Sign(dx) * side, a.Y + Math.Sign(dy) * side);
    }

    internal static SKRect Rectangle(SKPoint a, SKPoint b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    public void Redraw()
    {
        if (_disposed || Bounds.Width < 1 || Bounds.Height < 1 || Viewport.Scale <= 0) return;
        var dpi = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var items = _document.Items.Where((_, index) => index != EditingTextIndex).ToList();
        if (_draft is { } draft)
        {
            var copy = AnnotationDocument.Copy(draft);
            if (copy.Tool == AnnotationTool.Polyline && _hover is { } hover) copy.Points.Add(hover);
            items.Add(copy);
        }
        var size = new PixelSize((int)Math.Ceiling(Bounds.Width * dpi), (int)Math.Ceiling(Bounds.Height * dpi));
        if (_preview?.PixelSize != size)
        {
            _preview?.Dispose();
            _surface?.Dispose();
            _preview = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            _surface = new SKBitmap(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        }
        AnnotationRenderer.PaintView(_source, items, _surface!,
            (float)(Viewport.Scale * dpi), new SKPoint((float)(Viewport.Offset.X * dpi), (float)(Viewport.Offset.Y * dpi)),
            BackgroundBrush is ISolidColorBrush brush ? brush.Color.ToUInt32() : 0u);
        using (var buffer = _preview!.Lock())
        using (var pixels = _surface!.PeekPixels())
            if (!pixels.ReadPixels(_surface.Info, buffer.Address, buffer.RowBytes))
                throw new InvalidOperationException("无法更新标注画布缓冲");
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    public ImageFrame Export()
    {
        if (_document.Items.Count == 0) return _originalFrame;
        using var rendered = AnnotationRenderer.Render(_source, _document.Items);
        var pixels = new byte[rendered.Width * rendered.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(rendered.GetPixels(), pixels, 0, pixels.Length);
        return new ImageFrame(rendered.Width, rendered.Height, pixels);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(BackgroundBrush ?? Brushes.Transparent, new Rect(Bounds.Size));
        if (_preview is { } preview) context.DrawImage(preview, new Rect(Bounds.Size));
        var pen = new Pen(AccentBrush, 1.5);
        foreach (var index in _document.Selection)
        {
            if (index == EditingTextIndex) continue;
            var item = _document.Items[index];
            var b = AnnotationDocument.Bounds(item);
            context.DrawRectangle(null, pen, ViewRect(b));
            if (_document.Selection.Count == 1 && item.Points.Count == 2)
                foreach (var point in item.Points)
                {
                    var p = Viewport.ToView(point);
                    context.DrawRectangle(HandleBrush, pen, new Rect(p.X - 4, p.Y - 4, 8, 8));
                }
        }
        if (_selectionBox is { } box) context.DrawRectangle(SelectionBrush, pen, ViewRect(box));
        if (TextAnchor is { } anchor)
        {
            var p = Viewport.ToView(anchor);
            context.DrawLine(pen, new Point(p.X - 8, p.Y), new Point(p.X + 8, p.Y));
            context.DrawLine(pen, new Point(p.X, p.Y - 8), new Point(p.X, p.Y + 8));
        }
    }

    private Rect ViewRect(SKRect b) => new(Viewport.ToView(new SKPoint(b.Left, b.Top)), Viewport.ToView(new SKPoint(b.Right, b.Bottom)));
    private double DeviceScale => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
    public void DisplayScaleChanged()
    {
        if (_actual) Viewport.Fit(Bounds.Size, true, DeviceScale);
        Redraw();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _document.Changed -= DocumentChanged;
        _preview?.Dispose();
        _surface?.Dispose();
        _source.Dispose();
    }
}
