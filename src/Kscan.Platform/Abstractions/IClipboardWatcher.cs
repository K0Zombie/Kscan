namespace Kscan.Platform.Abstractions;

/// <summary>
/// 剪贴板变更监听。
/// <para>
/// 这是个典型的"平台能力差异"：Windows 可以用 <c>GetClipboardSequenceNumber</c> 廉价轮询，
/// 而 <b>Wayland 核心协议不给普通客户端"剪贴板内容变化"的通知</b>（只有拿到焦点的客户端才可能看到），
/// 所以不支持的平台直接返回 <see cref="IsSupported"/> = false，界面照实说明而不是假装能监听。
/// </para>
/// </summary>
public interface IClipboardWatcher
{
    bool IsSupported { get; }

    /// <summary>人类可读的实现说明（设置页展示）。</summary>
    string Description { get; }

    /// <summary>界面文案的 i18n key；为 <c>null</c> 时界面直接用 <see cref="Description"/>。</summary>
    string? DescriptionKey => null;

    /// <summary>剪贴板变了（可能在后台线程）。回调里只做"投递到 UI 线程"这件事，别做 I/O。</summary>
    event EventHandler? Changed;

    /// <summary>开始监听；重复调用是幂等的。</summary>
    void Start();

    void Stop();

    /// <summary>
    /// 把"当前状态"记为已处理。程序自己写剪贴板后必须调用，
    /// 否则会自己触发自己（例如把生成好的条码图片复制进剪贴板，又被当成"新图片"去识别）。
    /// </summary>
    void MarkHandled();

    /// <summary>
    /// 剪贴板的版本号：内容每变一次就换一个值。Windows 用 <c>GetClipboardSequenceNumber</c>；
    /// 读不到（平台没这个能力）返回 <c>null</c>，调用方据此退化 ——
    /// 例如"贴图贴回截图原位"要先确认剪贴板里还是那张截图，判不了就老老实实居中。
    /// </summary>
    long? SequenceNumber => null;
}
