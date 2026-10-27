using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kscan.App.Services;
using Kscan.Core.Imaging;

namespace Kscan.App.Tests;

public sealed class ClipboardWriterTests
{
    [Fact]
    public async Task Slow_old_copy_cannot_replace_newer_copy()
    {
        var gate = new ClipboardCopyGate();
        var old = gate.Begin(12);
        var ready = new TaskCompletionSource();
        var published = new List<string>();
        async Task FinishOld()
        {
            await ready.Task;
            if (gate.CanPublish(old, 12)) published.Add("old");
        }
        var pending = FinishOld();
        var current = gate.Begin(12);
        if (gate.CanPublish(current, 12)) published.Add("new");
        ready.SetResult(); await pending;
        Assert.Equal(new[] { "new" }, published);
    }

    [Fact]
    public void External_clipboard_change_during_preparation_cancels_pending_publication()
    {
        var gate = new ClipboardCopyGate();
        var request = gate.Begin(24);
        Assert.True(gate.CanPublish(request, 24));
        Assert.False(gate.CanPublish(request, 25));
        var unsupportedPlatform = gate.Begin(null);
        Assert.True(gate.CanPublish(unsupportedPlatform, null));
        Assert.False(gate.CanPublish(request, 24));
    }

    [AvaloniaFact]
    public async Task Png_preparation_runs_off_ui_thread_and_returns_ready_lossless_file()
    {
        var frame = new ImageFrame(97, 61, new byte[97 * 61 * 4]);
        new Random(84).NextBytes(frame.Pixels);
        for (var i = 3; i < frame.Pixels.Length; i += 4) frame.Pixels[i] = 255;
        var ranOnUi = true;
        var path = await ClipboardWriter.PreparePngAsync(frame, "test-background", source =>
        {
            ranOnUi = Dispatcher.UIThread.CheckAccess();
            return SkiaImageCodec.EncodePngFast(source);
        });
        try
        {
            Assert.False(ranOnUi);
            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            Assert.Equal(frame.Pixels, SkiaImageCodec.Load(path).Pixels);
        }
        finally { if (path is not null) File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task Concurrent_preparations_use_different_ready_files()
    {
        var first = new ImageFrame(40, 30, new byte[40 * 30 * 4]);
        var second = new ImageFrame(50, 20, new byte[50 * 20 * 4]);
        var paths = await Task.WhenAll(ClipboardWriter.PreparePngAsync(first, "test-concurrent"), ClipboardWriter.PreparePngAsync(second, "test-concurrent"));
        try
        {
            Assert.All(paths, p => Assert.NotNull(p));
            Assert.NotEqual(paths[0], paths[1]);
            Assert.Equal(40, SkiaImageCodec.Load(paths[0]!).Width);
            Assert.Equal(50, SkiaImageCodec.Load(paths[1]!).Width);
        }
        finally { foreach (var path in paths) if (path is not null) File.Delete(path); }
    }
}
