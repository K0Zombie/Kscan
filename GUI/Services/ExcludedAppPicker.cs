using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Kscan.Platform;
using Kscan.Platform.Abstractions;

namespace Kscan.App.Services;

/// <summary>
/// "框选排除应用"：弹一层全屏透明遮罩，鼠标移到哪个窗口就高亮哪个窗口，
/// 单击把它所属的程序加入热键排除列表（Esc / 右键取消）。
/// <para>
/// 窗口探测走 Z 序枚举并跳过遮罩自己，所以遮罩<b>不需要</b>鼠标穿透 ——
/// 点击落在遮罩上，不会误点到目标窗口的按钮。
/// </para>
/// </summary>
internal static class ExcludedAppPicker
{
    /// <summary>弹出选择器；用户取消时返回 <c>null</c>。</summary>
    public static Task<ForegroundAppInfo?> PickAsync()
    {
        var window = new ExcludedAppPickerWindow();

        // 不 Show 就收不到指针事件，await 会永远挂着
        window.Show();

        return window.Result;
    }
}

internal sealed class ExcludedAppPickerWindow : Window
{
    private readonly Canvas _canvas;
    private readonly Border _highlight;
    private readonly TextBlock _hint;
    private readonly TaskCompletionSource<ForegroundAppInfo?> _completion = new();
    private readonly DispatcherTimer _timer;

    private ForegroundAppInfo? _pending;
    private ScreenRect? _pendingRect;

    public ExcludedAppPickerWindow()
    {
        Title = "选择要排除的应用";
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Maximized;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None];
        Cursor = new Cursor(StandardCursorType.Cross);
        Focusable = true;

        _highlight = new Border
        {
            BorderBrush = Brushes.DeepSkyBlue,
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(30, 0, 120, 215)),
            IsVisible = false,
            IsHitTestVisible = false,
        };

        _hint = new TextBlock
        {
            Text = I18n.T("Str.Settings.ExcludedPickHint"),
            Foreground = Brushes.White,
            FontSize = 15,
            Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)),
            Padding = new Thickness(12, 6),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 0, 0),
            IsHitTestVisible = false,
        };

        _canvas = new Canvas();
        _canvas.Children.Add(_highlight);

        var root = new Grid();
        root.Children.Add(_canvas);
        root.Children.Add(_hint);
        Content = root;

        // 遮罩窗口自身可能盖住目标窗口的位置变化，用定时器兜底轮询鼠标（指针事件仍负责即时响应）
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Probe();

        Opened += (_, _) => _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _completion.TrySetResult(null);
        };

        PointerMoved += (_, _) => Probe();
        PointerPressed += OnPointerPressed;
        KeyDown += OnKeyDown;
    }

    public Task<ForegroundAppInfo?> Result => _completion.Task;

    private void Probe()
    {
        var cursor = PlatformServices.ForegroundApp.GetCursorPosition();
        if (cursor is not { } point)
        {
            return;
        }

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var rect = PlatformServices.ForegroundApp.GetRectAt(point.X, point.Y, handle);
        var info = PlatformServices.ForegroundApp.GetAt(point.X, point.Y, handle);

        if (rect != _pendingRect || info != _pending)
        {
            _pendingRect = rect;
            _pending = info;
            UpdateHighlight(rect, info);
        }
    }

    private void UpdateHighlight(ScreenRect? rect, ForegroundAppInfo? info)
    {
        if (rect is not { } r)
        {
            _highlight.IsVisible = false;
            _hint.Text = I18n.T("Str.Settings.ExcludedPickHint");
            return;
        }

        var scaling = RenderScaling <= 0 ? 1 : RenderScaling;

        // 物理像素 → 本窗口的 DIP 坐标：先减去窗口在虚拟桌面上的物理原点，再除以缩放
        var originX = (r.X - Position.X) / scaling;
        var originY = (r.Y - Position.Y) / scaling;
        _highlight.Margin = new Thickness(originX, originY, 0, 0);
        _highlight.Width = r.Width / scaling;
        _highlight.Height = r.Height / scaling;
        _highlight.IsVisible = true;

        _hint.Text = info is null
            ? I18n.T("Str.Settings.ExcludedPickHint")
            : I18n.F("Str.Settings.ExcludedPickTarget", info.ProcessName);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;

        if (properties.IsRightButtonPressed)
        {
            Complete(null);
            return;
        }

        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        Probe();

        if (_pending is not { } info)
        {
            return;
        }

        Complete(info);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Complete(null);
        }
        else if (e.Key is Key.Enter or Key.Space)
        {
            if (_pending is { } info)
            {
                Complete(info);
            }
        }
    }

    private void Complete(ForegroundAppInfo? result)
    {
        if (_completion.Task.IsCompleted)
        {
            return;
        }

        _completion.TrySetResult(result);
        Close();
    }
}
