using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kscan.Core.Configuration;

/// <summary>
/// 一串"排除的应用"条目，<b>带内容相等语义</b>（与 <see cref="HotkeyBindings"/> 同样的理由：
/// <c>AppSettings</c> 是 record，<see cref="AppSettingsService"/> 靠值相等跳过无谓写盘）。
/// <para>
/// 每个条目可以是 <c>taskmgr.exe</c> 这样的进程名，也可以是 <c>C:\Games\x\game.exe</c> 完整路径；
/// 匹配规则见 <see cref="ExcludedAppMatcher"/>。
/// </para>
/// </summary>
[JsonConverter(typeof(AppListJsonConverter))]
public sealed class AppList : IReadOnlyList<string>, IEquatable<AppList>
{
    /// <summary>一条都没有（默认值：不排除任何应用）。</summary>
    public static AppList Empty { get; } = new([]);

    private readonly string[] _items;

    public AppList(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = [.. items];
    }

    public int Count => _items.Length;

    public string this[int index] => _items[index];

    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(AppList? other) => other is not null && _items.SequenceEqual(other._items);

    public override bool Equals(object? obj) => Equals(obj as AppList);

    public override int GetHashCode()
    {
        var hash = new HashCode();

        foreach (var item in _items)
        {
            hash.Add(item, StringComparer.OrdinalIgnoreCase);
        }

        return hash.ToHashCode();
    }
}

/// <summary><see cref="AppList"/> 的 JSON 形态：一个字符串数组（<c>["taskmgr.exe"]</c>）。</summary>
public sealed class AppListJsonConverter : JsonConverter<AppList>
{
    public override AppList Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(JsonSerializer.Deserialize<string[]>(ref reader, options) ?? []);

    public override void Write(Utf8JsonWriter writer, AppList value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToArray(), options);
}

/// <summary>
/// 判断"前台应用是否命中排除列表"。
/// <para>
/// 条目含路径分隔符（<c>\</c> 或 <c>/</c>）时与<b>完整 exe 路径</b>比较；否则与<b>进程文件名</b>比较，
/// 且没写扩展名时自动补 <c>.exe</c>（<c>taskmgr</c> 与 <c>taskmgr.exe</c> 等价）。全部大小写不敏感。
/// </para>
/// </summary>
public static class ExcludedAppMatcher
{
    /// <summary>前台进程是否在排除列表里。</summary>
    public static bool IsExcluded(IReadOnlyList<string> ignoredApps, string? processName, string? executablePath)
    {
        if (ignoredApps.Count == 0)
        {
            return false;
        }

        var name = NormalizeProcessName(processName, executablePath);

        foreach (var entry in ignoredApps)
        {
            if (MatchesOne(entry, name, executablePath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>把进程名/路径归一化成一个文件名（<c>game.exe</c>）。</summary>
    public static string NormalizeProcessName(string? processName, string? executablePath)
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var fromPath = Path.GetFileName(executablePath.Trim());
            if (fromPath.Length > 0) return fromPath;
        }

        var name = processName?.Trim() ?? string.Empty;

        // Process.ProcessName 不带 .exe；这里统一成带扩展名的形式，方便与用户手填的条目比较
        if (name.Length > 0 && !Path.HasExtension(name))
        {
            name += ".exe";
        }

        return name;
    }

    private static bool MatchesOne(string? entry, string processName, string? executablePath)
    {
        var value = entry?.Trim().Trim('"');
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var hasSeparator = value.Contains('\\') || value.Contains('/');

        if (hasSeparator)
        {
            return !string.IsNullOrEmpty(executablePath)
                && string.Equals(Path.GetFullPath(value), Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase);
        }

        if (!Path.HasExtension(value))
        {
            value += ".exe";
        }

        return string.Equals(value, processName, StringComparison.OrdinalIgnoreCase);
    }
}
