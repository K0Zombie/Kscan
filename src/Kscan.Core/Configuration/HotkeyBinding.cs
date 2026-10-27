using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kscan.Core.Configuration;

/// <summary>
/// 一条全局热键绑定：动作 id（见 <see cref="HotkeyActionIds"/>）+ 键的写法（如 <c>"F1"</c> / <c>"Ctrl+Alt+S"</c>）。
/// <para>
/// 写法刻意<b>不在这里解析</b>：解析器在平台层（<c>HotkeyDefinition</c>），而 Core 不认识平台 API。
/// 存成原始写法还有个好处 —— 用户写了个当前平台认不出的键时，配置照样存得住、界面照样能回显。
/// </para>
/// </summary>
public sealed record HotkeyBinding(string ActionId, string Keys)
{
    public string? Command { get; init; }
    public string? Arguments { get; init; }
    public string? WorkingDirectory { get; init; }

    /// <summary>「执行命令」是否以管理员身份启动（见 <see cref="UserCommand.RunAsAdmin"/>）。</summary>
    public bool RunAsAdmin { get; init; }
}

/// <summary>
/// 有序的热键绑定列表，<b>带内容相等语义</b>。
/// <para>
/// 为什么要专门一个类型：<c>AppSettings</c> 是 record，而 <c>AppSettingsService.UpdateAsync</c>
/// 靠 <c>next == Settings</c> 跳过无谓的写盘 —— 也就是<b>值相等是产品逻辑</b>。
/// <see cref="List{T}"/> / <c>Dictionary</c> / 数组的相等性都是<b>引用相等</b>，直接放进 record
/// 会让"值没变也写盘"；而给 <c>AppSettings</c> 手写 <c>Equals</c> 又会在加字段时漏字段
/// （症状是"改了配置却不保存"）。
/// </para>
/// <para>
/// 所以把列表封成这个小类型：它只有"一串绑定"这一个字段，内容相等就是逐条序列相等
/// （<see cref="HotkeyBinding"/> 自己是 record）。元素类型要么是 record、要么是标量，
/// <b>不要</b>往这里塞集合字段，否则相等性又会退化成引用相等。
/// </para>
/// </summary>
[JsonConverter(typeof(HotkeyBindingsJsonConverter))]
public sealed class HotkeyBindings : IReadOnlyList<HotkeyBinding>, IEquatable<HotkeyBindings>
{
    /// <summary>一条都没绑（默认值）。</summary>
    public static HotkeyBindings Empty { get; } = new([]);

    private readonly HotkeyBinding[] _items;

    public HotkeyBindings(IEnumerable<HotkeyBinding> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = [.. items];
    }

    public int Count => _items.Length;

    public HotkeyBinding this[int index] => _items[index];

    public IEnumerator<HotkeyBinding> GetEnumerator() =>
        ((IEnumerable<HotkeyBinding>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(HotkeyBindings? other) =>
        other is not null && _items.SequenceEqual(other._items);

    public override bool Equals(object? obj) => Equals(obj as HotkeyBindings);

    public override int GetHashCode()
    {
        var hash = new HashCode();

        foreach (var item in _items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// <see cref="HotkeyBindings"/> 的 JSON 形态：一个数组（<c>[{ "actionId": ..., "keys": ... }]</c>），
/// 而不是 <c>{ "count": .., "item": .. }</c> 那种暴露内部结构的默认形态。
/// public 只是因为 <see cref="JsonConverterAttribute"/> 要按类型实例化它，正常代码用不到。
/// </summary>
public sealed class HotkeyBindingsJsonConverter : JsonConverter<HotkeyBindings>
{
    public override HotkeyBindings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(JsonSerializer.Deserialize<HotkeyBinding[]>(ref reader, options) ?? []);

    public override void Write(Utf8JsonWriter writer, HotkeyBindings value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToArray(), options);
}
