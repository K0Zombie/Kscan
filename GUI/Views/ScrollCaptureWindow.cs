using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Kscan.App.Services;
using Kscan.Core.Imaging;
using Kscan.Platform;
using System.Runtime.InteropServices;

namespace Kscan.App.Views;

/// <summary>采集器不覆盖采集区、不转发或模拟滚动；定时读取原始物理矩形。</summary>
internal sealed class ScrollCaptureWindow : Window
{
    private readonly ScrollStitcher _stitcher;
    private readonly PixelRect _placement;
    private readonly Func<CancellationToken, Task<ImageFrame?>> _capture;
    private readonly TaskCompletionSource<ImageFrame?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Queue<ImageFrame> _frames = new();
    private readonly int _queueCapacity;
    private bool _bufferBusy;
    private Task _capturePending = Task.CompletedTask;
    private bool _previewOpening;
    private ScrollPreviewWindow? _largePreview;
    private readonly PixelRect? _previewPlacement;
    private ScrollLivePreviewWindow? _sidePreview;
    private long _lastPreviewTime;
    private int _lastPreviewHeight;
    private bool _escapeWasDown;
    private long _resumeAfterPreview;
    private readonly DispatcherTimer _exitTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Func<bool> _escapePressed;
    private readonly List<Window> _shades = [];
    private readonly TextBlock _hint = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _size = new() { FontSize = 11 };
    private readonly Image _preview = new() { Width = 74, Height = 84, Stretch = Stretch.Uniform };
    private readonly Button _pause;
    private readonly Button _undo;
    private readonly Button _finish;
    private Bitmap? _previewBitmap;
    private Task _pending = Task.CompletedTask;
    private bool _paused;
    private bool _closed;
    private bool _finishing;
    private bool _undoing;
    private int _failures;
    private ScrollAppendStatus? _lastStatus;

    public Task<ImageFrame?> Result => _completion.Task;
    internal ScrollStitcher StitcherForTest => _stitcher;
    internal ScrollPreviewWindow? LargePreviewForTest => _largePreview;
    internal ScrollLivePreviewWindow? SidePreviewForTest => _sidePreview;

