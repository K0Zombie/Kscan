using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Kscan.App.Services;
using Kscan.App.Views;
using Kscan.Core.Configuration;

namespace Kscan.App.Tests;

public sealed class RecordingTests
{
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
    public async Task Live_recording_selection_has_no_snapshot_images_or_thumbnail()
    {
        var bounds = new PixelRect(-1920, 0, 3840, 1080);
        var monitor = new ScreenInfo(new PixelRect(-1920, 0, 1920, 1080), 1.5);
        var session = new MultiScreenSession(null, bounds, [monitor], visibleDesktopOnly: true);
        var shade = new ShadeWindow(session, monitor);
        session.Add(shade);
        Assert.True(session.IsLive);
        Assert.Null(session.Preview);
        Assert.Throws<InvalidOperationException>(() => session.Desktop);
        Assert.Contains(WindowTransparencyLevel.Transparent, shade.TransparencyLevelHint);
        Assert.All(shade.GetLogicalDescendants().OfType<Image>(), image => Assert.Null(image.Source));
        session.Cancel();
        Assert.Null(await session.LiveResult);
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
