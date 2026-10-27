namespace Kscan.Platform.Abstractions;

/// <summary>
/// 权限提升：Windows 的 UIPI 让普通权限进程注册的全局热键在<b>管理员窗口</b>（任务管理器、提权的
/// cmd / PowerShell / IDE）位于前台时收不到 <c>WM_HOTKEY</c>。以管理员身份运行本程序可以解决，
/// 但整个程序提权有副作用（例如资源管理器往窗口拖放文件会失效），所以做成"按需重启提权"。
/// </summary>
public interface IElevation
{
    /// <summary>当前平台是否支持提权（目前只有 Windows）。</summary>
    bool IsSupported { get; }

    /// <summary>当前进程是否已经以管理员身份运行。</summary>
    bool IsElevated { get; }

    /// <summary>
    /// 用管理员身份重新启动本程序（会弹 UAC）。成功返回 <see cref="OperationResult.Success"/>，
    /// 调用方随后应释放单实例并退出当前进程。
    /// </summary>
    Task<OperationResult> RestartElevatedAsync(string? arguments = null, CancellationToken cancellationToken = default);
}

/// <summary>不支持提权的平台（Linux 没有 UIPI，热键本来就是合成器管的）。</summary>
internal sealed class UnsupportedElevation : IElevation
{
    public bool IsSupported => false;

    public bool IsElevated => false;

    public Task<OperationResult> RestartElevatedAsync(string? arguments = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Fail("当前平台不支持以管理员身份重新启动。"));
}
