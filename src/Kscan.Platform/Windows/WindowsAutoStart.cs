using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Windows;

/// <summary>
/// Windows 开机自启：写 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>。
/// <para>
/// 特意用 P/Invoke 直接调 advapi32，而不是引 <c>Microsoft.Win32.Registry</c> 包：
/// 这样平台层不需要额外依赖，且写的是**用户级**键，不需要管理员权限
/// （旧版 QrCodeScanner 写 HKLM，导致 Win11 下必须提权、功能被禁用）。
/// </para>
/// </summary>
internal sealed class WindowsAutoStart : IAutoStart
{
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Kscan";

    private const uint KeyQueryValue = 0x0001;
    private const uint KeySetValue = 0x0002;
    private const uint RegSz = 1;
    private const int ErrorSuccess = 0;
    private const int ErrorFileNotFound = 2;

    /// <summary>HKEY_CURRENT_USER 的预定义句柄值。</summary>
    private static readonly IntPtr HkeyCurrentUser = new(-2147483647);

    public bool IsSupported => OperatingSystem.IsWindows();

    public string Description => @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run（无需管理员权限）";

    public string? DescriptionKey => "Platform.AutoStart.Windows";

    public bool IsEnabled()
    {
        if (!IsSupported)
        {
            return false;
        }

        if (RegOpenKeyExW(HkeyCurrentUser, RunSubKey, 0, KeyQueryValue, out var key) != ErrorSuccess)
        {
            return false;
        }

        try
        {
            uint size = 0;
            var status = RegQueryValueExW(key, ValueName, IntPtr.Zero, out _, null, ref size);
            return status == ErrorSuccess && size > 0;
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    public Task<OperationResult> SetEnabledAsync(bool enabled)
    {
        if (!IsSupported)
        {
            return Task.FromResult(OperationResult.Fail("当前平台不支持。"));
        }

        return Task.Run(() =>
        {
            var access = KeyQueryValue | KeySetValue;
            var status = RegOpenKeyExW(HkeyCurrentUser, RunSubKey, 0, access, out var key);
            if (status != ErrorSuccess)
            {
                return OperationResult.Fail($"打开注册表失败（错误码 {status}）。");
            }

            try
            {
                if (!enabled)
                {
                    var deleted = RegDeleteValueW(key, ValueName);
                    return deleted is ErrorSuccess or ErrorFileNotFound
                        ? OperationResult.Ok()
                        : OperationResult.Fail($"删除注册表项失败（错误码 {deleted}）。");
                }

                var executable = Environment.ProcessPath;
                if (string.IsNullOrEmpty(executable))
                {
                    return OperationResult.Fail("无法确定程序自身路径。");
                }

                // 路径可能含空格，加引号
                var value = $"\"{executable}\"";
                var bytes = Encoding.Unicode.GetBytes(value + "\0");
                var written = RegSetValueExW(key, ValueName, 0, RegSz, bytes, (uint)bytes.Length);

                return written == ErrorSuccess
                    ? OperationResult.Ok()
                    : OperationResult.Fail($"写入注册表失败（错误码 {written}）。");
            }
            catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
            {
                return OperationResult.Fail(ex.Message);
            }
            finally
            {
                RegCloseKey(key);
            }
        });
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyExW(IntPtr hKey, string subKey, uint options, uint desiredAccess, out IntPtr result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegSetValueExW(IntPtr hKey, string valueName, uint reserved, uint type, byte[] data, uint dataSize);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueExW(IntPtr hKey, string valueName, IntPtr reserved, out uint type, byte[]? data, ref uint dataSize);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegDeleteValueW(IntPtr hKey, string valueName);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);
}
