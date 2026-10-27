using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kscan.App.Services;
using Kscan.App.Views;
using Kscan.Core.Imaging;
using Kscan.Core.Configuration;

namespace Kscan.App.Tests;

public sealed class ScrollCaptureTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void Live_preview_fits_beside_selection_and_never_covers_capture_or_toolbar(double scale)
    {
        var work = new PixelRect(-2560, 0, 2560, 1440);
        var region = new PixelRect(-1710, 240, 850, 700);
        var toolbar = ScrollCaptureLayout.Place(region, [(work, scale)])!.Value;
        var preview = ScrollCaptureLayout.PlacePreview(region, toolbar, [(work, scale)]);
        Assert.NotNull(preview);
        Assert.True(ScrollCaptureLayout.Contains(work, preview.Value));
        Assert.False(preview.Value.Intersects(region));
        Assert.False(preview.Value.Intersects(toolbar));
        Assert.True(preview.Value.Right <= region.X || preview.Value.X >= region.Right);
        Assert.True(preview.Value.Width >= 220 * scale);
        Assert.True(preview.Value.Height >= 240 * scale);
    }

    [Fact]
    public void Live_preview_uses_second_monitor_when_selection_monitor_has_no_room()
    {
        var first = new PixelRect(0, 0, 1280, 800);
        var second = new PixelRect(-1920, 0, 1920, 1080);
        var region = new PixelRect(10, 10, 1260, 780);
        var toolbar = new PixelRect(-1200, 900, 520, 112);
        var preview = ScrollCaptureLayout.PlacePreview(region, toolbar, [(first, 1), (second, 1.5)]);
        Assert.NotNull(preview);
        Assert.True(ScrollCaptureLayout.Contains(second, preview.Value));
        Assert.False(preview.Value.Intersects(toolbar));
        Assert.Null(ScrollCaptureLayout.PlacePreview(region, toolbar, [(first, 1)]));
    }

    [Fact]
    public void Native_shade_has_a_hole_for_live_preview()
    {
        if (!OperatingSystem.IsWindows()) return;
        var screen = new PixelRect(0, 0, 1920, 1080);
        var region = new PixelRect(160, 180, 800, 600);
        var toolbar = ScrollCaptureLayout.Place(region, [(screen, 1)])!.Value;
        var preview = ScrollCaptureLayout.PlacePreview(region, toolbar, [(screen, 1)])!.Value;
        var clip = ScrollCaptureShade.CreateClip(screen, region, toolbar, preview);
        try
        {
            Assert.False(PtInRegion(clip, preview.X + 1, preview.Y + 1));
            Assert.False(PtInRegion(clip, preview.Right - 1, preview.Bottom - 1));
            Assert.False(PtInRegion(clip, region.X + 1, region.Y + 1));
            Assert.False(PtInRegion(clip, toolbar.X + 1, toolbar.Y + 1));
            Assert.True(PtInRegion(clip, 0, 0));
        }
        finally { ScrollCaptureShade.DeleteObject(clip); }
    }

    [AvaloniaFact]
    public async Task Live_preview_opens_automatically_updates_and_closes_with_capture()
    {
        var pixels = new byte[120 * 900 * 4];
        new Random(714).NextBytes(pixels);
        var page = new ImageFrame(120, 900, pixels);
        var window = new ScrollCaptureWindow(page.Crop(0, 0, 120, 300), new(10, 10, 120, 300), new(10, 400, 520, 112),
            _ => Task.FromResult<ImageFrame?>(page.Crop(0, 100, 120, 300)), false, () => false,
            previewPlacement: new PixelRect(600, 10, 400, 650));
        window.Show();
        var preview = window.SidePreviewForTest;
        Assert.NotNull(preview);
        Assert.True(preview.IsVisible);
        Assert.Equal(new PixelPoint(600, 10), preview.Position);
        Assert.NotNull(preview.PreviewImageForTest.Source);
        var previous = preview.PreviewImageForTest.Source;
        // 完成会刷新不足150ms时尚未更新的缩略预览；直接刷新只用于验证显示源随长图变化。
        await window.SampleAsync();
        preview.Refresh(window.StitcherForTest);
        Assert.NotSame(previous, preview.PreviewImageForTest.Source);
        Assert.Equal(400, window.StitcherForTest.Height);
        window.Close();
        Assert.False(preview.IsVisible);
        Assert.Null(preview.PreviewImageForTest.Source);
        Assert.Null(await window.Result);
    }

    [AvaloniaFact]
    public async Task Undo_button_stays_enabled_during_capture_instead_of_flashing_each_frame()
    {
        var pixels = new byte[120 * 900 * 4];
        new Random(174).NextBytes(pixels);
        var page = new ImageFrame(120, 900, pixels);
        var current = page.Crop(0, 200, 120, 300);
        var window = new ScrollCaptureWindow(page.Crop(0, 300, 120, 300), new(0, 0, 120, 300), new(0, 400, 520, 112),
            _ => Task.FromResult<ImageFrame?>(current), false, () => false);
        window.Show();
        await window.SampleAsync();
        var undo = window.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "撤销上一段"));
        Assert.True(undo.IsEnabled);
        var disabledCount = 0;
        undo.PropertyChanged += (_, args) => { if (args.Property == Button.IsEnabledProperty && !undo.IsEnabled) disabledCount++; };
        for (var i = 0; i < 20; i++)
        {
            current = page.Crop(0, i % 2 == 0 ? 250 : 200, 120, 300);
            var sample = window.SampleAsync();
            Assert.True(undo.IsEnabled);
            await sample;
            Assert.True(undo.IsEnabled);
        }
        Assert.Equal(0, disabledCount);
        Assert.Equal(400, window.StitcherForTest.Height);
        await window.FinishAsync();
        Assert.Equal(page.Crop(0, 200, 120, 400).Pixels, (await window.Result)!.Pixels);
    }

    [AvaloniaFact]
    public async Task Large_preview_pauses_capture_and_held_escape_only_closes_preview()
    {
        var frame = new ImageFrame(120, 300, new byte[120 * 300 * 4]);
        var escape = false;
        var captures = 0;
        var window = new ScrollCaptureWindow(frame, new(0, 0, 120, 300), new(0, 400, 520, 112),
            _ => { captures++; return Task.FromResult<ImageFrame?>(frame); }, false, () => escape);
        window.Show();
        var preview = window.OpenPreviewAsync();
        for (var i = 0; i < 100 && window.LargePreviewForTest is null; i++) await Task.Delay(10);
        Assert.NotNull(window.LargePreviewForTest);
        await window.SampleAsync(); Assert.Equal(0, captures);
        escape = true; window.CheckForExit();
        await preview;
        Assert.Null(window.LargePreviewForTest);
        Assert.True(window.IsVisible);
        window.CheckForExit(); Assert.True(window.IsVisible);
        escape = false; window.CheckForExit();
        escape = true; window.CheckForExit();
        Assert.False(window.IsVisible);
        Assert.Null(await window.Result);
    }

    [AvaloniaFact]
    public async Task Burst_capture_queues_frames_while_previous_frames_are_stitching()
    {
        var pixels = new byte[120 * 1200 * 4];
        new Random(772).NextBytes(pixels);
        var page = new ImageFrame(120, 1200, pixels);
        var captured = 0;
        var window = new ScrollCaptureWindow(page.Crop(0, 0, 120, 300), new(0, 0, 120, 300), new(0, 400, 520, 112),
            _ => Task.FromResult<ImageFrame?>(page.Crop(0, Math.Min(++captured, 6) * 100, 120, 300)), false, () => false);
        window.Show();
        var samples = new List<Task>();
        for (var i = 0; i < 6; i++) samples.Add(window.SampleAsync());
        Assert.Equal(6, captured);
        await Task.WhenAll(samples);
        await window.FinishAsync();
        var result = await window.Result;
        Assert.NotNull(result);
        Assert.Equal(page.Crop(0, 0, 120, 900).Pixels, result.Pixels);
    }

    [AvaloniaFact]
    public async Task Full_capture_buffer_resumes_automatically_after_drain()
    {
        var pixels = new byte[120 * 1800 * 4];
        new Random(773).NextBytes(pixels);
        var page = new ImageFrame(120, 1800, pixels);
        var captured = 0;
        var window = new ScrollCaptureWindow(page.Crop(0, 0, 120, 300), new(0, 0, 120, 300), new(0, 400, 520, 112),
            _ => Task.FromResult<ImageFrame?>(page.Crop(0, ++captured * 100, 120, 300)), false, () => false);
        window.Show();
        var samples = new List<Task>();
        // No dispatcher yield: one frame is processing, eight fill the bounded queue.
        for (var i = 0; i < 16; i++) samples.Add(window.SampleAsync());
        Assert.Equal(9, captured);
        await Task.WhenAll(samples);
        await window.SampleAsync();
        Assert.Equal(10, captured);
        Assert.Equal(1300, window.StitcherForTest.Height);
        await window.FinishAsync();
        var result = await window.Result;
        Assert.NotNull(result);
        Assert.Equal(page.Crop(0, 0, 120, result.Height).Pixels, result.Pixels);
    }

    [AvaloniaFact]
    public void Large_preview_zoom_does_not_change_source_pixels()
    {
        var pixels = new byte[320 * 3000 * 4];
        new Random(551).NextBytes(pixels);
        var frame = new ImageFrame(320, 3000, pixels);
        var before = pixels.ToArray();
        var window = new ScrollPreviewWindow(frame);
        window.Show(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, window.ZoomForTest);
        window.SetZoom(2);
        Assert.Equal(2, window.ZoomForTest);
        window.SetZoom(.5);
        Assert.Equal(.5, window.ZoomForTest);
        Assert.Equal(before, frame.Pixels);
        window.Close();
    }

    [Fact]
    public void Native_clip_excludes_thin_gap_near_toolbar_without_expanding_it()
    {
        if (!OperatingSystem.IsWindows()) return;
        var screen = new PixelRect(-1920, 0, 1920, 1080);
        var region = new PixelRect(-1760, 60, 800, 565);
        var toolbar = new PixelRect(-1480, 635, 520, 112);
        var clip = ScrollCaptureShade.CreateClip(screen, region, toolbar);
        try
        {
            // 检查原生 GDI 区域，不仅检查 C# 矩形；10px 间隙不能侵入工具栏。
            foreach (var x in new[] { -1920, -1761, -1760, -1481, -1480, -1200, -961, -960, -1 })
            foreach (var y in new[] { 0, 59, 60, 624, 625, 634, 635, 636, 660, 746, 747, 1079 })
            {
                static bool Inside(PixelRect rect, int px, int py) => px >= rect.X && px < rect.Right && py >= rect.Y && py < rect.Bottom;
                var expected = !Inside(region, x, y) && !Inside(toolbar, x, y);
                Assert.Equal(expected, PtInRegion(clip, x - screen.X, y - screen.Y));
            }
        }
        finally { ScrollCaptureShade.DeleteObject(clip); }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PtInRegion(IntPtr region, int x, int y);

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void Shade_leaves_toolbar_and_capture_clear(double scale)
    {
        var screen = new PixelRect(-1920, 0, 1920, 1080);
        var region = new PixelRect(-1700, 260, 1200, 500);
        var toolbar = ScrollCaptureLayout.Place(region, [(screen, scale)])!.Value;
        var pieces = ScrollCaptureShade.Outside(screen, region, toolbar).ToArray();
        Assert.All(pieces, p =>
        {
            Assert.True(ScrollCaptureLayout.Contains(screen, p));
            Assert.False(p.Intersects(region));
            Assert.False(p.Intersects(toolbar));
        });
        Assert.Equal((long)screen.Width * screen.Height - (long)region.Width * region.Height - (long)toolbar.Width * toolbar.Height,
            pieces.Sum(p => (long)p.Width * p.Height));
    }

    [AvaloniaFact]
    public async Task Escape_cancels_without_focus_even_while_finish_waits_for_capture()
    {
        var frame = new ImageFrame(100, 160, new byte[100 * 160 * 4]);
        var pending = new TaskCompletionSource<ImageFrame?>();
        var escape = false;
        CancellationToken captureToken = default;
        var window = new ScrollCaptureWindow(frame, new(0, 0, 100, 160), new(0, 200, 520, 112),
            token => { captureToken = token; return pending.Task; }, false, () => escape);
        window.Show();
        var sample = window.SampleAsync();
        var finish = window.FinishAsync();
        var otherWindow = new Window();
        otherWindow.Show(); otherWindow.Activate();
        window.CheckForExit();
        Assert.True(window.IsVisible);
        escape = true;
        window.CheckForExit();
        Assert.False(window.IsVisible);
        Assert.True(captureToken.IsCancellationRequested);
        Assert.Null(await window.Result);
        pending.SetResult(frame);
        await sample;
        await finish;
        await window.SampleAsync();
        Assert.Equal(1, window.StitcherForTest.PartCount);
        otherWindow.Close();
    }

    [AvaloniaFact]
    public async Task Cancel_button_closes_capture_immediately()
    {
        var frame = new ImageFrame(100, 160, new byte[100 * 160 * 4]);
        var window = new ScrollCaptureWindow(frame, new(0, 0, 100, 160), new(0, 200, 520, 112),
            _ => Task.FromResult<ImageFrame?>(frame), false, () => false);
        window.Show();
        var cancel = window.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "取消 (Esc)"));
        cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.False(window.IsVisible);
        Assert.Null(await window.Result);
    }

    [Theory]
    [InlineData(-1920, 0, 1920, 1080)]
    [InlineData(0, 0, 1920, 1080)]
    public void Shade_covers_only_pixels_outside_selection(int x, int y, int width, int height)
    {
        var screen = new PixelRect(x, y, width, height);
        var region = new PixelRect(100, 150, 800, 600);
        var pieces = ScrollCaptureShade.Outside(screen, region).ToArray();
        Assert.All(pieces, p => { Assert.True(ScrollCaptureLayout.Contains(screen, p)); Assert.False(p.Intersects(region)); });
        var hole = screen.Intersect(region);
        Assert.Equal((long)width * height - (long)hole.Width * hole.Height, pieces.Sum(p => (long)p.Width * p.Height));
    }

    [AvaloniaFact]
    public void Trim_controls_follow_theme_changes_while_open()
    {
        var resources = Application.Current!.Resources;
        var keys = new[] { "KscanBackgroundBrush", "KscanForegroundBrush", "KscanPanelBrush", "KscanAccentBrush", "KscanAccentForegroundBrush" };
        var previous = keys.ToDictionary(key => key, key => resources.ContainsKey(key) ? resources[key] : null);
        var window = new ScrollTrimWindow(new ImageFrame(20, 100, new byte[20 * 100 * 4]));
        try
        {
            window.Show();
            foreach (var dark in new[] { false, true })
            {
                var background = dark ? Avalonia.Media.Brushes.Black : Avalonia.Media.Brushes.White;
                var foreground = dark ? Avalonia.Media.Brushes.White : Avalonia.Media.Brushes.Black;
                resources[keys[0]] = background;
                resources[keys[1]] = foreground;
                resources[keys[2]] = background;
                resources[keys[3]] = Avalonia.Media.Brushes.Blue;
                resources[keys[4]] = Avalonia.Media.Brushes.White;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(background, window.Background);
                Assert.Equal(foreground, window.Foreground);
                foreach (var editor in window.GetLogicalDescendants().OfType<NumericUpDown>())
                {
                    Assert.Equal(background, editor.Background);
                    Assert.Equal(foreground, editor.Foreground);
                }
                Assert.All(window.GetLogicalDescendants().OfType<TextBlock>(), label =>
                    Assert.Equal(label.Text == "确认并标注" ? Avalonia.Media.Brushes.White : foreground, label.Foreground));
            }
        }
        finally
        {
            window.Close();
            foreach (var key in keys)
                if (previous[key] is { } value) resources[key] = value; else resources.Remove(key);
        }
    }

    [AvaloniaFact]
    public async Task Trim_keeps_exact_selected_rows()
    {
        var pixels = new byte[100 * 400 * 4];
        new Random(42).NextBytes(pixels);
        var source = new ImageFrame(100, 400, pixels);
        var window = new ScrollTrimWindow(source);
        window.Show();
        var inputs = window.GetLogicalDescendants().OfType<NumericUpDown>().ToArray();
        inputs[0].Value = 31;
        inputs[1].Value = 320;
        Assert.Equal(30, window.Start);
        Assert.Equal(320, window.End);
        var finish = window.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "确认并标注"));
        finish.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var result = await window.Result;
        Assert.NotNull(result);
        Assert.Equal(source.Crop(0, 30, 100, 290).Pixels, result.Pixels);
    }

    [AvaloniaFact]
    public void Long_image_opens_at_actual_size_from_top()
    {
        var source = new ImageFrame(100, 4000, new byte[100 * 4000 * 4]);
        var window = new AnnotationWindow(source, startAtActualSize: true);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var transform = window.DisplayTransformForTest();
        Assert.Equal(1, transform.Scale * window.RenderScaling, 6);
        Assert.Equal(24, transform.OffsetY, 6);
        window.Close();
    }

    [Fact]
    public void Migration_adds_scroll_binding_once_and_keeps_original_actions()
    {
        var original = new HotkeyBinding(HotkeyActionIds.CaptureRegionAndPin, "Ctrl+F3");
        var settings = AppSettings.Default with { Version = 13, Hotkeys = new HotkeyBindings([original]) };
        var migrated = settings.Migrate().Migrate();
        Assert.Contains(original, migrated.Hotkeys);
        Assert.Equal("Ctrl+Shift+F2", Assert.Single(migrated.Hotkeys, x => x.ActionId == HotkeyActionIds.ScrollCapture).Keys);
        var custom = settings with { Hotkeys = new HotkeyBindings([new(HotkeyActionIds.ScrollCapture, "Alt+F2")]) };
        Assert.Equal("Alt+F2", Assert.Single(custom.Migrate().Hotkeys).Keys);
    }

    [AvaloniaFact]
    public async Task Finish_waits_for_inflight_capture_and_preserves_exact_pixels()
    {
        var pixels = new byte[100 * 400 * 4];
        new Random(812).NextBytes(pixels);
        var page = new ImageFrame(100, 400, pixels);
        var first = page.Crop(0, 0, 100, 200);
        var next = page.Crop(0, 80, 100, 200);
        var pending = new TaskCompletionSource<ImageFrame?>();
        var calls = 0;
        var window = new ScrollCaptureWindow(first, new(0, 0, 100, 200), new(0, 300, 520, 112),
            _ => ++calls == 1 ? pending.Task : Task.FromResult<ImageFrame?>(next), false);
        window.Show();
        var sample = window.SampleAsync();
        var finish = window.FinishAsync();
        Assert.False(finish.IsCompleted);
        pending.SetResult(next);
        await sample;
        await finish;
        var result = await window.Result;
        Assert.NotNull(result);
        Assert.Equal(page.Crop(0, 0, 100, 280).Pixels, result.Pixels);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void Toolbar_stays_outside_capture_and_inside_work_area(double scale)
    {
        var work = new PixelRect(-1920, 0, 1920, 1080);
        var region = new PixelRect(-1700, 260, 1200, 500);
        var placement = ScrollCaptureLayout.Place(region, [(work, scale)]);
        Assert.NotNull(placement);
        Assert.True(ScrollCaptureLayout.Contains(work, placement.Value));
        Assert.False(region.Intersects(placement.Value));
    }

    [Fact]
    public void Full_screen_selection_cannot_include_toolbar()
    {
        var work = new PixelRect(0, 0, 1920, 1080);
        Assert.Null(ScrollCaptureLayout.Place(work, [(work, 1)]));
    }

    [AvaloniaFact]
    public async Task Cancel_during_capture_returns_null_without_appending()
    {
        var frame = new ImageFrame(100, 160, new byte[100 * 160 * 4]);
        var pending = new TaskCompletionSource<ImageFrame?>();
        var window = new ScrollCaptureWindow(frame, new(0, 0, 100, 160), new(0, 200, 520, 112), _ => pending.Task, false);
        window.Show();
        var sample = window.SampleAsync();
        window.Close();
        pending.SetResult(frame);
        await sample;
        Assert.Null(await window.Result);
        Assert.Equal(1, window.StitcherForTest.PartCount);
    }
}
