namespace Kscan.Core.Configuration;

public sealed record RecordingSettings
{
    public string FFmpegPath { get; init; } = "ffmpeg";
    public string? VideoDirectory { get; init; }
    public string? GifDirectory { get; init; }
    public string? AudioDirectory { get; init; }
    public bool UseApplicationDirectory { get; init; }
    public int VideoFps { get; init; } // 0: selected monitor refresh rate
    public int GifFps { get; init; } = 15;
    public bool DrawMouse { get; init; } = true;
    public string AudioSource { get; init; } = "none";
    public string? MicrophoneId { get; init; }
    public string? OutputDeviceId { get; init; }
    public string VideoEncoder { get; init; } = "libx264";
    public string ExtraOutputArguments { get; init; } = "";
}
