using System.Diagnostics;
using Avalonia;
using Kscan.Core.Configuration;

namespace Kscan.App.Services;

internal enum RecordingKind { Video, Gif, Audio }

internal sealed class RecordingSession
{
    private readonly Process _process;
    private readonly RecordingAudio? _audio;
    private readonly string _partial;
    private readonly Queue<string> _errors = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Task? _stop;
    public RecordingKind Kind { get; }
    public PixelRect? Region { get; }
    public string Output { get; }
    public int Fps { get; }
    public bool HasExited => _process.HasExited;
    public TimeSpan Elapsed => _clock.Elapsed;

    private RecordingSession(RecordingKind kind, PixelRect? region, int fps, string output, string partial,
        Process process, RecordingAudio? audio)
    { Kind = kind; Region = region; Fps = fps; Output = output; _partial = partial; _process = process; _audio = audio; }

    internal static List<string> BuildArguments(RecordingKind kind, PixelRect? region, int fps,
        RecordingSettings settings, string? audioPipe, string partial)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-n" };
        if (kind != RecordingKind.Audio)
        {
            var r = region ?? throw new ArgumentException("录制画面需要选区。");
            args.AddRange(["-rtbufsize", "33554432", "-f", "gdigrab", "-framerate", fps.ToString(),
                "-probesize", "32", "-analyzeduration", "0",
                "-draw_mouse", settings.DrawMouse ? "1" : "0", "-offset_x", r.X.ToString(), "-offset_y", r.Y.ToString(),
                "-video_size", $"{r.Width}x{r.Height}", "-i", "desktop"]);
        }
        if (audioPipe is not null)
            args.AddRange(["-rtbufsize", "1048576", "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", audioPipe]);
        if (kind == RecordingKind.Gif)
            args.AddRange(["-an", "-filter_complex", "[0:v]split[a][b];[a]palettegen=stats_mode=single[p];[b][p]paletteuse=new=1", "-loop", "0"]);
        else
        {
            if (kind == RecordingKind.Video)
            {
                args.AddRange(["-map", "0:v:0", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-c:v", settings.VideoEncoder,
                    "-pix_fmt", "yuv420p"]);
                if (settings.VideoEncoder == "libx264") args.AddRange(["-preset", "veryfast", "-tune", "zerolatency", "-crf", "20"]);
            }
            if (audioPipe is not null)
                args.AddRange(["-map", kind == RecordingKind.Audio ? "0:a:0" : "1:a:0", "-c:a", "aac", "-b:a", "192k"]);
            else args.Add("-an");
            args.AddRange(["-movflags", "+faststart"]);
        }
        args.AddRange(ParseArguments(settings.ExtraOutputArguments));
        args.Add(partial);
        return args;
    }

    internal static IReadOnlyList<string> ParseArguments(string text)
    {
        var args = new List<string>();
        var value = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in text)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(ch) && !quoted)
            { if (value.Length > 0) { args.Add(value.ToString()); value.Clear(); } }
            else value.Append(ch);
        }
        if (quoted) throw new ArgumentException("FFmpeg 高级参数的双引号没有配对。");
        if (value.Length > 0) args.Add(value.ToString());
        // Only codec/filter/output tuning, never additional inputs, files or process control.
        var allowed = new HashSet<string> { "-crf", "-preset", "-b:v", "-maxrate", "-bufsize", "-g", "-tune", "-profile:v", "-level:v", "-b:a", "-compression_level", "-q:v" };
        for (var i = 0; i < args.Count; i += 2)
            if (!allowed.Contains(args[i]) || i + 1 >= args.Count || args[i + 1].StartsWith('-'))
                throw new ArgumentException("高级参数只支持编码质量参数（如 -crf 20 -preset fast），每项须有值。");
        return args;
    }

    public static async Task<RecordingSession> StartAsync(RecordingKind kind, PixelRect? region, int fps,
        RecordingSettings settings, string directory)
    {
        if (fps is < 1 or > 360) throw new ArgumentException("帧率范围为 1–360。");
        Directory.CreateDirectory(directory);
        var extension = kind == RecordingKind.Gif ? ".gif" : kind == RecordingKind.Audio ? ".m4a" : ".mp4";
        var output = Path.Combine(directory, $"kscan-{kind.ToString().ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}" + extension);
        var partial = Path.ChangeExtension(output, ".partial" + extension);
        RecordingAudio? audio = null;
        Process? process = null;
        var started = false;
        try
        {
            if (kind != RecordingKind.Gif && settings.AudioSource != "none")
                audio = new RecordingAudio(settings.AudioSource, settings.MicrophoneId, settings.OutputDeviceId);
            if (kind == RecordingKind.Audio && audio is null) throw new ArgumentException("单独录音需要选择音源。");
            var start = new ProcessStartInfo(settings.FFmpegPath)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory };
            foreach (var arg in BuildArguments(kind, region, fps, settings, audio?.PipePath, partial)) start.ArgumentList.Add(arg);
            process = new Process { StartInfo = start };
            var session = new RecordingSession(kind, region, fps, output, partial, process, audio);
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (session._errors) { session._errors.Enqueue(e.Data); while (session._errors.Count > 60) session._errors.Dequeue(); }
            };
            process.Start(); started = true; process.BeginErrorReadLine();
            audio?.Start();
            await Task.Delay(900);
            if (process.HasExited) throw new InvalidOperationException(session.ErrorMessage());
            GuiLog.Write($"recording: start {kind} {region} fps={fps} file={output}");
            return session;
        }
        catch
        {
            audio?.Dispose();
            if (process is not null) { if (started && !process.HasExited) process.Kill(true); process.Dispose(); }
            // Preserve nonempty partial output for diagnosis/recovery.
            if (File.Exists(partial) && new FileInfo(partial).Length == 0) File.Delete(partial);
            throw;
        }
    }

    private string ErrorMessage()
    {
        lock (_errors) return "FFmpeg 录制失败：" + string.Join(Environment.NewLine, _errors.TakeLast(12)) + "\n临时文件：" + _partial;
    }

    public Task StopAsync() => _stop ??= StopCoreAsync();
    private async Task StopCoreAsync()
    {
        try
        {
            _clock.Stop();
            _audio?.BeginFinishing();
            if (!_process.HasExited)
            {
                await _process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
                await _process.StandardInput.FlushAsync().ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { _process.Kill(true); throw new IOException("编码器结束超时，临时录制文件保留在：" + _partial); }
            }
            _process.WaitForExit();
            if (_process.ExitCode != 0) throw new IOException(ErrorMessage());
            if (_audio?.Error is { } error) throw new IOException("音源采集失败，临时文件已保留。", error);
            if (!File.Exists(_partial) || new FileInfo(_partial).Length == 0) throw new IOException("录制没有产生有效文件。");
            File.Move(_partial, Output);
            GuiLog.Write($"recording: saved {Output} duration={Elapsed}");
        }
        finally { _audio?.Dispose(); _process.Dispose(); }
    }
}
