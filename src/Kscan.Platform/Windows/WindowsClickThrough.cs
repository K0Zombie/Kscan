using System.Runtime.InteropServices;
using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Windows;

/// <summary>
/// Windows 的鼠标穿透：给窗口补上 <c>WS_EX_TRANSPARENT | WS_EX_LAYERED</c>；
/// 取消时只摘掉 <c>WS_EX_TRANSPARENT</c>（layered 留着没有副作用）。
/// </summary>
internal sealed class WindowsClickThrough : IClickThrough
{
    /// <summary>GWL_EXSTYLE。</summary>
    private const int GwlExStyle = -20;

    private const long WsExTransparent = 0x0000_0020L;

    private const long WsExLayered = 0x0008_0000L;

    public bool IsSupported => true;

    public string Description => "Win32 窗口样式 WS_EX_TRANSPARENT（点击落到下层）";

    public string? DescriptionKey => "Platform.ClickThrough.Windows";

    public bool TrySetEnabled(IntPtr windowHandle, bool enabled)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var before = ReadExtendedStyle(windowHandle);
            var wanted = enabled ? before | WsExTransparent | WsExLayered : before & ~WsExTransparent;

            if (wanted != before)
            {
                WriteExtendedStyle(windowHandle, wanted);
            }

            // 读回来确认：SetWindowLong 成功和"旧样式恰好为 0"时返回值都是 0，不能拿返回值判断成败
            var after = ReadExtendedStyle(windowHandle);
            return (after & WsExTransparent) == (wanted & WsExTransparent);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static long ReadExtendedStyle(IntPtr handle) => IntPtr.Size == 8
        ? GetWindowLongPtr64(handle, GwlExStyle).ToInt64()
        : GetWindowLong32(handle, GwlExStyle);

    private static void WriteExtendedStyle(IntPtr handle, long value)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(handle, GwlExStyle, new IntPtr(value));
        }
        else
        {
            SetWindowLong32(handle, GwlExStyle, unchecked((int)value));
        }
    }

    // 32 位下 user32 只导出 GetWindowLongW/SetWindowLongW（Ptr 版是编译期的宏），所以要按进程位数挑入口
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
}