    public ScrollCaptureWindow(ImageFrame initial, PixelRect region, PixelRect placement,
        Func<CancellationToken, Task<ImageFrame?>> capture, bool autoStart = true, Func<bool>? escapePressed = null, PixelRect? previewPlacement = null)
    {
        _stitcher = new ScrollStitcher(initial);
        _placement = placement;
        _capture = capture;
        _previewPlacement = previewPlacement;
        _queueCapacity = (int)Math.Clamp(64_000_000L / initial.Pixels.Length, 2, 8);
        _escapePressed = escapePressed ?? (() => OperatingSystem.IsWindows() && (GetAsyncKeyState(0x1B) & 0x8000) != 0);
        Title = "滚动截图";
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        BindTheme(this, BackgroundProperty, "KscanBackgroundBrush");
        BindTheme(this, ForegroundProperty, "KscanForegroundBrush");
        BindTheme(this, FontFamilyProperty, "KscanFontFamily");
        BindTheme(_hint, TextBlock.ForegroundProperty, "KscanMutedBrush");
        BindTheme(_size, TextBlock.ForegroundProperty, "KscanMutedBrush");
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,84"), Margin = new Thickness(10, 7) };
        var controls = new StackPanel { Spacing = 4 };
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        title.Children.Add(new TextBlock { Text = "滚动截图 · 上下滚动", FontSize = 14, FontWeight = FontWeight.SemiBold });
        Grid.SetColumn(_size, 1); title.Children.Add(_size);
        controls.Children.Add(title);
        controls.Children.Add(_hint);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        _finish = MakeButton("完成并调整起止", () => _ = FinishAsync());
        BindTheme(_finish, Button.BackgroundProperty, "KscanAccentBrush");
        BindTheme(_finish, Button.ForegroundProperty, "KscanAccentForegroundBrush");
        _pause = MakeButton("暂停", TogglePause);
        _undo = MakeButton("撤销上一段", () => _ = UndoAsync());
        actions.Children.Add(_finish); actions.Children.Add(_pause); actions.Children.Add(_undo);
        actions.Children.Add(MakeButton("取消 (Esc)", Close));
        controls.Children.Add(actions);
        body.Children.Add(controls);
        _preview.Height = 64;
        var previewContent = new StackPanel { Spacing = 2 };
        previewContent.Children.Add(_preview);
        previewContent.Children.Add(new TextBlock { Text = "放大预览", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
        var previewButton = new Button { Content = previewContent, Padding = new Thickness(2), Name = "ScrollLargePreview" };
        ToolTip.SetTip(previewButton, "暂停采集并查看大图；可缩放、滚动，关闭后继续");
        previewButton.Click += async (_, _) => await OpenPreviewAsync();
        Grid.SetColumn(previewButton, 1); body.Children.Add(previewButton);
        var border = new Border { BorderThickness = new Thickness(1), Child = body };
        BindTheme(border, Border.BorderBrushProperty, "KscanBorderBrush");
        BindTheme(border, Border.CornerRadiusProperty, "KscanCornerRadius");
        Content = border;
        _hint.Text = "鼠标放在选区内上下滚动；支持快速拼接，避开固定栏";
        ToolTip.SetTip(_hint, _hint.Text);
        _timer.Tick += async (_, _) => await SampleAsync();
        _exitTimer.Tick += (_, _) => CheckForExit();
        Opened += (_, _) =>
        {
            if (_previewPlacement is { } previewRect)
            {
                _sidePreview = new ScrollLivePreviewWindow(previewRect, () => _ = OpenPreviewAsync());
                _sidePreview.Show(this);
            }
            RefreshPreview(); _exitTimer.Start(); if (autoStart) _timer.Start();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            _exitTimer.Stop();
            foreach (var shade in _shades) if (shade.IsVisible) shade.Close();
            _shades.Clear();
            _frames.Clear();
            _largePreview?.Close();
            if (_sidePreview?.IsVisible == true) _sidePreview.Close();
            _sidePreview = null;
            _cancellation.Cancel();
            _preview.Source = null;
            _previewBitmap?.Dispose();
            _completion.TrySetResult(null);
            GuiLog.Write("scroll: closed; capture and shades stopped");
        };
    }

    public static async Task<ImageFrame?> CaptureAsync(RegionSelection selection)
    {
        if (selection.ScreenRect is not { } region || region.Width != selection.Frame.Width || region.Height != selection.Frame.Height)
        {
            await PinDialog.ChooseAsync(TopLevelAccessor.AnyWindow, "滚动截图", "没有可靠的屏幕区域信息，请在单屏内容区重新框选。", "确定");
            return null;
        }
        var screens = TopLevelAccessor.GetScreens();
        if (region.Width < 96 || region.Height < 128 || screens is null || !screens.All.Any(screen => ScrollCaptureLayout.Contains(screen.Bounds, region)))
        {
            await PinDialog.ChooseAsync(TopLevelAccessor.AnyWindow, "滚动截图", "请框选单个屏幕内至少 96×128 像素的可滚动内容，避开固定标题栏和底部输入框。", "确定");
            return null;
        }
        var placement = ScrollCaptureLayout.Place(region, screens.All.Select(s => (s.WorkingArea, s.Scaling)));
        if (placement is null)
        {
            await PinDialog.ChooseAsync(TopLevelAccessor.AnyWindow, "滚动截图", "选区外放不下采集工具栏。请缩小选区或给上方、下方留出约 120 像素的空白，再触发滚动截图。", "确定");
            return null;
        }
        var previewPlacement = ScrollCaptureLayout.PlacePreview(region, placement.Value, screens.All.Select(s => (s.WorkingArea, s.Scaling)));
        var window = new ScrollCaptureWindow(selection.Frame, region, placement.Value,
            token => PlatformServices.ScreenCapture.CaptureDesktopAsync(new(region.X, region.Y, region.Width, region.Height), token), previewPlacement: previewPlacement);
        GuiLog.Write($"scroll: start region={region} toolbar={placement}");
        var shades = ScrollCaptureShade.Show(screens.All.Select(s => (s.Bounds, s.Scaling)), region, placement.Value, previewPlacement);
        window._shades.AddRange(shades);
        try
        {
            window.Show();
            return await window.Result;
        }
        finally
        {
            if (window.IsVisible) window.Close();
            foreach (var shade in shades) if (shade.IsVisible) shade.Close();
        }
    }

    // 页面滚动时焦点属于被截图程序，普通 OnKeyDown 收不到 Esc。
    // 独立退出计时器也覆盖暂停、采集中和等待完成三个状态。
    internal void CheckForExit()
    {
        var down = _escapePressed();
        if (!_closed && down && !_escapeWasDown)
        {
            if (_largePreview is not null) _largePreview.Close();
            else Close();
        }
        _escapeWasDown = down;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private static void BindTheme(AvaloniaObject target, AvaloniaProperty property, string key) => target.Bind(property, new DynamicResourceExtension(key));
    private static Button MakeButton(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(8, 5), FontSize = 12 };
        BindTheme(button, Button.CornerRadiusProperty, "KscanCornerRadius");
        BindTheme(button, Button.ForegroundProperty, "KscanForegroundBrush");
        button.Click += (_, _) => action();
        return button;
    }

    internal Task SampleAsync()
    {
        if (_closed || _paused || _finishing || !_capturePending.IsCompleted || Environment.TickCount64 < _resumeAfterPreview) return Task.CompletedTask;
        if (_frames.Count >= _queueCapacity)
        {
            if (!_bufferBusy)
            {
                _bufferBusy = true;
                _hint.Text = "正在追赶拼接进度，请稍停滚动；缓冲腾出后自动继续";
                GuiLog.Write($"scroll: buffer busy queued={_frames.Count} height={_stitcher.Height}");
            }
            return Task.CompletedTask;
        }
        if (_bufferBusy)
        {
            _bufferBusy = false;
            GuiLog.Write($"scroll: buffer resumed queued={_frames.Count} height={_stitcher.Height}");
        }
        _capturePending = CaptureFrameAsync();
        return WaitForSampleAsync(_capturePending);
    }

    private async Task WaitForSampleAsync(Task capture)
    {
        await capture;
        await _pending;
    }

    private async Task CaptureFrameAsync()
    {
        try
        {
            var frame = await _capture(_cancellation.Token);
            if (_closed) return;
            if (frame is null)
            {
                if (++_failures >= 3) Pause("连续采集失败，已暂停；可完成当前长图");
                return;
            }
            _failures = 0;
            _frames.Enqueue(frame);
            if (_pending.IsCompleted) _pending = DrainFramesAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed) Pause("采集出错，已保留长图；可以完成或取消");
            GuiLog.Write($"scroll: capture failed {ex}");
        }
    }

    private async Task DrainFramesAsync()
    {
        while (!_closed && _frames.TryDequeue(out var frame)) await AppendFrameAsync(frame);
        if (!_closed && _lastPreviewHeight != _stitcher.Height && Environment.TickCount64 - _lastPreviewTime >= 150) RefreshPreview();
    }

    private async Task CaptureOnceAsync()
    {
        var frame = await _capture(_cancellation.Token);
        if (!_closed && frame is not null) await AppendFrameAsync(frame);
    }

    private async Task AppendFrameAsync(ImageFrame frame)
    {
        try
        {
            var result = await Task.Run(() => _stitcher.TryAppend(frame, _cancellation.Token), _cancellation.Token);
            if (_closed) return;
            if (_lastStatus != result.Status || result.Status == ScrollAppendStatus.Added)
                GuiLog.Write($"scroll: {result.Status} +{result.AddedRows} => {_stitcher.Width}x{_stitcher.Height} parts={_stitcher.PartCount}");
            _lastStatus = result.Status;
            var hint = result.Status switch
            {
                ScrollAppendStatus.Added => $"已在{(result.AddedAtTop ? "顶部" : "底部")}拼接 {result.AddedRows} 行，继续滚动或完成",
                ScrollAppendStatus.Unchanged => "当前内容已采集，可继续上下滚动或完成",
                ScrollAppendStatus.Reverse => "正在向上滚动，继续采集",
                ScrollAppendStatus.Ambiguous => "内容过于重复，无法确定拼接位置；请小幅滚动重试",
                ScrollAppendStatus.NoOverlap => "未找到可靠重叠；请回到上一段后小幅滚动，避开固定栏",
                ScrollAppendStatus.SizeChanged => "采集尺寸变化，已暂停；请完成当前长图或重新框选",
                _ => $"已达当前宽度上限（{_stitcher.HeightLimit:N0} 行），已暂停；请完成当前长图",
            };
            if (!_paused && !_bufferBusy) _hint.Text = hint;
            if (result.Status is ScrollAppendStatus.SizeChanged or ScrollAppendStatus.LimitReached) Pause(hint);
            ToolTip.SetTip(_hint, _hint.Text);
            if (result.Status == ScrollAppendStatus.Added && Environment.TickCount64 - _lastPreviewTime >= 150) RefreshPreview();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed) Pause("采集出错，已保留当前长图；可以完成或取消");
            GuiLog.Write($"scroll: capture failed {ex}");
        }
        finally { if (!_closed) UpdateUndoButton(); }
    }

