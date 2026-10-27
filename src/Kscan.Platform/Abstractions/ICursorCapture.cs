using Kscan.Core.Imaging;

namespace Kscan.Platform.Abstractions;

/// <summary>
/// 截屏时"定格"的光标：图像（BGRA，可能带透明）+ 热点 + 屏幕位置（物理像素）。
/// </summary>
/// <param name="Image">光标图像（BGRA32，左上角为原点）。</param>
/// <param name="HotspotX">热点相对图像左边缘的偏移。</param>
/// <param name="HotspotY">热点相对图像上边缘的偏移。</param>
/// <param name="ScreenX">热点在屏幕上的 X（虚拟桌面坐标）。</param>
/// <param name="ScreenY">热点在屏幕上的 Y（虚拟桌面坐标）。</param>
public sealed record CursorSnapshot(ImageFrame Image, int HotspotX, int HotspotY, int ScreenX, int ScreenY)
{
    /// <summary>光标图像左上角在屏幕上的 X。</summary>
    public int Left => ScreenX - HotspotX;

    /// <summary>光标图像左上角在屏幕上的 Y。</summary>
    public int Top => ScreenY - HotspotY;
}

/// <summary>
/// 光标图像采集：全屏框选遮罩出现时用它把光标"画"出来（屏幕截图本身不含光标）。
/// <para>
/// Windows 用 <c>GetCursorInfo</c> + <c>DrawIconEx</c> 抓当前光标；其它平台返回不支持 ——
/// Wayland 下客户端拿不到光标图像，也没有"读光标形状"的协议。
/// </para>
/// </summary>
public interface ICursorCapture
{
    bool IsSupported { get; }

    /// <summary>当前光标；光标隐藏或拿不到时返回 <c>null</c>。</summary>
    CursorSnapshot? Capture();
}

/// <summary>不支持光标采集的平台。</summary>
internal sealed class UnsupportedCursorCapture : ICursorCapture
{
    public bool IsSupported => false;

    public CursorSnapshot? Capture() => null;
}
