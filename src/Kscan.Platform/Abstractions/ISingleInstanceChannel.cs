namespace Kscan.Platform.Abstractions;

/// <summary>
/// 单实例通道：让"后来启动的进程"能把命令交给"已经跑着的那个进程"。
/// <para>
/// Windows 用具名管道、Linux/macOS 用 Unix domain socket —— 两者都在 BCL 里
/// （<c>System.IO.Pipes</c> / <c>System.Net.Sockets</c>），不需要额外依赖。
/// 协议就是一行 UTF-8 文本命令（<c>activate</c> / <c>capture-region</c>）。
/// </para>
/// <para>
/// 用法：长驻进程先 <see cref="TryAcquire"/>；返回 <c>false</c> 说明已经有一个实例在跑，
/// 这时应该把命令 <see cref="SendAsync"/> 过去然后自己退出。
/// </para>
/// </summary>
public interface ISingleInstanceChannel
{
    /// <summary>端点标识（诊断用，例如 <c>\\.\pipe\kscan-me</c> 或 <c>/run/user/1000/kscan.sock</c>）。</summary>
    string Endpoint { get; }

    /// <summary>
    /// 尝试成为那个唯一实例：成功返回 <c>true</c> 并开始接收命令；
    /// 已经有实例在跑时返回 <c>false</c>（此时不要调用 <see cref="Release"/>）。
    /// </summary>
    bool TryAcquire(Func<string, Task> onCommand);

    /// <summary>把命令发给已运行的实例；没有实例或发送失败时返回 <c>false</c>。</summary>
    Task<bool> SendAsync(string command, CancellationToken cancellationToken = default);

    /// <summary>停止监听并释放端点。</summary>
    void Release();
}
