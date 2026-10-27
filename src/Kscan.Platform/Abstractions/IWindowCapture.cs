using Kscan.Core.Imaging;

namespace Kscan.Platform.Abstractions;

/// <summary>
/// 整窗抓图结果：<paramref name="Image"/> 是裁掉未绘制边之后的图，
/// <paramref name="Coverage"/> 是这张图对应的<b>屏幕矩形</b>（贴图定位、屏幕裁剪回退都用它）。
/// </summary>
public readonly record struct WindowCapture(ImageFrame Image, ScreenRect Coverage);

/// <summary>
/// 整个窗口的离屏渲染：窗口比屏幕大（超出屏幕边缘）时，屏幕截图只能拿到露出来的那一块，
/// 用它可以拿到<b>完整窗口</b>（包括屏幕外的部分），不需要用户先把窗口挪出来。
/// <para>
/// Windows 用 <c>PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)</c>；其它平台没有等价能力
/// （Wayland 客户端连别的窗口内容都拿不到），返回不支持。
/// </para>
/// </summary>
public interface IWindowCapture
{
    bool IsSupported { get; }

    /// <summary>
    /// 渲染整个窗口（物理像素，左上角为窗口原点）。拿不到（句柄失效 / 应用拒绝渲染）时返回 <c>null</c>。
    /// </summary>
    ImageFrame? Capture(IntPtr handle);

    /// <summary>
    /// 同 <see cref="Capture"/>，但同时给出"这张图对应屏幕上哪一块"。默认实现用窗口的
    /// <see cref="Capture"/> 结果 + 空矩形（调用方据此保持原行为）。
    /// </summary>
    WindowCapture? CaptureWhole(IntPtr handle) =>
        Capture(handle) is { } image ? new WindowCapture(image, default) : null;
}

/// <summary>不支持整窗渲染的平台。</summary>
internal sealed class UnsupportedWindowCapture : IWindowCapture
{
    public bool IsSupported => false;

    public ImageFrame? Capture(IntPtr handle) => null;
}