namespace Kscan.Platform.Abstractions;

/// <summary>
/// 系统通知。Windows 上暂用气泡/日志，Linux 上走 D-Bus（<c>notify-send</c> 协议）。
/// 注意：niri 没有系统托盘，所以 Linux 的通知是主要反馈通道。
/// </summary>
public interface INotification
{
    bool IsSupported { get; }

    string Description { get; }

    /// <summary>界面文案的 i18n key；为 <c>null</c> 时界面直接用 <see cref="Description"/>。</summary>
    string? DescriptionKey => null;

    Task ShowAsync(string title, string body, CancellationToken cancellationToken = default);
}
