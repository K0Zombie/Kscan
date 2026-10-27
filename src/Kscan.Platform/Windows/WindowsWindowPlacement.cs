using System.Runtime.InteropServices;
using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Windows;

/// <summary>Windows 上的窗口层级控制：<c>SetWindowPos(HWND_TOPMOST)</c>。</summary>
internal sealed class WindowsWindowPlacement : IWindowPlacement
{
    private static readonly IntPtr HwndTopmost = new(-1);

    private static readonly IntPtr HwndNoTopmost = new(-2);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool ForceTopmost(IntPtr handle) =>
        OperatingSystem.IsWindows()
        && handle != IntPtr.Zero
        && SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

    /// <summary>
    /// 置顶开关。取消置顶用 <c>HWND_NOTOPMOST</c>：窗口会回到普通层级，并且<b>不再</b>压住其它窗口。
    /// 不走 Avalonia 的 <c>Topmost</c> 属性是因为它每次都会重建一次窗口层级，
    /// 实测在某些环境下会让窗口"闪"一下甚至重排到别处。
    /// </summary>
    public bool SetTopmost(IntPtr handle, bool enabled) =>
        OperatingSystem.IsWindows()
        && handle != IntPtr.Zero
        && SetWindowPos(handle, enabled ? HwndTopmost : HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

    /// <summary>
    /// 插到某个窗口正上方：<c>SetWindowPos(handle, reference, …)</c>。
    /// 这是"让贴图稳定浮在管理窗口上面"的关键：两个窗口都置顶时，<c>HWND_TOPMOST</c>
    /// 只是在带内抢一次，谁在上还可能随激活状态变化；指定参照窗口就不会。
    /// </summary>
    public bool PlaceAbove(IntPtr handle, IntPtr reference) =>
        OperatingSystem.IsWindows()
        && handle != IntPtr.Zero
        && reference != IntPtr.Zero
        && SetWindowPos(handle, reference, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

    /// <summary>
    /// 按物理像素摆放窗口：<c>SetWindowPos</c> + <c>SWP_NOZORDER | SWP_NOACTIVATE</c>。
    /// 多屏遮罩用它精确盖住每块显示器（Avalonia 的 Position/Width 在 Show 前后不可靠）。
    /// </summary>
    public bool SetBounds(IntPtr handle, int x, int y, int width, int height) =>
        OperatingSystem.IsWindows()
        && handle != IntPtr.Zero
        && width > 0
        && height > 0
        && SetWindowPos(handle, IntPtr.Zero, x, y, width, height, SwpNoZOrder | SwpNoActivate);

    private const uint SwpNoZOrder = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
