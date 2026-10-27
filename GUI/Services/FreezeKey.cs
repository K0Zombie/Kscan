using Kscan.Platform.Abstractions;

namespace Kscan.App.Services;

/// <summary>
/// 「动图定格键」的取值与显示名。
/// <para>
/// 设置里存的是稳定字符串（<c>Shift</c>/<c>Control</c>/<c>Alt</c>/<c>None</c>），
/// 界面上显示本地化的键名（Shift / Ctrl / Alt / 关闭）。未知值一律按 Shift 处理。
/// </para>
/// </summary>
internal static class FreezeKey
{
    public const string Shift = "Shift";

    public const string Control = "Control";

    public const string Alt = "Alt";

    public const string None = "None";

    /// <summary>配置值 → 修饰键；<c>null</c> = 这个功能关掉了。</summary>
    public static HoldModifier? ToModifier(string? stored) => stored switch
    {
        None => null,
        Alt => HoldModifier.Alt,
        Control or "Ctrl" => HoldModifier.Control,
        _ => HoldModifier.Shift,
    };

    /// <summary>修饰键 → 显示名（提示文案里用）。</summary>
    public static string Label(HoldModifier modifier) => modifier switch
    {
        HoldModifier.Control => "Ctrl",
        HoldModifier.Alt => "Alt",
        _ => "Shift",
    };

    /// <summary>修饰键 → 配置值。</summary>
    public static string Stored(HoldModifier modifier) => modifier switch
    {
        HoldModifier.Control => Control,
        HoldModifier.Alt => Alt,
        _ => Shift,
    };
}
