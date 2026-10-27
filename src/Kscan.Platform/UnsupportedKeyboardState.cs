using Kscan.Platform.Abstractions;

namespace Kscan.Platform;

/// <summary>不支持查询修饰键状态的平台：功能直接隐藏，而不是"看着能用、按了不灵"。</summary>
internal sealed class UnsupportedKeyboardState : IKeyboardState
{
    public bool IsSupported => false;

    public bool IsDown(HoldModifier modifier) => false;
}
