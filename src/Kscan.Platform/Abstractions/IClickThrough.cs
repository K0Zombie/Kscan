namespace Kscan.Platform.Abstractions;

/// <summary>
/// 鼠标穿透：让贴图窗口不接收指针事件，点击直接落到它下面的窗口。
/// <para>
/// Windows 靠窗口扩展样式 <c>WS_EX_TRANSPARENT</c>（配合 <c>WS_EX_LAYERED</c>）；
/// Wayland 上对应 layer-shell 的 <c>set_input_region</c>（设成空区域），
/// Avalonia 的 toplevel 降级路径做不到 —— 那时界面要禁用"穿透"并把原因写清楚。
/// </para>
/// </summary>
public interface IClickThrough
{
    /// <summary>当前平台有没有穿透能力。</summary>
    bool IsSupported { get; }

    /// <summary>给界面显示的一句话说明。</summary>
    string Description { get; }

    /// <summary>i18n key（界面优先用它，缺翻译时退回 <see cref="Description"/>）。</summary>
    string? DescriptionKey { get; }

    /// <summary>
    /// 对指定原生窗口设置/取消穿透；句柄来自 Avalonia 的 <c>TryGetPlatformHandle()</c>。
    /// 返回"设置后的实际状态是否等于期望状态"，而不是"调用有没有抛异常"。
    /// </summary>
    bool TrySetEnabled(IntPtr windowHandle, bool enabled);
}
