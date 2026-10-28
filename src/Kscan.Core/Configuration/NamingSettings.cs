namespace Kscan.Core.Configuration;

public sealed record NamingSettings
{
    public const string DefaultTemplate = "{type}_{date:yyyyMMdd}_{time:HHmmssfff}_{counter:000}";
    public Dictionary<string, string> Templates { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string StampTemplate { get; init; } = "{date:yyyy-MM-dd} {time:HH:mm:ss}";
    public bool RecordStamp { get; init; }
    public bool LiveStamp { get; init; } = true;
    public string StampPosition { get; init; } = "bottom-right";
    public double StampSize { get; init; } = 20;
    public string StampColor { get; init; } = "#FFFFFFFF";
    public double StampOpacity { get; init; } = 0.9;
}
