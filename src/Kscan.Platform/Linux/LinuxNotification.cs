using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Linux;

/// <summary>
/// Linux 通知：走 D-Bus 的 <c>notify-send</c>。
/// niri 没有系统托盘，所以通知是扫码结果的主要反馈通道。
/// </summary>
internal sealed class LinuxNotification : INotification
{
    public bool IsSupported => OperatingSystem.IsLinux() && ShellRunner.Exists("notify-send");

    public string Description => IsSupported ? "D-Bus 通知（notify-send）" : "缺少 notify-send（libnotify）";

    public string? DescriptionKey => IsSupported ? "Platform.Notification.Linux" : "Platform.Notification.Missing";

    public async Task ShowAsync(string title, string body, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            return;
        }

        // 用参数数组语义：把标题与正文各自引号包起来，避免被 shell 拆词
        await ShellRunner.RunAsync(
            $"notify-send --app-name=kscan {Quote(title)} {Quote(body)}",
            cancellationToken).ConfigureAwait(false);
    }

    private static string Quote(string text) => "'" + text.Replace("'", @"'\''") + "'";
}
