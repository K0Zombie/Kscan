namespace Kscan.Platform.Abstractions;

/// <summary>
/// "以最高权限开机自启"（Windows 的计划任务；普通 <see cref="IAutoStart"/> 只能普通权限启动）。
/// 与 <see cref="IAutoStart"/> 分开：只有 Windows 有这种需求，Linux 的 .desktop 自启不需要提权。
/// </summary>
public interface IElevatedAutoStart
{
    bool IsSupported { get; }

    bool IsEnabled();

    Task<OperationResult> SetEnabledAsync(bool enabled);
}

/// <summary>不支持"最高权限自启"的平台。</summary>
internal sealed class UnsupportedElevatedAutoStart : IElevatedAutoStart
{
    public bool IsSupported => false;

    public bool IsEnabled() => false;

    public Task<OperationResult> SetEnabledAsync(bool enabled) =>
        Task.FromResult(OperationResult.Fail("当前平台不支持以最高权限开机自启。"));
}
