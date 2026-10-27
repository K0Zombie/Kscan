using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using Kscan.App.Services;
using Kscan.Core.Imaging;
using SkiaSharp;

namespace Kscan.App.Views;

internal readonly record struct AnnotationResult(ImageFrame Frame, bool Recognize);

/// <summary>独立标注编辑器；绘图事件仅由画布处理，文本输入保留原生输入法行为。</summary>
internal sealed class AnnotationWindow : Window
{
    private readonly ImageFrame _source;
    private readonly bool _startAtActualSize;
    private readonly AnnotationDocument _document;
    private readonly QuickAnnotationLayout? _quickLayout;
    private readonly List<Flyout> _quickFlyouts = [];
    private bool _expanding;
    private readonly AnnotationCanvas _canvas;
    private readonly TaskCompletionSource<AnnotationResult?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<AnnotationTool, Button> _tools = [];
    private readonly Dictionary<uint, Button> _colors = [];
    private readonly TextBlock _status = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _stats = new() { FontSize = 12 };
    private readonly TextBlock _propertyTitle = new() { FontSize = 18, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _propertyHint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly NumericUpDown _width = new() { Minimum = 1, Maximum = 32, Value = 4, Increment = 1, FormatString = "0", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _block = new() { Minimum = 4, Maximum = 64, Value = 12, Increment = 2, FormatString = "0", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _fontSize = new() { Minimum = 8, Maximum = 192, Value = 24, Increment = 2, FormatString = "0" };
    private readonly NumericUpDown _textWidth = new() { Minimum = 0, Maximum = 12000, Value = 0, Increment = 20, FormatString = "0" };
    private readonly TextBox _text = new()
    {
        Name = "InlineAnnotationText", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        PlaceholderText = "输入文字…", Padding = new Thickness(4), BorderThickness = new Thickness(1),
        Background = Brushes.Transparent, IsVisible = false,
    };
    private readonly Canvas _textOverlay = new() { ClipToBounds = true };
    private readonly TextBox _customColor = new() { PlaceholderText = "#RRGGBB", MaxLength = 9 };
    private Button _undo = null!;
    private Button _redo = null!;
    private Button _delete = null!;
    private Button _applyText = null!;
    private Button _editText = null!;
    private StackPanel _textSection = null!;
    private StackPanel _mosaicSection = null!;
    private bool _updating;
    private bool _finished;
    private SKPoint? _textPoint;
    private uint _color = 0xFFFF3B30;
    private int? _textSelection;
    private string? _loadedText;
    private bool _inlineEditing;
    private int? _editingIndex;

    private static readonly (AnnotationTool Tool, string Name, string Path, string Hint)[] ToolDefinitions =
    [
        (AnnotationTool.Select, "选择", "M 5,3 L 5,20 L 10,15 L 14,22 L 17,20 L 13,13 L 20,12 Z", "点击选择 · Shift 多选 · 空白处拖框 · 拖动移动 · 端点调整大小"),
        (AnnotationTool.Rect, "矩形", "M 3,5 L 21,5 L 21,19 L 3,19 Z", "拖动绘制矩形 · 按住 Shift 绘制正方形"),
        (AnnotationTool.Circle, "椭圆", "M 12,3 A 9,9 0 1 1 11.99,3 Z", "拖动绘制椭圆 · 按住 Shift 绘制圆形"),
        (AnnotationTool.Arrow, "箭头", "M 3,19 L 14,8 L 10,4 L 21,3 L 20,14 L 16,10 L 5,21 Z", "拖动绘制箭头 · Shift 锁定 45° 方向"),
        (AnnotationTool.Line, "直线", "M 4,20 L 20,4", "拖动绘制直线 · Shift 锁定 45° 方向"),
        (AnnotationTool.Pen, "画笔", "M 4,19 L 6,13 L 16,3 L 21,8 L 11,18 Z M 6,13 L 11,18", "按住左键自由绘制 · Esc 取消当前笔画"),
        (AnnotationTool.Text, "文字", "M 3,4 L 21,4 M 12,4 L 12,21 M 7,21 L 17,21", "点击图片直接输入 · Enter 换行 · Ctrl+Enter 确认 · 选择模式双击修改"),
        (AnnotationTool.Number, "序号", "M 12,2 A 10,10 0 1 1 11.99,2 Z M 9,9 L 12,6 L 12,18 M 9,18 L 15,18", "点击放置序号 · 自动递增 · 撤销后恢复编号"),
        (AnnotationTool.Mosaic, "马赛克", "M 3,3 L 21,3 L 21,21 L 3,21 Z M 9,3 L 9,21 M 15,3 L 15,21 M 3,9 L 21,9 M 3,15 L 21,15", "拖动遮盖区域 · 右侧调整颗粒大小"),
        (AnnotationTool.Highlight, "高亮", "M 4,15 L 14,5 L 20,11 L 10,21 Z M 3,21 L 21,21", "按住左键绘制半透明高亮"),
        (AnnotationTool.Polyline, "折线", "M 3,18 L 9,6 L 15,16 L 21,4", "单击添加节点 · 双击或 Enter 结束折线 · Esc 取消"),
        (AnnotationTool.Dimension, "尺寸", "M 3,8 L 3,16 M 21,8 L 21,16 M 3,12 L 21,12 M 7,9 L 3,12 L 7,15 M 17,9 L 21,12 L 17,15", "拖动测量图片像素距离 · Shift 锁定方向"),
        (AnnotationTool.Crop, "裁剪", "M 6,2 L 6,18 L 22,18 M 2,6 L 18,6 L 18,22", "拖动设置导出范围 · 再画一次替换裁剪 · Delete 移除选中裁剪"),
        (AnnotationTool.Spotlight, "聚光", "M 3,3 L 21,3 L 21,21 L 3,21 Z M 8,8 L 16,8 L 16,16 L 8,16 Z", "拖动突出区域，区域外压暗"),
        (AnnotationTool.ZoomLens, "放大镜", "M 10,3 A 7,7 0 1 1 9.99,3 Z M 15,15 L 22,22 M 7,10 L 13,10 M 10,7 L 10,13", "拖动选择局部，旁边显示 2 倍放大图"),
    ];

    public AnnotationWindow(ImageFrame source, AnnotationDocument? document = null, QuickAnnotationLayout? quickLayout = null, bool startAtActualSize = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _startAtActualSize = startAtActualSize;
        _document = document ?? new AnnotationDocument();
        _quickLayout = quickLayout;
        _canvas = new AnnotationCanvas(source, _document) { Name = "AnnotationCanvas" };
        Title = "Kscan · 标注编辑器";
        Width = 1180; Height = 760; MinWidth = 900; MinHeight = 560;
        CanResize = true;
        ShowInTaskbar = true;
        Theme(this, BackgroundProperty, "KscanBackgroundBrush");
        Theme(this, ForegroundProperty, "KscanForegroundBrush");
        Theme(this, FontFamilyProperty, "KscanFontFamily");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (_quickLayout is not null)
        {
            WindowDecorations = WindowDecorations.None;
            WindowStartupLocation = WindowStartupLocation.Manual;
            CanResize = false;
            Topmost = true;
            ShowInTaskbar = false;
            MinWidth = MinHeight = 0;
            Background = Brushes.Transparent;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            _canvas.AllowViewNavigation = false;
        }
        LoadDefaults();
        Content = BuildLayout();
        Theme(_status, TextBlock.ForegroundProperty, "KscanMutedBrush");
        Theme(_stats, TextBlock.ForegroundProperty, "KscanMutedBrush");
        Theme(_propertyTitle, TextBlock.ForegroundProperty, "KscanForegroundBrush");
        Theme(_propertyHint, TextBlock.ForegroundProperty, "KscanMutedBrush");
        Theme(_text, TextBox.BorderBrushProperty, "KscanAccentBrush");
        Theme(_canvas, AnnotationCanvas.BackgroundBrushProperty, "KscanBackgroundBrush");
        Theme(_canvas, AnnotationCanvas.AccentBrushProperty, "KscanAccentBrush");
        Theme(_canvas, AnnotationCanvas.SelectionBrushProperty, "KscanSelectionBrush");
        Theme(_canvas, AnnotationCanvas.HandleBrushProperty, "KscanBackgroundBrush");
        _text.Resources["TextControlBackgroundFocused"] = Brushes.Transparent;
        _text.Resources["TextControlBackgroundPointerOver"] = Brushes.Transparent;
        _text.Resources["TextControlBackground"] = Brushes.Transparent;
        _canvas.TextRequested += BeginText;
        _canvas.TextEditRequested += BeginExistingText;
        _canvas.BeforePointerPressed += () => { if (_inlineEditing) ApplyText(); };
        _canvas.Hint += message => _status.Text = message;
        _canvas.ViewChanged += () => { UpdateStats(); PositionTextEditor(); };
        ScalingChanged += (_, _) => _canvas.DisplayScaleChanged();
        _document.Changed += OnDocumentChanged;
        _width.ValueChanged += (_, _) => ApplyStyle();
        _block.ValueChanged += (_, _) => ApplyStyle();
        _fontSize.ValueChanged += (_, _) => ApplyStyle();
        _textWidth.ValueChanged += (_, _) => ApplyStyle();
        _text.TextChanged += (_, _) => { _applyText.IsEnabled = _inlineEditing && !string.IsNullOrWhiteSpace(_text.Text); PositionTextEditor(); };
        _text.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Enter && args.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                ApplyText();
                args.Handled = true;
            }
            else if (args.Key == Key.Escape)
            {
                CancelText();
                _canvas.Focus();
                args.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Closed += (_, _) =>
        {
            _document.Changed -= OnDocumentChanged;
            _completion.TrySetResult(null);
            _canvas.Dispose();
        };
        SelectTool(AnnotationTool.Rect);
        OnDocumentChanged();
    }

    public Task<AnnotationResult?> Result => _completion.Task;
    internal IReadOnlyList<AnnotationItem> ItemsForTest => _document.Items;
    internal Control CanvasForTest => _canvas;
    internal AnnotationDocument DocumentForTest => _document;
    internal bool IsQuickForTest => _quickLayout is not null;
    internal AnnotationWindow? ExpandedEditorForTest { get; private set; }
    internal (double Scale, double OffsetX, double OffsetY) DisplayTransformForTest() => (_canvas.Viewport.Scale, _canvas.Viewport.Offset.X, _canvas.Viewport.Offset.Y);

    public static Task<AnnotationResult?> EditAsync(ImageFrame source, bool startAtActualSize = false)
    {
        var started = Environment.TickCount64;
        var window = new AnnotationWindow(source, startAtActualSize: startAtActualSize);
        window.Show(); window.Activate();
        Dispatcher.UIThread.Post(() => GuiLog.Write($"annotation-v3: ready {Environment.TickCount64 - started}ms {source.Width}x{source.Height}"));
        return window.Result;
    }

    /// <summary>仅截图并标注调用；扫码、截图贴图和剪贴板编辑均不经过此路由。</summary>
    public static Task<AnnotationResult?> EditCaptureAsync(RegionSelection selection)
    {
        QuickAnnotationLayout? layout = null;
        if (selection.ScreenRect is { } region && region.Width == selection.Frame.Width && region.Height == selection.Frame.Height)
        {
            var screens = TopLevelAccessor.GetScreens();
            if (screens is not null)
                foreach (var screen in screens.All)
                {
                    layout = QuickAnnotationLayout.TryCreate(region, screen.Bounds, screen.WorkingArea, screen.Scaling);
                    if (layout is not null) break;
                }
        }
        GuiLog.Write($"annotation: capture route={(layout is null ? "independent" : "quick")} image={selection.Frame.Width}x{selection.Frame.Height} region={selection.ScreenRect}");
        if (layout is null) return EditAsync(selection.Frame);
        var window = new AnnotationWindow(selection.Frame, quickLayout: layout);
        window.Show();
        window.Activate();
        return window.Result;
    }

    private void LoadDefaults()
    {
        try
        {
            var settings = AppSettingsService.Current.Settings;
            _color = ParseColor(settings.AnnotationColor) ?? 0xFFFF3B30;
            _width.Value = (decimal)Math.Clamp(settings.AnnotationThickness, 1, 32);
            AnnotationRenderer.FontFamily = "";
            AnnotationRenderer.ResetTypeface();
        }
        catch (Exception ex) { GuiLog.Write($"annotation-v3: defaults {ex.Message}"); }
        _canvas.Color = _color;
        _canvas.Thickness = (float)(_width.Value ?? 4);
    }

    private Control BuildLayout()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 1) };
        Theme(header, Panel.BackgroundProperty, "KscanPanelBrush");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12) };
        actions.Children.Add(ActionButton("识别", () => Complete(false, true)));
        actions.Children.Add(ActionButton("使用原图", () => Complete(true, false)));
        actions.Children.Add(ActionButton("取消", Close));
        var done = ActionButton("完成并复制", () => Complete(false, false));
        done.FontWeight = FontWeight.SemiBold;
        Theme(done, Button.BackgroundProperty, "KscanAccentBrush");
        Theme(done, Button.ForegroundProperty, "KscanAccentForegroundBrush");
        actions.Children.Add(done);
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        header.Children.Add(new StackPanel
        {
            Margin = new Thickness(20, 12), Spacing = 3,
            Children = { Theme(new TextBlock { Text = "标注", FontSize = 20, FontWeight = FontWeight.SemiBold }, TextBlock.ForegroundProperty, "KscanForegroundBrush"),
                Theme(new TextBlock { Text = "绘制、选择、编辑，然后复制图片", FontSize = 11 }, TextBlock.ForegroundProperty, "KscanMutedBrush") },
        });
        root.Children.Add(header);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("148,*,232") };
        Grid.SetRow(body, 1); root.Children.Add(body);
        var tools = new StackPanel { Margin = new Thickness(10), Spacing = 10 };
        tools.Children.Add(Label("绘图工具"));
        var toolGrid = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var definition in ToolDefinitions)
        {
            var tool = definition.Tool;
            var icon = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(definition.Path), StrokeThickness = 1.6,
                Width = 24, Height = 24, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center,
            };
            var button = new Button
            {
                Name = "Tool" + tool, Width = 60, Height = 61, Padding = new Thickness(4), Margin = new Thickness(2),
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new StackPanel { Spacing = 4, Children = { icon, new TextBlock { Text = definition.Name, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center } } },
            };
            Theme(icon, Avalonia.Controls.Shapes.Shape.StrokeProperty, "KscanForegroundBrush");
            if (tool == AnnotationTool.Arrow) Theme(icon, Avalonia.Controls.Shapes.Shape.FillProperty, "KscanForegroundBrush");
            Theme(((StackPanel)button.Content!).Children.OfType<TextBlock>().Single(), TextBlock.ForegroundProperty, "KscanForegroundBrush");
            Theme(button, Button.CornerRadiusProperty, "KscanCornerRadius");
            ToolTip.SetTip(button, definition.Hint);
            Avalonia.Automation.AutomationProperties.SetName(button, definition.Name);
            button.Click += (_, _) => SelectTool(tool);
            _tools[tool] = button; toolGrid.Children.Add(button);
        }
        tools.Children.Add(toolGrid);
        body.Children.Add(Theme(new ScrollViewer { Content = tools, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, ScrollViewer.BackgroundProperty, "KscanPanelBrush"));
        var workspace = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetColumn(workspace, 1); body.Children.Add(workspace);
        var viewBar = new WrapPanel { Margin = new Thickness(8), Orientation = Orientation.Horizontal };
        _undo = ActionButton("撤销", () => { CancelText(); _canvas.CancelGesture(); _document.Undo(); _canvas.Focus(); });
        _redo = ActionButton("重做", () => { CancelText(); _canvas.CancelGesture(); _document.Redo(); _canvas.Focus(); });
        ToolTip.SetTip(_undo, "Ctrl+Z"); ToolTip.SetTip(_redo, "Ctrl+Y / Ctrl+Shift+Z");
        viewBar.Children.Add(_undo); viewBar.Children.Add(_redo);
        viewBar.Children.Add(ActionButton("−", () => _canvas.Zoom(1 / 1.25)));
        viewBar.Children.Add(ActionButton("+", () => _canvas.Zoom(1.25)));
        viewBar.Children.Add(ActionButton("适应", () => _canvas.Fit()));
        viewBar.Children.Add(ActionButton("100%", () => _canvas.Fit(true)));
        workspace.Children.Add(viewBar);
        var canvasHost = new Grid { ClipToBounds = true };
        canvasHost.Children.Add(_canvas);
        _textOverlay.Children.Add(_text);
        canvasHost.Children.Add(_textOverlay);
        Grid.SetRow(canvasHost, 1); workspace.Children.Add(canvasHost);
        var properties = new StackPanel { Margin = new Thickness(18), Spacing = 14 };
        properties.Children.Add(Label("属性")); properties.Children.Add(_propertyTitle); properties.Children.Add(_propertyHint);
        properties.Children.Add(new Separator()); properties.Children.Add(Label("颜色"));
        var palette = new WrapPanel();
        foreach (var color in new uint[] { 0xFFFF3B30, 0xFFFF9500, 0xFFFFCC00, 0xFF34C759, 0xFF0A84FF, 0xFFBF5AF2, 0xFFFFFFFF, 0xFF000000 })
        {
            var swatch = new Button { Width = 34, Height = 30, Margin = new Thickness(3), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromUInt32(color)), BorderThickness = new Thickness(2) };
            Theme(swatch, Button.CornerRadiusProperty, "KscanCornerRadius");
            ToolTip.SetTip(swatch, $"#{color & 0xFFFFFF:X6}");
            swatch.Click += (_, _) => ChangeColor(color);
            _colors[color] = swatch; palette.Children.Add(swatch);
        }
        properties.Children.Add(palette);
        var custom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        custom.Children.Add(_customColor);
        var setColor = ActionButton("设置", () =>
        {
            if (ParseColor(_customColor.Text) is { } color) ChangeColor(color);
            else _status.Text = "颜色格式无效，请输入 #RRGGBB 或 #AARRGGBB";
        });
        Grid.SetColumn(setColor, 1); custom.Children.Add(setColor); properties.Children.Add(custom);
        properties.Children.Add(Label("线宽（px）")); properties.Children.Add(_width);
        _mosaicSection = new StackPanel { Spacing = 8, Children = { Label("马赛克颗粒（px）"), _block } };
        properties.Children.Add(_mosaicSection);
        _applyText = ActionButton("确认文字", ApplyText); _applyText.IsEnabled = false;
        _editText = ActionButton("编辑选中文字", BeginExistingText);
        _textSection = new StackPanel { Spacing = 8, Children = { Label("字号（px）"), _fontSize,
            Label("换行宽度（0 = 自动）"), _textWidth,
            Theme(new TextBlock { Text = "在图片上点击直接输入。\nEnter 换行，Ctrl+Enter 确认。\n选择模式双击文字可修改。", TextWrapping = TextWrapping.Wrap, FontSize = 12 }, TextBlock.ForegroundProperty, "KscanMutedBrush"), _editText, _applyText } };
        properties.Children.Add(_textSection); properties.Children.Add(new Separator());
        _delete = ActionButton("删除选中", () => { CancelText(); _canvas.CancelGesture(); _document.Delete(); _canvas.Focus(); });
        properties.Children.Add(_delete);
        properties.Children.Add(ActionButton("清空标注", () => { CancelText(); _canvas.CancelGesture(); _document.Clear(); _canvas.Focus(); }));
        properties.Children.Add(Theme(new TextBlock { Text = "滚轮缩放 · 中键平移\nShift 约束绘图 / 多选\nCtrl+Z 撤销 · Ctrl+Y 重做\nDelete 删除 · 方向键微移\nCtrl+Enter 完成并复制", FontSize = 11, LineHeight = 21, TextWrapping = TextWrapping.Wrap }, TextBlock.ForegroundProperty, "KscanMutedBrush"));
        var propertyScroll = new ScrollViewer { Content = properties, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Theme(propertyScroll, ScrollViewer.BackgroundProperty, "KscanPanelBrush");
        Grid.SetColumn(propertyScroll, 2); body.Children.Add(propertyScroll);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(14, 9) };
        footer.Children.Add(_status); _stats.Margin = new Thickness(18, 0, 0, 0);
        Grid.SetColumn(_stats, 1); footer.Children.Add(_stats); Grid.SetRow(footer, 2); root.Children.Add(footer);
        return _quickLayout is null ? root : BuildQuickLayout(canvasHost, toolGrid, propertyScroll);
    }

    private Control BuildQuickLayout(Grid canvasHost, WrapPanel tools, ScrollViewer properties)
    {
        var layout = _quickLayout!;
        var scale = layout.Scaling;
        static void Detach(Control child)
        {
            if (child.Parent is Panel parent) parent.Children.Remove(child);
        }
        var root = new Canvas { ClipToBounds = true, Name = "QuickAnnotationOverlay" };
        Theme(root, Panel.BackgroundProperty, "KscanEffectiveMaskBrush");
        Detach(canvasHost);
        canvasHost.Width = layout.Image.Width / scale;
        canvasHost.Height = layout.Image.Height / scale;
        Canvas.SetLeft(canvasHost, (layout.Image.X - layout.Monitor.X) / scale);
        Canvas.SetTop(canvasHost, (layout.Image.Y - layout.Monitor.Y) / scale);
        var outline = new Border
        {
            Width = canvasHost.Width + 2, Height = canvasHost.Height + 2,
            BorderThickness = new Thickness(1), IsHitTestVisible = false,
        };
        Canvas.SetLeft(outline, Canvas.GetLeft(canvasHost) - 1);
        Theme(outline, Border.BorderBrushProperty, "KscanAccentBrush");
        Canvas.SetTop(outline, Canvas.GetTop(canvasHost) - 1);
        root.Children.Add(outline);
        root.Children.Add(canvasHost);

        var strip = new WrapPanel { Orientation = Orientation.Horizontal };
        var moreTools = new WrapPanel { Width = 330 };
        var primary = new[] { AnnotationTool.Select, AnnotationTool.Rect, AnnotationTool.Circle, AnnotationTool.Arrow, AnnotationTool.Pen, AnnotationTool.Text, AnnotationTool.Mosaic };
        foreach (var definition in ToolDefinitions)
        {
            var button = _tools[definition.Tool];
            tools.Children.Remove(button);
            var oldContent = (StackPanel)button.Content!;
            var icon = oldContent.Children[0];
            oldContent.Children.Remove(icon);
            button.Content = icon;
            button.Width = 34; button.Height = 34;
            button.Padding = new Thickness(5);
            ToolTip.SetTip(button, definition.Name + " · " + definition.Hint);
            if (primary.Contains(definition.Tool)) strip.Children.Add(button);
            else moreTools.Children.Add(button);
        }
        void AddFlyout(string title, Control content)
        {
            var button = ActionButton(title, () => { });
            var flyout = new Flyout { Content = content, Placement = PlacementMode.Top };
            _quickFlyouts.Add(flyout);
            button.Click += (_, _) => flyout.ShowAt(button);
            strip.Children.Add(button);
        }
        AddFlyout("更多", moreTools);
        Detach(properties);
        properties.Width = 252;
        properties.MaxHeight = Math.Min(550, layout.WorkArea.Height / scale * .7);
        AddFlyout("样式", properties);
        Detach(_undo); Detach(_redo);
        strip.Children.Add(_undo); strip.Children.Add(_redo);
        strip.Children.Add(ActionButton("识别", () => Complete(false, true)));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        actions.Children.Add(ActionButton("独立编辑", () => _ = ExpandQuickAsync()));
        actions.Children.Add(ActionButton("取消", Close));
        var done = ActionButton("完成复制", () => Complete(false, false));
        Theme(done, Button.BackgroundProperty, "KscanAccentBrush");
        Theme(done, Button.ForegroundProperty, "KscanAccentForegroundBrush");
        actions.Children.Add(done);
        Detach(_status);
        _status.VerticalAlignment = VerticalAlignment.Center;
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        bottom.Children.Add(_status);
        Grid.SetColumn(actions, 1);
        bottom.Children.Add(actions);
        var toolbar = new Border
        {
            Name = "QuickAnnotationToolbar", Width = layout.Toolbar.Width / scale, Height = layout.Toolbar.Height / scale,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(8),
            Child = new StackPanel { Spacing = 5, Children = { strip, bottom } },
        };
        Canvas.SetLeft(toolbar, (layout.Toolbar.X - layout.Monitor.X) / scale);
        Theme(toolbar, Border.BackgroundProperty, "KscanBackgroundBrush");
        Theme(toolbar, Border.BorderBrushProperty, "KscanBorderBrush");
        Theme(toolbar, Border.CornerRadiusProperty, "KscanCornerRadius");
        Canvas.SetTop(toolbar, (layout.Toolbar.Y - layout.Monitor.Y) / scale);
        root.Children.Add(toolbar);
        return root;
    }

    private async Task ExpandQuickAsync()
    {
        if (_expanding || _finished) return;
        _expanding = true;
        try
        {
        if (_inlineEditing) ApplyText();
        _canvas.FinishPolyline();
        _canvas.CancelGesture();
        foreach (var flyout in _quickFlyouts) flyout.Hide();
        var editor = new AnnotationWindow(_source, _document);
        ExpandedEditorForTest = editor;
        editor.SelectTool(_canvas.Tool);
        editor.ChangeColor(_color);
        editor._width.Value = _width.Value;
        editor._fontSize.Value = _fontSize.Value;
        editor._textWidth.Value = _textWidth.Value;
        editor.Show();
        editor.Activate();
        Hide();
        var result = await editor.Result;
        _finished = true;
        _completion.TrySetResult(result);
        Close();
        }
        catch (Exception ex)
        {
            _expanding = false;
            if (!IsVisible) Show();
            Activate();
            _status.Text = "打开独立编辑器失败，当前标注仍保留";
            GuiLog.Write($"annotation: expand failed {ex}");
        }
    }

    private static T Theme<T>(T control, AvaloniaProperty property, string resource) where T : AvaloniaObject
    {
        control.Bind(property, new DynamicResourceExtension(resource));
        return control;
    }
    private static TextBlock Label(string text) => Theme(new TextBlock { Text = text, FontSize = 12 }, TextBlock.ForegroundProperty, "KscanMutedBrush");
    private static Button ActionButton(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 7), Margin = new Thickness(2), MinHeight = 32,
            FontSize = 12 };
        Theme(button, Button.ForegroundProperty, "KscanForegroundBrush");
        Theme(button, Button.CornerRadiusProperty, "KscanCornerRadius");
        button.Click += (_, _) => action(); return button;
    }

    private void SelectTool(AnnotationTool tool)
    {
        if (_inlineEditing) ApplyText();
        foreach (var flyout in _quickFlyouts) flyout.Hide();
        _textPoint = null; _canvas.TextAnchor = null; _canvas.SetTool(tool);
        foreach (var (key, button) in _tools)
        {
            if (key == tool)
            {
                Theme(button, Button.BackgroundProperty, "KscanSelectionBrush");
                Theme(button, Button.BorderBrushProperty, "KscanAccentBrush");
            }
            else { button.Background = Brushes.Transparent; button.BorderBrush = Brushes.Transparent; }
        }
        _status.Text = ToolDefinitions.First(x => x.Tool == tool).Hint;
        SyncProperties();
    }

    private void ChangeColor(uint color) { _color = color; ApplyStyle(); UpdateColors(); }
    private void UpdateColors()
    {
        _customColor.Text = $"#{_color & 0xFFFFFF:X6}";
        foreach (var (color, swatch) in _colors)
            Theme(swatch, Button.BorderBrushProperty, color == _color ? "KscanAccentBrush" : "KscanBorderBrush");
    }
    private void ApplyStyle()
    {
        if (_updating) return;
        _canvas.Color = _color; _canvas.Thickness = (float)(_width.Value ?? 4); _canvas.Block = (int)(_block.Value ?? 12);
        if (_inlineEditing) { PositionTextEditor(); return; }
        _document.ApplyStyle(_color, _canvas.Thickness, _canvas.Block, fontSize: (float)(_fontSize.Value ?? 24), textWidth: (float)(_textWidth.Value ?? 0));
    }
    private void SyncProperties()
    {
        _updating = true;
        var selected = _document.Selection;
        AnnotationItem? single = selected.Count == 1 ? _document.Items[selected.Single()] : null;
        var tool = single?.Tool ?? _canvas.Tool;
        _propertyTitle.Text = selected.Count > 1 ? $"已选 {selected.Count} 个标注" : ToolDefinitions.First(x => x.Tool == tool).Name;
        _propertyHint.Text = selected.Count > 0 ? "调整颜色和线宽会立即应用到选中标注。" : "设置将用于下一个标注。";
        if (single is not null) { _color = single.Color; _width.Value = (decimal)single.Thickness; _block.Value = single.Block; }
        _textSection.IsVisible = tool == AnnotationTool.Text && selected.Count <= 1;
        _mosaicSection.IsVisible = tool == AnnotationTool.Mosaic || selected.Any(i => _document.Items[i].Tool == AnnotationTool.Mosaic);
        if (single?.Tool == AnnotationTool.Text)
        {
            var index = selected.Single();
            if (_textSelection != index || _loadedText != single.Text) _text.Text = single.Text;
            _textSelection = index; _loadedText = single.Text;
            _fontSize.Value = (decimal)(single.FontSize > 0 ? single.FontSize : Math.Max(12, single.Thickness * 6));
            _textWidth.Value = (decimal)single.TextWidth;
        }
        else { _textSelection = null; _loadedText = null; }
        _applyText.IsEnabled = _inlineEditing && !string.IsNullOrWhiteSpace(_text.Text);
        _editText.IsEnabled = single?.Tool == AnnotationTool.Text;
        _canvas.Color = _color; _canvas.Thickness = (float)(_width.Value ?? 4); _canvas.Block = (int)(_block.Value ?? 12);
        _delete.IsEnabled = selected.Count > 0; UpdateColors(); _updating = false;
    }
    private void BeginText(SKPoint point)
    {
        _document.Select(-1);
        _editingIndex = null;
        _textPoint = point;
        _text.Text = "";
        OpenTextEditor();
    }
    private void BeginExistingText()
    {
        if (_document.Selection.Count != 1) return;
        var index = _document.Selection.Single();
        var item = _document.Items[index];
        if (item.Tool != AnnotationTool.Text || item.Points.Count == 0) return;
        SyncProperties();
        _editingIndex = index;
        _textPoint = item.Points[0];
        _text.Text = item.Text;
        OpenTextEditor();
        _text.SelectAll();
    }
    private void OpenTextEditor()
    {
        _inlineEditing = true;
        _canvas.EditingTextIndex = _editingIndex;
        _text.IsVisible = true;
        PositionTextEditor();
        _canvas.Redraw();
        _status.Text = "直接输入文字 · Enter 换行 · Ctrl+Enter 或点击图片确认 · Esc 取消修改";
        _text.Focus();
        Dispatcher.UIThread.Post(() => { if (_inlineEditing) _text.Focus(); }, DispatcherPriority.Loaded);
    }
    private void PositionTextEditor()
    {
        if (!_inlineEditing || _textPoint is not { } point) return;
        var p = _canvas.Viewport.ToView(point);
        var scale = _canvas.Viewport.Scale;
        _text.FontSize = Math.Max(1, (double)(_fontSize.Value ?? 24) * scale);
        _text.FontFamily = new FontFamily(AnnotationRenderer.Typeface.FamilyName);
        _text.Foreground = new SolidColorBrush(Color.FromUInt32(_color));
        var width = (double)(_textWidth.Value ?? 0);
        _text.TextWrapping = width > 0 ? TextWrapping.Wrap : TextWrapping.NoWrap;
        using var font = new SKFont(AnnotationRenderer.Typeface, (float)_text.FontSize);
        var measured = (_text.Text ?? "").Split('\n').Select(line => font.MeasureText(line.TrimEnd('\r'))).DefaultIfEmpty(0).Max();
        _text.Width = width > 0 ? Math.Max(40, width * scale + 10)
            : Math.Max(120, Math.Min(measured + 24, Math.Max(120, _canvas.Bounds.Width - p.X - 8)));
        _text.MinHeight = Math.Max(32, _text.FontSize * 1.5 + 10);
        _text.MaxHeight = Math.Max(_text.MinHeight, _canvas.Bounds.Height - p.Y);
        Canvas.SetLeft(_text, p.X - 5);
        Canvas.SetTop(_text, p.Y - 5);
    }
    private void CancelText()
    {
        _inlineEditing = false;
        _editingIndex = null;
        _textPoint = null;
        _text.IsVisible = false;
        _canvas.EditingTextIndex = null;
        _canvas.TextAnchor = null;
        _canvas.Redraw();
        _applyText.IsEnabled = false;
    }
    private void ApplyText()
    {
        if (!_inlineEditing) return;
        var text = _text.Text?.Trim();
        if (string.IsNullOrEmpty(text)) { CancelText(); return; }
        var index = _editingIndex;
        var point = _textPoint;
        var color = _color;
        var thickness = (float)(_width.Value ?? 4);
        var block = (int)(_block.Value ?? 12);
        var fontSize = (float)(_fontSize.Value ?? 24);
        var textWidth = (float)(_textWidth.Value ?? 0);
        // 在通知文档变更前结束编辑，避免属性同步覆盖正在提交的内容。
        _inlineEditing = false;
        _text.IsVisible = false;
        _canvas.EditingTextIndex = null;
        if (index is { } selected && selected < _document.Items.Count)
        {
            _document.Select(selected);
            _document.ApplyStyle(color, thickness, block, text, fontSize, textWidth);
        }
        else if (point is { } anchor)
        {
            _document.Add(new AnnotationItem { Tool = AnnotationTool.Text, Points = [anchor], Text = text, Color = color,
                Thickness = thickness, TextWidth = textWidth, FontSize = fontSize });
        }
        _editingIndex = null; _textPoint = null; _applyText.IsEnabled = false;
        _canvas.Redraw();
        _canvas.Focus();
    }
    private void OnDocumentChanged()
    {
        _undo.IsEnabled = _document.CanUndo; _redo.IsEnabled = _document.CanRedo; SyncProperties(); UpdateStats();
    }
    private void UpdateStats() => _stats.Text = $"{_source.Width} × {_source.Height}  ·  {_document.Items.Count} 个标注  ·  {_canvas.Viewport.Scale * RenderScaling * 100:0}%";

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (_text.IsKeyboardFocusWithin || _customColor.IsKeyboardFocusWithin || _width.IsKeyboardFocusWithin
            || _block.IsKeyboardFocusWithin || _fontSize.IsKeyboardFocusWithin || _textWidth.IsKeyboardFocusWithin)
        {
            if (ctrl && e.Key == Key.Enter && _text.IsKeyboardFocusWithin) { ApplyText(); e.Handled = true; }
            else base.OnKeyDown(e);
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (_textPoint is not null)
            {
                _textPoint = null; _canvas.TextAnchor = null; _canvas.InvalidateVisual();
            }
            else if (!_canvas.CancelGesture())
            {
                if (_document.Selection.Count > 0) _document.Select(-1); else Close();
            }
        }
        else if (e.Key == Key.Enter && _canvas.FinishPolyline()) { }
        else if (ctrl && e.Key == Key.Enter) Complete(false, false);
        else if (ctrl && e.Key == Key.Z)
        {
            _canvas.CancelGesture();
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _document.Redo(); else _document.Undo();
        }
        else if (ctrl && e.Key == Key.Y) { _canvas.CancelGesture(); _document.Redo(); }
        else if (ctrl && e.Key == Key.A) { SelectTool(AnnotationTool.Select); _document.SelectAll(); }
        else if (e.Key is Key.Delete or Key.Back) { _canvas.CancelGesture(); _document.Delete(); }
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && _document.Selection.Count > 0)
        {
            var n = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
            _document.Move(e.Key == Key.Left ? -n : e.Key == Key.Right ? n : 0, e.Key == Key.Up ? -n : e.Key == Key.Down ? n : 0);
            _document.Commit();
        }
        else { base.OnKeyDown(e); return; }
        e.Handled = true;
    }
    private void Complete(bool original, bool recognize)
    {
        if (_finished) return;
        if (original) { CancelText(); _canvas.CancelGesture(); }
        else if (_canvas.HasGesture)
        {
            _canvas.FinishPolyline();
            if (_canvas.HasGesture) { _status.Text = "请先完成或取消当前绘制操作"; return; }
        }
        if (!original && _inlineEditing) ApplyText();
        try
        {
            var frame = original || _document.Items.Count == 0 ? _source : _canvas.Export();
            _finished = true; _completion.TrySetResult(new AnnotationResult(frame, recognize)); Close();
        }
        catch (Exception ex) { GuiLog.Write($"annotation-v3: export failed {ex}"); _status.Text = "导出失败，标注仍保留，请重试"; }
    }
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_quickLayout is { } layout)
        {
            Width = layout.Monitor.Width / layout.Scaling;
            Height = layout.Monitor.Height / layout.Scaling;
            Position = layout.Monitor.Position;
            Dispatcher.UIThread.Post(() => { _canvas.Fit(true); _canvas.Focus(); }, DispatcherPriority.Loaded);
            return;
        }
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is not null)
        {
            var maxWidth = screen.WorkingArea.Width / screen.Scaling * .94;
            var maxHeight = screen.WorkingArea.Height / screen.Scaling * .92;
            MinWidth = Math.Min(900, maxWidth); MinHeight = Math.Min(560, maxHeight);
            Width = Math.Min(1180, maxWidth); Height = Math.Min(760, maxHeight);
            Position = new PixelPoint(screen.WorkingArea.X + (int)((screen.WorkingArea.Width - Width * screen.Scaling) / 2),
                screen.WorkingArea.Y + (int)((screen.WorkingArea.Height - Height * screen.Scaling) / 2));
        }
        Dispatcher.UIThread.Post(() =>
        {
            _canvas.Fit(_startAtActualSize);
            if (_startAtActualSize) _canvas.AlignTop();
            _canvas.Focus();
        }, DispatcherPriority.Loaded);
    }
    internal static uint? ParseColor(string? text)
    {
        var hex = text?.Trim().TrimStart('#');
        if (hex?.Length == 6) hex = "FF" + hex;
        return hex?.Length == 8 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var value) ? value : null;
    }
}
