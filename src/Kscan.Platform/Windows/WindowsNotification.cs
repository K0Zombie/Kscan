using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Windows;

/// <summary>
/// Windows 通知目前是"空实现"：GUI 有自己的窗口反馈，CLI 直接打终端，
/// 再引一套 WinRT Toast 或 NotifyIcon 不划算。等真需要系统级通知时再补。
/// </summary>
internal sealed class WindowsNotification : INotification
{
    public bool IsSupported => false;

    public string Description => "暂不支持（Windows 上用界面反馈代替）";

    public string? DescriptionKey => "Platform.Notification.Windows";

    public Task ShowAsync(string title, string body, CancellationToken cancellationToken = default)
    {
        Console.Error.WriteLine($"[通知] {title}：{body}");
        return Task.CompletedTask;
    }
}