    private void RefreshPreview()
    {
        _lastPreviewTime = Environment.TickCount64;
        _lastPreviewHeight = _stitcher.Height;
        var next = ResultActions.ToBitmap(_stitcher.Preview(74, 84));
        var old = _previewBitmap;
        _previewBitmap = next;
        _preview.Source = next;
        old?.Dispose();
        if (_sidePreview?.IsVisible == true) _sidePreview.Refresh(_stitcher);
        _size.Text = $"{_stitcher.Width}×{_stitcher.Height} · {_stitcher.PartCount}段";
        ToolTip.SetTip(_size, $"当前宽度最多 {_stitcher.HeightLimit:N0} 行；总像素上限 {ScrollStitcher.DefaultMaxPixels:N0}，高度上限 {ScrollStitcher.DefaultMaxHeight:N0} 行");
        UpdateUndoButton();
    }

    private void UpdateUndoButton() => _undo.IsEnabled = !_finishing && !_undoing && _stitcher.CanUndo;

    private void Pause(string? hint)
    {
        GuiLog.Write($"scroll: paused height={_stitcher.Height} queued={_frames.Count} reason={hint}");
        _paused = true;
        _pause.Content = "继续";
        if (hint is not null) _hint.Text = hint;
    }
    private void TogglePause()
    {
        if (_finishing || _undoing || _previewOpening || _largePreview is not null) return;
        if (!_paused) Pause("已暂停，可以操作页面；继续前请回到上次采集位置");
        else { _paused = false; _pause.Content = "暂停"; _hint.Text = "继续采集：鼠标放在选区内上下滚动"; }
    }
    private async Task UndoAsync()
    {
        if (_finishing || _closed || _previewOpening || _undoing) return;
        _undoing = true;
        UpdateUndoButton();
        Pause("已暂停，等待撤销上一段");
        try
        {
            await _capturePending;
            await _pending;
            if (_closed) return;
            if (_stitcher.Undo()) { RefreshPreview(); _hint.Text = "已撤销最近新增的一段。请把页面滚回上一段的位置，再点继续"; }
        }
        finally { _undoing = false; if (!_closed) UpdateUndoButton(); }
    }
    internal async Task FinishAsync()
    {
        if (_finishing || _closed || _previewOpening || _undoing) return;
        _finishing = true;
        _timer.Stop();
        _finish.IsEnabled = _pause.IsEnabled = _undo.IsEnabled = false;
        _hint.Text = "正在完成长图…";
        try
        {
            await _pending;
            await _capturePending;
            await _pending;
            if (_closed) return;
            if (!_paused) await CaptureOnceAsync();
            if (_closed) return;
            var partCount = _stitcher.PartCount;
            var frame = await Task.Run(_stitcher.BuildAndCompact);
            if (_closed) return;
            _completion.TrySetResult(frame);
            GuiLog.Write($"scroll: complete {frame.Width}x{frame.Height} parts={partCount}");
            Close();
        }
        catch (Exception ex)
        {
            if (_closed) return;
            _finishing = false;
            _finish.IsEnabled = _pause.IsEnabled = true;
            Pause("完成长图失败，当前内容仍保留，请重试");
            GuiLog.Write($"scroll: finish failed {ex}");
        }
    }

