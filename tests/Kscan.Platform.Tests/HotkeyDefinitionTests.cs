using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Tests;

/// <summary>
/// 全局热键的解析与归一化。配置里存的是一行文本，用户手写很容易写错，
/// 所以边界（认不出的键名、越界的 F 键、只有修饰键没主键）必须钉死。
/// 修饰键<b>可选</b> —— 用户明确要求能绑 <c>F1</c> 这种不带修饰键的键。
/// </summary>
public class HotkeyDefinitionTests
{
    private static HotkeyDefinition Parse(string text)
    {
        Assert.True(HotkeyDefinition.TryParse(text, out var hotkey, out var error), $"{text} 应当能解析（{error}）");
        return hotkey!;
    }

    [Theory]
    [InlineData("Ctrl+Alt+S", HotkeyModifiers.Control | HotkeyModifiers.Alt, "S")]
    [InlineData("ctrl+shift+f9", HotkeyModifiers.Control | HotkeyModifiers.Shift, "F9")]
    [InlineData("Super+Space", HotkeyModifiers.Win, "Space")]
    [InlineData("Cmd+PageUp", HotkeyModifiers.Win, "PageUp")]
    [InlineData("Win+Alt+Return", HotkeyModifiers.Win | HotkeyModifiers.Alt, "Enter")]
    [InlineData("Control+escape", HotkeyModifiers.Control, "Esc")]
    public void Parses_modifiers_and_key(string text, HotkeyModifiers modifiers, string key)
    {
        var hotkey = Parse(text);

        Assert.Equal(modifiers, hotkey.Modifiers);
        Assert.Equal(key, hotkey.Key);
    }

    [Theory]
    [InlineData("F1", "F1")]
    [InlineData("f3", "F3")]
    [InlineData("Insert", "Insert")]
    [InlineData("s", "S")]
    public void Modifiers_are_optional(string text, string key)
    {
        var hotkey = Parse(text);

        Assert.Equal(HotkeyModifiers.None, hotkey.Modifiers);
        Assert.Equal(key, hotkey.Key);
    }

    [Theory]
    [InlineData("F1", false)]       // 功能键没有修饰键也不算危险（没人靠 F1 打字）
    [InlineData("Insert", false)]
    [InlineData("S", true)]         // 裸字母：会把 S 从所有程序手里抢走
    [InlineData("7", true)]
    [InlineData("Ctrl+S", false)]   // 带了修饰键就不算
    public void Flags_only_bare_letter_or_digit_as_risky(string text, bool risky) =>
        Assert.Equal(risky, Parse(text).IsRiskyGlobally);

    [Fact]
    public void Round_trips_through_ToString_in_a_fixed_order()
    {
        var hotkey = Parse("shift+win+alt+ctrl+s");

        Assert.Equal("Ctrl+Alt+Shift+Win+S", hotkey.ToString());

        // 归一化后的写法必须还能解析回同一个定义（配置存的就是这个字符串）
        Assert.Equal(hotkey, Parse(hotkey.ToString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl")]           // 只有修饰键、没有主键
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Foo")]
    [InlineData("Foo+S")]
    [InlineData("Ctrl+A+B")]       // A 被当成修饰键，不认识
    [InlineData("Ctrl+!")]
    [InlineData("Ctrl+F25")]       // F 键只到 F24
    [InlineData("Ctrl+F0")]
    public void Rejects_invalid_text(string? text)
    {
        Assert.False(HotkeyDefinition.TryParse(text, out var hotkey, out var error));
        Assert.Null(hotkey);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("A", "A")]
    [InlineData("z", "Z")]
    [InlineData("7", "7")]
    [InlineData("return", "Enter")]
    [InlineData("RETURN", "Enter")]
    [InlineData("pgdn", "PageDown")]
    [InlineData("del", "Delete")]
    [InlineData("esc", "Esc")]
    [InlineData("f1", "F1")]
    [InlineData("f24", "F24")]
    [InlineData("f", "F")]   // 单个 f 是字母 F 键，不是功能键（功能键要写 f1）
    public void Normalizes_key_names(string raw, string expected) =>
        Assert.Equal(expected, HotkeyDefinition.NormalizeKey(raw));

    [Theory]
    [InlineData("")]
    [InlineData("f25")]
    [InlineData("!")]
    [InlineData("Super")]   // 修饰键不能当主键
    public void Rejects_unknown_key_names(string raw) =>
        Assert.Null(HotkeyDefinition.NormalizeKey(raw));
}
