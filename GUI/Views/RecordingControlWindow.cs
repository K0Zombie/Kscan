using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Kscan.App.Services;
using Kscan.Platform;

namespace Kscan.App.Views;

internal sealed class RecordingControlWindow : Window
{
    public RecordingSession Session { get; }
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly List<Window> _frames = [];
    private Task? _stopping;
    private bool _finished;

    public RecordingControlWindow(RecordingSession session)
    {
        Session = session;
        Title = "录制控制"; Width = 180; SizeToContent = SizeToContent.Height;
        CanResize = false; WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = true; ShowActivated = false; Topmost = true;
        Bind(BackgroundProperty, new DynamicResourceExtension("KscanBackgroundBrush"));
        Bind(ForegroundProperty, new DynamicResourceExtension("KscanForegroundBrush"));
        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 8 };
        panel.Children.Add(_info);
        var stop = new Button { Content = "停止并保存", HorizontalAlignment = HorizontalAlignment.Stretch };
        stop.Click += async (_, _) => await StopAsync(); panel.Children.Add(stop);
        var add = new Button { Content = "再录一个选区", HorizontalAlignment = HorizontalAlignment.Stretch };
        add.Click += (_, _) => RecordingManager.Show(); panel.Children.Add(add);
        Content = panel;
        _timer.Tick += async (_, _) =>
        {
            if (_stopping is not null) return;
            _info.Text = $"● {session.Kind}  {session.Elapsed:hh\\:mm\\:ss}\n" +
                (session.Region is { } r ? $"{r.Width}×{r.Height} · 目标 {session.Fps} FPS" : "音频录制中");
            foreach (var overlay in _frames.OfType<RecordingFrameWindow>()) overlay.UpdateTime(session.Elapsed);
            if (session.HasExited) await StopAsync();
        };
        Closing += (_, e) => { if (!_finished) { e.Cancel = true; _ = StopAsync(); } };
        KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; await StopAsync(); } };
    }

    public void Initialize()
    {
        RecordingDisplay.Exclude(this);
        var screens = Screens.All;
        var screen = Session.Region is { } r
            ? screens.OrderByDescending(s => (long)s.Bounds.Intersect(r).Width * s.Bounds.Intersect(r).Height).First()
            : Screens.Primary ?? screens.First();
        var scale = screen.Scaling;
        var work = screen.WorkingArea;
        var target = Session.Region;
        var x = target?.Right + 8 ?? work.X + 16;
        if (x + Width * scale > work.Right) x = (int)((target?.X ?? work.Right) - Width * scale - 8);
        Position = new PixelPoint(Math.Clamp(x, work.X, Math.Max(work.X, work.Right - (int)(Width * scale))),
            Math.Clamp(target?.Y ?? work.Y + 16, work.Y, Math.Max(work.Y, work.Bottom - (int)(220 * scale))));
        if (target is { } region)
        {
            foreach (var monitor in screens.Where(s => s.Bounds.Intersects(region)))
            {
                var overlay = new RecordingFrameWindow(region, monitor.Bounds, monitor.Scaling);
                _frames.Add(overlay); overlay.Show();
                RecordingDisplay.Exclude(overlay);
                var handle = overlay.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (!PlatformServices.ClickThrough.TrySetEnabled(handle, true))
                    throw new InvalidOperationException("录制边框无法启用鼠标穿透。");
            }
        }
        _timer.Start();
    }

    public Task StopAsync() => _stopping ??= StopCoreAsync();
    private async Task StopCoreAsync()
    {
        _timer.Stop();
        foreach (var window in _frames) window.Close();
        _frames.Clear();
        _info.Text = "正在完成编码并保存…";
        try
        {
            await Session.StopAsync();
            var choice = await PinDialog.ChooseRecordingAsync(this, "录制完成", Session.Output, "打开预览", "打开目录", "关闭");
            if (choice == 0) Open(Session.Output);
            else if (choice == 1) Open(Path.GetDirectoryName(Session.Output)!);
        }
        catch (Exception ex) { GuiLog.Write($"recording: stop failed {ex}"); await PinDialog.ChooseRecordingAsync(this, "录制未完成", ex.Message, "确定"); }
        finally { _finished = true; Close(); }
    }
    private static void Open(string path) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
}

internal sealed class RecordingFrameWindow : Window
{
    private readonly TextBlock _time;
    private readonly PixelRect _screen;
    private readonly PixelRect _region;
    public RecordingFrameWindow(PixelRect region, PixelRect screen, double scale)
    {
        _screen = screen; _region = region;
        WindowDecorations = WindowDecorations.None; CanResize = false;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        MinWidth = MinHeight = 0;
        Width = screen.Width / scale; Height = screen.Height / scale;
        Position = screen.Position;
        var canvas = new Canvas { ClipToBounds = true };
        var border = new Border { Width = region.Width / scale, Height = region.Height / scale,
            BorderBrush = Brushes.Red, BorderThickness = new Thickness(2) };
        Canvas.SetLeft(border, (region.X - screen.X) / scale); Canvas.SetTop(border, (region.Y - screen.Y) / scale);
        canvas.Children.Add(border);
        _time = new TextBlock { Text = $"● 00:00:00  {region.Width}×{region.Height}", Foreground = Brushes.White,
            Background = Brushes.Black, Padding = new Thickness(5), FontSize = 12 };
        Canvas.SetLeft(_time, Math.Max(0, (region.X - screen.X) / scale + 4));
        Canvas.SetTop(_time, Math.Max(0, (region.Y - screen.Y) / scale + 4));
        canvas.Children.Add(_time); Content = canvas;
    }
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        SetWindowPos(handle, new IntPtr(-1), _screen.X, _screen.Y, _screen.Width, _screen.Height, 0x10);
    }
    public void UpdateTime(TimeSpan elapsed) => _time.Text = $"● {elapsed:hh\\:mm\\:ss}  {_region.Width}×{_region.Height}";
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
