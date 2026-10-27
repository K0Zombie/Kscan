namespace Kscan.Platform.Abstractions;

/// <summary>
/// 窗口层级控制：把某个窗口强行提到最上层（不激活、不改位置大小）。
/// <para>
/// 用途：框选遮罩要压在全屏程序（浏览器 <c>F11</c>、播放器、边框屏应用）上面。这些程序自己也会把自己
/// 设为 topmost，两个 topmost 窗口的先后是按"最后设置的那个在上"决定的，所以遮罩显示后要再抢一次。
/// </para>
/// </summary>
public interface IWindowPlacement
{
    bool IsSupported { get; }

    /// <summary>把窗口提到最上层（<c>HWND_TOPMOST</c>）。返回是否成功。</summary>
    bool ForceTopmost(IntPtr handle);

    /// <summary>
    /// 设置/取消"始终置顶"（<c>HWND_TOPMOST</c> / <c>HWND_NOTOPMOST</c>），不激活、不改位置大小。
    /// 返回是否真的改成功；平台不支持时返回 <c>false</c>，调用方退回框架自己的 <c>Topmost</c> 属性。
    /// </summary>
    bool SetTopmost(IntPtr handle, bool enabled) => false;

    /// <summary>
    /// 把 <paramref name="handle"/> 插到 <paramref name="reference"/> 窗口的<b>正上方</b>
    /// （<c>SetWindowPos</c> 的 <c>hWndInsertAfter = reference</c>）。
    /// <para>
    /// 比 <see cref="ForceTopmost"/> 更确定：后者只是"进 topmost 带的最上面"，两个都置顶的窗口谁在上仍看时序；
    /// 这条直接指定"就在这个窗口上面"，用来让贴图稳定地浮在管理窗口上。
    /// </para>
    /// </summary>
    bool PlaceAbove(IntPtr handle, IntPtr reference) => false;

    /// <summary>
    /// 按**物理像素**直接摆放窗口（<c>SetWindowPos</c>，不改层级、不激活）。
    /// <para>
    /// 为什么需要它：Avalonia 的 <c>Position</c>/<c>Width</c> 在 <c>Show()</c> 前后行为不一致，
    /// 多屏遮罩这种"必须精确盖住某块显示器"的场景靠它不稳（实测出现"主屏画面跑到副屏、主屏变黑"）。
    /// </para>
    /// </summary>
    bool SetBounds(IntPtr handle, int x, int y, int width, int height) => false;
}

/// <summary>不支持该能力的平台。</summary>
internal sealed class UnsupportedWindowPlacement : IWindowPlacement
{
    public bool IsSupported => false;

    public bool ForceTopmost(IntPtr handle) => false;

    public bool SetTopmost(IntPtr handle, bool enabled) => false;

    public bool PlaceAbove(IntPtr handle, IntPtr reference) => false;
}
