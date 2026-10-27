namespace Kscan.Core.Imaging;

/// <summary>颜色解析与格式化（ARGB，0xAARRGGBB）。</summary>
public static class Colors
{
    public const uint Black = 0xFF000000;
    public const uint White = 0xFFFFFFFF;

    private static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = Black,
        ["white"] = White,
        ["red"] = 0xFFFF0000,
        ["green"] = 0xFF008000,
        ["blue"] = 0xFF0000FF,
        ["gray"] = 0xFF808080,
        ["grey"] = 0xFF808080,
        ["transparent"] = 0x00000000,
    };

    /// <summary>
    /// 解析颜色：支持 <c>#RGB</c>、<c>#RRGGBB</c>、<c>#AARRGGBB</c>（<c>#</c> 可省略）
    /// 以及少量命名色。
    /// </summary>
    public static uint Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var value = text.Trim();
        if (Named.TryGetValue(value, out var named))
        {
            return named;
        }

        if (value.StartsWith('#'))
        {
            value = value[1..];
        }

        return value.Length switch
        {
            3 => Expand(Convert.ToUInt32(value, 16)),
            6 => 0xFF000000u | Convert.ToUInt32(value, 16),
            8 => Convert.ToUInt32(value, 16),
            _ => throw new FormatException($"无法解析颜色：{text}（支持 #RGB / #RRGGBB / #AARRGGBB 或命名色）"),
        };

        // #RGB -> #RRGGBB（每位重复一次）
        static uint Expand(uint rgb) =>
            0xFF000000u
            | (((rgb >> 8) & 0xF) * 0x11u) << 16
            | (((rgb >> 4) & 0xF) * 0x11u) << 8
            | ((rgb & 0xF) * 0x11u);
    }

    public static string ToHex(uint argb) => $"#{argb:X8}";
}