    internal async Task OpenPreviewAsync()
    {
        if (_closed || _finishing || _previewOpening || _undoing) return;
        if (_largePreview is not null) { _largePreview.Activate(); return; }
        var wasPaused = _paused;
        _previewOpening = true;
        Pause("预览中暂停采集，关闭预览后返回");
        try
        {
            await _capturePending;
            await _pending;
            if (_closed) return;
            var frame = await Task.Run(_stitcher.Build);
            if (_closed) return;
            _largePreview = new ScrollPreviewWindow(frame);
            await _largePreview.ShowDialog(this);
        }
        catch (Exception ex)
        {
            wasPaused = true;
            if (!_closed) Pause("打开预览失败，长图仍保留，可以继续或完成");
            GuiLog.Write($"scroll: preview failed {ex}");
        }
        finally
        {
            _largePreview = null;
            _previewOpening = false;
            if (!_closed && !wasPaused && !_finishing)
            {
                _paused = false;
                _resumeAfterPreview = Environment.TickCount64 + 180;
                _pause.Content = "暂停";
                _hint.Text = "继续采集：鼠标放在选区内上下滚动";
            }
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var screen = Screens.ScreenFromPoint(_placement.Position) ?? Screens.Primary;
        var dpi = screen?.Scaling ?? 1;
        Width = _placement.Width / dpi; Height = _placement.Height / dpi;
        Position = _placement.Position;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else base.OnKeyDown(e);
    }
}
