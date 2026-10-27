using System.Runtime.InteropServices;
using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Windows;

/// <summary>
/// Windows 的修饰键状态：<c>GetAsyncKeyState</c>（不需要窗口焦点，也不会抢键）。
/// <para>
/// 只看最高位（0x8000 = 当前按下）。左/右 Shift、Ctrl、Alt 都算（用户按哪个都算按住）。
/// </para>
/// </summary>
internal sealed class WindowsKeyboardState : IKeyboardState
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;   // Alt

    public bool IsSupported => true;

    public bool IsDown(HoldModifier modifier)
    {
        var key = modifier switch
        {
            HoldModifier.Shift => VkShift,
            HoldModifier.Control => VkControl,
            HoldModifier.Alt => VkMenu,
            _ => VkShift,
        };

        try
        {
            return (GetAsyncKeyState(key) & 0x8000) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
