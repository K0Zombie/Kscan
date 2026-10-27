using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Kscan.App.Services;

/// <summary>Bounded WASAPI buffers; continuously sends mixed float PCM, including silence.</summary>
internal sealed class RecordingAudio : IDisposable
{
    private readonly List<(WasapiCapture Capture, BufferedWaveProvider Buffer, ISampleProvider Samples)> _sources = [];
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _cancel = new();
    private Task? _pump;
    private volatile bool _finishing;
    public string PipePath { get; }
    internal Exception? Error { get; private set; }

    public static IReadOnlyList<(string Id, string Name)> Devices(DataFlow flow)
    {
        using var devices = new MMDeviceEnumerator();
        return devices.EnumerateAudioEndPoints(flow, DeviceState.Active).Select(d =>
        {
            using (d) return (d.ID, d.FriendlyName);
        }).ToArray();
    }

    /// <summary>A clocked silent PCM pipe; device sources may be attached by the other constructor.</summary>
    internal RecordingAudio()
    {
        var name = "kscan-audio-" + Guid.NewGuid().ToString("N");
        PipePath = @"\\.\pipe\" + name;
        _pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 0, 48000 * 8 / 4);
    }

    public RecordingAudio(string source, string? microphone, string? output) : this()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            if (source is "system" or "mix")
            {
                using var device = string.IsNullOrWhiteSpace(output)
                    ? devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : devices.GetDevice(output);
                Add(new WasapiLoopbackCapture(device));
            }
            if (source is "mic" or "mix")
            {
                using var device = string.IsNullOrWhiteSpace(microphone)
                    ? devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia) : devices.GetDevice(microphone);
                Add(new WasapiCapture(device));
            }
            if (_sources.Count == 0) throw new InvalidOperationException("请选择音源。");
        }
        catch { Dispose(); throw; }
    }

    private void Add(WasapiCapture capture)
    {
        try
        {
            var buffer = new BufferedWaveProvider(capture.WaveFormat)
            { BufferDuration = TimeSpan.FromMilliseconds(500), DiscardOnBufferOverflow = true, ReadFully = true };
            ISampleProvider samples = buffer.ToSampleProvider();
            if (samples.WaveFormat.Channels == 1) samples = new MonoToStereoSampleProvider(samples);
            if (samples.WaveFormat.Channels != 2) throw new InvalidOperationException("当前音源不是单声道/双声道，请切换为双声道设备。");
            if (samples.WaveFormat.SampleRate != 48000) samples = new WdlResamplingSampleProvider(samples, 48000);
            capture.DataAvailable += (_, e) => buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            capture.RecordingStopped += (_, e) => { if (e.Exception is not null) Error = e.Exception; };
            _sources.Add((capture, buffer, samples));
        }
        catch { capture.Dispose(); throw; }
    }

    public void Start()
    {
        foreach (var source in _sources) source.Capture.StartRecording();
        _pump = Task.Run(PumpAsync);
    }
    internal void BeginFinishing() => _finishing = true;

    private async Task PumpAsync()
    {
        try
        {
            await _pipe.WaitForConnectionAsync(_cancel.Token);
            var clock = Stopwatch.StartNew();
            var mixed = new float[960]; // 10ms, 48kHz stereo
            var part = new float[960];
            var bytes = new byte[960 * 4];
            long frames = 0;
            while (!_cancel.IsCancellationRequested)
            {
                Array.Clear(mixed);
                foreach (var source in _sources)
                {
                    var read = source.Samples.Read(part, 0, part.Length);
                    for (var i = 0; i < read; i++) mixed[i] += part[i];
                }
                for (var i = 0; i < mixed.Length; i++) mixed[i] = Math.Clamp(mixed[i], -1, 1);
                MemoryMarshal.AsBytes(mixed.AsSpan()).CopyTo(bytes);
                await _pipe.WriteAsync(bytes, _cancel.Token);
                frames += 480;
                var wait = frames * 1000 / 48000 - clock.ElapsedMilliseconds;
                if (wait > 0) await Task.Delay((int)Math.Min(wait, 100), _cancel.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) when (_finishing) { } // FFmpeg closes its PCM input while finalizing a normal stop.
        catch (Exception ex) { if (!_cancel.IsCancellationRequested) Error = ex; }
    }

    public void Dispose()
    {
        _cancel.Cancel();
        foreach (var source in _sources) { source.Capture.StopRecording(); source.Capture.Dispose(); }
        _pipe.Dispose();
        try { _pump?.GetAwaiter().GetResult(); } catch { }
        _cancel.Dispose();
    }
}
