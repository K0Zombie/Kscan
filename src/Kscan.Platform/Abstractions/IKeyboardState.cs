namespace Kscan.Platform.Abstractions;

/// <summary>可以被"按住不放"的修饰键（动图定格用）。</summary>
public enum HoldModifier
{
    Shift,
    Control,
    Alt,
}

/// <summary>
/// 查询修饰键的<b>物理按下状态</b>（不依赖窗口焦点）。
/// <para>
/// 用它的场景：光标停在动图上时，按住 Shift 让动图停在当前帧 —— 这时贴图窗口
/// 通常<b>没有键盘焦点</b>，收不到 KeyDown，只能靠系统级的按键状态。
/// </para>
/// </summary>
public interface IKeyboardState
{
    /// <summary>当前平台是否支持查询（不支持的平台返回 <c>false</c>，功能自动隐藏）。</summary>
    bool IsSupported { get; }

    /// <summary>这个修饰键现在是按下的吗？</summary>
    bool IsDown(HoldModifier modifier);
}
