namespace Kscan.Core.Configuration;

/// <summary>
/// 框选遮罩上的「修饰键」：按住它时单击 / 移动的含义会变（忽略子元素、截整个窗口、复制 SVG…）。
/// <para>
/// 单独定义而不是直接存 Avalonia 的 <c>KeyModifiers</c>：<c>Kscan.Core</c> 不许引用 GUI 库，
/// 而且配置里要能写成一个字符串、能被单测直接解析。
/// </para>
/// </summary>
public enum HeldModifier
{
    /// <summary>不绑定任何键（功能关闭）。</summary>
    None,

    Shift,

    Control,

    Alt,
}

/// <summary>把配置里的修饰键字符串（<c>Ctrl</c> / <c>Alt</c> / <c>Shift</c> / <c>none</c>）解析成 <see cref="HeldModifier"/>。</summary>
public static class HeldModifierParser
{
    /// <summary>
    /// 解析修饰键。无法识别的值（含 <c>null</c> / 空串）一律当作 <see cref="HeldModifier.None"/> ——
    /// 「不认识就当成没绑」，不会让遮罩单击行为变得不可预期。
    /// </summary>
    public static HeldModifier Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "shift" => HeldModifier.Shift,
        "ctrl" or "control" => HeldModifier.Control,
        "alt" => HeldModifier.Alt,
        _ => HeldModifier.None,
    };
}
