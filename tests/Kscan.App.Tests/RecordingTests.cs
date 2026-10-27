using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.LogicalTree;
using Kscan.App.Services;
using Kscan.App.Views;
using Kscan.Core.Configuration;

namespace Kscan.App.Tests;

public sealed class RecordingTests
{
    [AvaloniaFact]
    public void Selection_hint_and_active_recording_track_live_theme_colors_and_font()
    {
        var previous = ThemeService.Current.CurrentTheme;
        var screen = new ScreenInfo(new PixelRect(0, 0, 800, 600), 1);
        var overlay = RecordingRegionSelector.CreateOverlay(screen, RecordingKind.Gif);
        var active = new RecordingFrameWindow(new PixelRect(10, 10, 300, 200), screen.Bounds, 1);
        try
        {
            overlay.Show(); active.Show();
            overlay.SetSelection(new PixelRect(10, 10, 300, 200), new PixelPoint(400, 300));
            static T Find<T>(Control root, string name) where T : Control => root.GetLogicalDescendants().OfType<T>().Single(x => x.Name == name);
            var hint = Find<Border>(overlay, "RecordingSelectionHint");
            var label = Find<TextBlock>(overlay, "RecordingSelectionLabel");
            var selection = Find<Border>(overlay, "RecordingSelectionBorder");
            var clock = Find<TextBlock>(active, "RecordingActiveTime");
            var frame = Find<Border>(active, "RecordingActiveBorder");
            foreach (var theme in new[] { ThemeDefinition.Light, ThemeDefinition.Dark, ThemeDefinition.Dark with
            {
                FontFamily = "Courier New", CornerRadius = 12,
                Colors = ThemeDefinition.Dark.Colors with { Background = "#244050", Foreground = "#FFEEAA", Accent = "#AAEEFF", Danger = "#FF55AA" }
            } })
            {
                ThemeService.Current.Apply(theme);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
                Assert.Equal(Color.Parse(theme.Colors.Background), ColorOf(hint.Background));
                Assert.Equal(Color.Parse(theme.Colors.Foreground), ColorOf(label.Foreground));
                Assert.Equal(Color.Parse(theme.Colors.Accent), ColorOf(hint.BorderBrush));
                Assert.Equal(Color.Parse(theme.Colors.Accent), ColorOf(selection.BorderBrush));
                Assert.Equal(Color.Parse(theme.Colors.Background), ColorOf(clock.Background));
                Assert.Equal(Color.Parse(theme.Colors.Foreground), ColorOf(clock.Foreground));
                Assert.Equal(Color.Parse(theme.Colors.Danger), ColorOf(frame.BorderBrush));
                Assert.Equal(theme.CornerRadius, hint.CornerRadius.TopLeft);
                if (theme.FontFamily is { Length: > 0 }) Assert.Equal(theme.FontFamily, label.FontFamily.Name);
                using var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(800, 600));
                image.Render(overlay); image.Render(active);
            }
        }
        finally { overlay.Close(); active.Close(); ThemeService.Current.Apply(previous); }
    }
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    public void Recording_hint_measures_and_renders_using_a_resolved_control_font(int kind)
    {
        var screen = new ScreenInfo(new PixelRect(0, 0, 800, 600), 1);
        var overlay = RecordingRegionSelector.CreateOverlay(screen, (RecordingKind)kind);
        try
        {
            overlay.Show();
            overlay.SetSelection(new PixelRect(10, 10, 300, 200), new PixelPoint(780, 580));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var label = Assert.Single(overlay.GetLogicalDescendants().OfType<TextBlock>());
            Assert.True(label.Bounds.Width > 100);
            Assert.NotEqual("$Default", label.FontFamily.Name);
            using var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(800, 600));
            image.Render(overlay);
        }
        finally { overlay.Close(); }
    }
    [Theory]
    [InlineData("frame=1", true)]
    [InlineData("frame=0", false)]
    [InlineData("out_time_us=23000", true)]
    [InlineData("out_time_ms=0", false)]
    [InlineData("progress=continue", false)]
    [InlineData("out_time_us=N/A", false)]
    public void Startup_waits_for_actual_encoded_output(string progress, bool expected) =>
        Assert.Equal(expected, RecordingSession.IsReadyProgress(progress));

    [Fact]
    public void Recording_cursor_keeps_alpha_and_premultiplies_colors()
    {
        byte[] source = [100, 200, 50, 128, 255, 255, 255, 0, 10, 20, 30, 255];
        Assert.Equal(new byte[] { 50, 100, 25, 128, 0, 0, 0, 0, 10, 20, 30, 255 }, RecordingCursor.Premultiply(source));
        Assert.Equal(100, source[0]);
    }

    [Fact]
    public void Selection_hint_identifies_recording_kind()
    {
        Assert.Contains("GIF", RecordingRegionSelector.SelectionLabel(RecordingKind.Gif));
        Assert.Contains("视频", RecordingRegionSelector.SelectionLabel(RecordingKind.Video));
        Assert.Contains("Esc", RecordingRegionSelector.SelectionLabel(RecordingKind.Gif));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Audio_pipe_close_is_only_ignored_during_normal_finalization(bool finishing)
    {
        using var audio = new RecordingAudio();
        audio.Start();
        var name = audio.PipePath[(audio.PipePath.LastIndexOf('\\') + 1)..];
        using var consumer = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.In, System.IO.Pipes.PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await consumer.ConnectAsync(timeout.Token);
        var buffer = new byte[3840];
        await consumer.ReadExactlyAsync(buffer, timeout.Token);
        Assert.All(buffer, value => Assert.Equal(0, value));
        if (finishing) audio.BeginFinishing();
        consumer.Dispose();
        for (var i = 0; i < 30 && audio.Error is null; i++) await Task.Delay(10, timeout.Token);
        if (finishing) Assert.Null(audio.Error);
        else Assert.IsAssignableFrom<IOException>(audio.Error);
    }

    [AvaloniaFact]
    public void Recording_subpage_loads_only_when_selected_and_hides_advanced_settings()
    {
        var model = new Kscan.App.ViewModels.SettingsViewModel();
        var page = new SettingsPage { DataContext = model };
        var host = page.FindControl<ContentControl>("RecordingSectionHost")!;
        Assert.Null(host.Content);
        model.SelectSectionCommand.Execute("recording");
        Assert.True(model.SectionRecording);
        Assert.False(model.SectionGeneral);
        var panel = Assert.IsType<RecordingSettingsPanel>(host.Content);
        var expander = Assert.Single(panel.GetLogicalDescendants().OfType<Expander>());
        Assert.False(expander.IsExpanded);
        var kind = panel.GetLogicalDescendants().OfType<ComboBox>().First();
        var fps = panel.GetLogicalDescendants().OfType<NumericUpDown>().ToArray();
        Assert.True(fps[0].IsVisible);
        Assert.False(fps[1].IsVisible);
        kind.SelectedIndex = 1;
        Assert.False(fps[0].IsVisible);
        Assert.True(fps[1].IsVisible);
        model.SelectSectionCommand.Execute("general");
        Assert.False(model.SectionRecording);
        model.SelectSectionCommand.Execute("recording");
        Assert.Same(panel, host.Content);
    }

    [AvaloniaFact]
    public void Passive_recording_selection_has_no_snapshot_and_does_not_request_focus()
    {
        var monitor = new ScreenInfo(new PixelRect(-1920, 0, 1920, 1080), 1.5);
        var shade = RecordingRegionSelector.CreateOverlay(monitor);
        Assert.False(shade.ShowActivated);
        Assert.False(shade.Focusable);
        Assert.Contains(WindowTransparencyLevel.Transparent, shade.TransparencyLevelHint);
        Assert.Empty(shade.GetLogicalDescendants().OfType<Image>());
        shade.Close();
    }

    [Fact]
    public void Recording_drag_preserves_cross_monitor_negative_physical_coordinates()
    {
        var desktop = new PixelRect(-2560, -365, 4480, 1445);
        var a = new PixelPoint(-2400, -200); var b = new PixelPoint(600, 700);
        Assert.Equal(new PixelRect(-2400, -200, 3000, 900), RecordingSelectionGeometry.Drag(a, b, desktop));
        Assert.Equal(RecordingSelectionGeometry.Drag(a, b, desktop), RecordingSelectionGeometry.Drag(b, a, desktop));
        Assert.False(RecordingSelectionGeometry.IsDrag(a, new(a.X + 3, a.Y + 3)));
        Assert.True(RecordingSelectionGeometry.IsDrag(a, new(a.X + 4, a.Y)));
        Assert.Equal(desktop, RecordingSelectionGeometry.Drag(new(-10000, -10000), new(10000, 10000), desktop));
    }

    [Fact]
    public void Arguments_preserve_negative_desktop_coordinates_and_bound_input_queues()
    {
        var args = RecordingSession.BuildArguments(RecordingKind.Video, new PixelRect(-800, -120, 301, 201), 144,
            new(), null, "output file.partial.mp4");
        Assert.Equal("-800", args[args.IndexOf("-offset_x") + 1]);
        Assert.Equal("-120", args[args.IndexOf("-offset_y") + 1]);
        Assert.Equal("144", args[args.IndexOf("-framerate") + 1]);
        Assert.Equal("33554432", args[args.IndexOf("-rtbufsize") + 1]);
        Assert.DoesNotContain("-thread_queue_size", args);
        Assert.Contains("pad=ceil(iw/2)*2:ceil(ih/2)*2", args);
        Assert.Equal("output file.partial.mp4", args[^1]);
        Assert.DoesNotContain("-y", args);
    }

    [Theory]
    [InlineData("-i anything")]
    [InlineData("output.mp4")]
    [InlineData("-crf")]
    [InlineData("-crf \"20")]
    public void Advanced_parameters_cannot_replace_input_or_output(string text) =>
        Assert.Throws<ArgumentException>(() => RecordingSession.ParseArguments(text));

    [Fact]
    public void Advanced_quality_parameters_are_passed_as_separate_arguments() =>
        Assert.Equal(new[] { "-crf", "18", "-preset", "fast" }, RecordingSession.ParseArguments("-crf 18 -preset \"fast\""));

    [AvaloniaFact]
    public void Recording_settings_window_can_open_without_starting_capture()
    {
        var window = new RecordingWindow();
        window.Show();
        Assert.True(window.IsVisible);
        Assert.False(RecordingManager.IsRecording);
        Assert.Empty(window.GetLogicalDescendants().OfType<TextBox>());
        Assert.Empty(window.GetLogicalDescendants().OfType<NumericUpDown>());
        window.Close();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task Real_ffmpeg_encodes_synthetic_video_gif_audio_and_muxed_audio(int kindValue, bool audio)
    {
        var kind = (RecordingKind)kindValue;
        var directory = Path.Combine(Path.GetTempPath(), "kscan-encode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, kind == RecordingKind.Gif ? "output.gif" : kind == RecordingKind.Audio ? "output.m4a" : "output.mp4");
        try
        {
            var args = RecordingSession.BuildArguments(kind, kind == RecordingKind.Audio ? null : new PixelRect(0, 0, 321, 201),
                15, new(), audio ? "synthetic-audio" : null, output);
            var codec = args.FindIndex(a => a is "-an" or "-map");
            args.RemoveRange(0, codec);
            var sources = new List<string> { "-hide_banner", "-loglevel", "error", "-n" };
            if (kind != RecordingKind.Audio) sources.AddRange(["-f", "lavfi", "-i", "testsrc2=size=322x202:rate=15"]);
            if (audio) sources.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000"]);
            sources.AddRange(args); sources.InsertRange(sources.Count - 1, ["-t", "1"]);
            var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in sources) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(true); throw; }
            Assert.True(process.ExitCode == 0, await errors);
            Assert.True(new FileInfo(output).Length > 100);
            var probe = new ProcessStartInfo("ffprobe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-v", "error", "-show_streams", "-of", "json", output }) probe.ArgumentList.Add(arg);
            using var inspect = Process.Start(probe)!;
            var json = await inspect.StandardOutput.ReadToEndAsync();
            await inspect.WaitForExitAsync(timeout.Token);
            using var metadata = JsonDocument.Parse(json);
            var streams = metadata.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            Assert.Equal(kind != RecordingKind.Audio, streams.Any(s => s.GetProperty("codec_type").GetString() == "video"));
            Assert.Equal(audio && kind != RecordingKind.Gif, streams.Any(s => s.GetProperty("codec_type").GetString() == "audio"));
        }
        finally { Directory.Delete(directory, true); }
    }
}
