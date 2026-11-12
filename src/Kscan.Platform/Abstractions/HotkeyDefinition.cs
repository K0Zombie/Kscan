namespace Kscan.Platform.Abstractions;

/// <summary>全局热键的修饰键。</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,

    Alt = 1,

    Control = 2,

    Shift = 4,

    /// <summary>Windows 键 / Linux 的 Super（Super 在本项目里也归到这里）。</summary>
    Win = 8,
}

/// <summary>
/// 一条全局热键：修饰键（可为空）+ 主键。
/// <para>
/// 主键用<b>字符串</b>存（<c>"S"</c> / <c>"F9"</c> / <c>"Space"</c>），而不是虚拟键码 ——
/// 虚拟键码是 Windows 特有的，Linux 那边要在 niri 的 <c>binds</c> 里用键名写，
/// 存键名才能两平台共用同一份配置。
/// </para>
/// <para>
/// 修饰键<b>可以不要</b>（例如单独 <c>F1</c>）—— 这是用户明确要的能力。
/// 但"无修饰键 + 字母/数字"会把那个键从所有程序手里抢走，界面用 <see cref="IsRiskyGlobally"/> 提示。
/// </para>
/// </summary>
public sealed record HotkeyDefinition(HotkeyModifiers Modifiers, string Key)
{
    /// <summary>主键名（用作 <c>Key</c> 之前先归一化）。</summary>
    private static readonly string[] NamedKeys =
    [
        "Space", "Enter", "Tab", "Esc", "Backspace", "Delete",
        "Insert", "Home", "End", "PageUp", "PageDown",
        "Left", "Right", "Up", "Down",
        "NumPad0", "NumPad1", "NumPad2", "NumPad3", "NumPad4", "NumPad5", "NumPad6", "NumPad7", "NumPad8", "NumPad9",
        "Multiply", "Add", "Subtract", "Decimal", "Divide", "CapsLock", "NumLock", "Scroll", "Pause", "PrintScreen",
        "OemSemicolon", "OemPlus", "OemComma", "OemMinus", "OemPeriod", "OemQuestion", "OemTilde",
        "OemOpenBrackets", "OemPipe", "OemCloseBrackets", "OemQuotes", "OemBackslash",
        "VolumeMute", "VolumeDown", "VolumeUp", "MediaNextTrack", "MediaPreviousTrack", "MediaStop", "MediaPlayPause",
        "BrowserBack", "BrowserForward", "BrowserRefresh", "BrowserStop", "BrowserSearch", "BrowserFavorites", "BrowserHome",
    ];

    /// <summary>
    /// 解析 <c>"Ctrl+Alt+S"</c> / <c>"F1"</c> / <c>"Ctrl+F3"</c> 这类写法。
    /// 修饰键可选；写法里的修饰键顺序随意（<see cref="ToString"/> 会归一）。
    /// </summary>
    public static bool TryParse(string? text, out HotkeyDefinition? hotkey, out string? error)
    {
        hotkey = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "热键是空的";
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "热键是空的";
            return false;
        }

        var modifiers = HotkeyModifiers.None;

        // 最后一段一定是主键，前面全是修饰键
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "win" or "super" or "meta" or "cmd" or "command":
                    modifiers |= HotkeyModifiers.Win;
                    break;
                default:
                    error = $"认不出的修饰键：{parts[i]}";
                    return false;
            }
        }

        if (NormalizeKey(parts[^1]) is not { } key)
        {
            error = $"认不出的主键：{parts[^1]}";
            return false;
        }

        hotkey = new HotkeyDefinition(modifiers, key);
        return true;
    }

    /// <summary>
    /// 没有修饰键、主键又是普通字符（字母 / 数字）—— 这种全局热键会把那个键
    /// 从所有程序手里抢走（打字就没了）。界面据此给橙色提示，但不拦着不让用。
    /// </summary>
    public bool IsRiskyGlobally =>
        Modifiers == HotkeyModifiers.None
        && Key.Length == 1
        && char.IsAsciiLetterOrDigit(Key[0]);

    /// <summary>主键名归一化（大小写、别名）。认不出返回 <c>null</c>。</summary>
    public static string? NormalizeKey(string? raw)
    {
        var key = raw?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        if (key.Length == 1)
        {
            var character = char.ToUpperInvariant(key[0]);
            return char.IsAsciiLetterOrDigit(character) ? character.ToString() : null;
        }

        var lower = key.ToLowerInvariant();

        // 别名先归一，再比对规范名
        var canonical = lower switch
        {
            "return" => "enter",
            "escape" => "esc",
            "del" => "delete",
            "pageup" or "pgup" => "pageup",
            "pagedown" or "pgdn" => "pagedown",
            _ => lower,
        };

        foreach (var name in NamedKeys)
        {
            if (string.Equals(name, canonical, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        if (canonical.Length >= 2
            && canonical[0] == 'f'
            && int.TryParse(canonical[1..], out var number)
            && number is >= 1 and <= 24)
        {
            return $"F{number}";
        }

        return null;
    }

    /// <summary>归一化写法：修饰键顺序固定，便于显示与比较。</summary>
    public override string ToString()
    {
        var parts = new List<string>(4);

        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        parts.Add(Key);
        return string.Join('+', parts);
    }
}
