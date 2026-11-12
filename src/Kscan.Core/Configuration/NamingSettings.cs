namespace Kscan.Core.Configuration;

/// <summary>
/// 文件命名模板。除模板表外的字段都是标量；<see cref="Templates"/> 是字典，
/// 必须自己实现内容相等 —— <c>AppSettings</c> 的 record 相等性会被
/// <c>AppSettingsService.UpdateAsync</c> 用来判断"配置有没有变"，
/// 字典的引用相等会让每次保存都误判为发生了变化。
/// </summary>
public sealed record NamingSettings
{
    public const string DefaultTemplate = "{type}_{date:yyyyMMdd}_{time:HHmmssfff}_{counter:000}";

    /// <summary>按文件类型（image / video / gif / audio）覆盖默认模板；键忽略大小写。</summary>
    public Dictionary<string, string> Templates { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string StampTemplate { get; init; } = "{date:yyyy-MM-dd} {time:HH:mm:ss}";
    public bool RecordStamp { get; init; }
    public bool LiveStamp { get; init; } = true;
    public string StampPosition { get; init; } = "bottom-right";
    public double StampSize { get; init; } = 20;
    public string StampColor { get; init; } = "#FFFFFFFF";
    public double StampOpacity { get; init; } = 0.9;

    public bool Equals(NamingSettings? other)
    {
        if (other is null || ReferenceEquals(this, other))
        {
            return ReferenceEquals(this, other);
        }

        return StampTemplate == other.StampTemplate
            && RecordStamp == other.RecordStamp
            && LiveStamp == other.LiveStamp
            && StampPosition == other.StampPosition
            && StampSize.Equals(other.StampSize)
            && StampColor == other.StampColor
            && StampOpacity.Equals(other.StampOpacity)
            && TemplatesEqual(Templates, other.Templates);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(StampTemplate);
        hash.Add(RecordStamp);
        hash.Add(LiveStamp);
        hash.Add(StampPosition);
        hash.Add(StampSize);
        hash.Add(StampColor);
        hash.Add(StampOpacity);

        if (Templates is not null)
        {
            // 字典本身不保证遍历顺序，排序后再进哈希，保证相等对象哈希一致。
            foreach (var entry in Templates.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                hash.Add(entry.Key, StringComparer.OrdinalIgnoreCase);
                hash.Add(entry.Value);
            }
        }

        return hash.ToHashCode();
    }

    private static bool TemplatesEqual(
        Dictionary<string, string>? left,
        Dictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        // 用户可能用不同大小写写同一个类型键；比较时统一忽略键大小写，值仍严格比较。
        var normalized = new Dictionary<string, string>(right, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in left)
        {
            if (!normalized.TryGetValue(entry.Key, out var value) || !string.Equals(entry.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
