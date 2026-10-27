namespace Kscan.Core.Pins;

/// <summary>
/// 贴图分组：一个名字 + 一个颜色。分组名同时是磁盘上的目录名
/// （<c>data\user\pins\&lt;分组名&gt;\</c>），目录里的图片文件就是属于这个分组的贴图。
/// </summary>
public sealed record PinGroup(string Id, string Name, string Color)
{
    /// <summary>预设配色：新建分组时按顺序轮转分配，用户可改。</summary>
    public static IReadOnlyList<string> Palette { get; } =
    [
        "#4A90D9",   // 蓝
        "#48A868",   // 绿
        "#D98A3A",   // 橙
        "#B45AD9",   // 紫
        "#D95A6E",   // 红
        "#3AAFB0",   // 青
        "#C9A227",   // 金
        "#7A7AD9",   // 靛
    ];

    /// <summary>按序号取一个预设颜色（超出范围就循环）。</summary>
    public static string ColorAt(int index) => Palette[Math.Abs(index) % Palette.Count];

    /// <summary>归一化颜色字符串：允许 <c>#RRGGBB</c> / <c>RRGGBB</c>；非法时给个兜底色。</summary>
    public static string NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return Palette[0];
        }

        var text = color.Trim();

        if (!text.StartsWith('#'))
        {
            text = "#" + text;
        }

        if (text.Length != 7)
        {
            return Palette[0];
        }

        for (var i = 1; i < text.Length; i++)
        {
            if (!Uri.IsHexDigit(text[i]))
            {
                return Palette[0];
            }
        }

        return text.ToUpperInvariant();
    }
}
