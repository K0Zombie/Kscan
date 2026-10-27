namespace Kscan.Platform.Abstractions;

/// <summary>一次平台操作的结果（自启开关、写文件这类会失败的动作）。</summary>
public sealed record OperationResult(bool Success, string Message)
{
    public static OperationResult Ok(string message = "") => new(true, message);

    public static OperationResult Fail(string message) => new(false, message);
}

/// <summary>
/// 开机自启。**一律写用户级位置**（Windows 的 <c>HKCU\...\Run</c>、Linux 的
/// <c>~/.config/autostart/*.desktop</c>），不碰 HKLM —— 旧版写 HKLM 导致需要管理员权限，
/// 在 Win11 上直接不可用。
/// </summary>
public interface IAutoStart
{
    bool IsSupported { get; }

    string Description { get; }

    /// <summary>界面文案的 i18n key；为 <c>null</c> 时界面直接用 <see cref="Description"/>。</summary>
    string? DescriptionKey => null;

    bool IsEnabled();

    Task<OperationResult> SetEnabledAsync(bool enabled);
}
