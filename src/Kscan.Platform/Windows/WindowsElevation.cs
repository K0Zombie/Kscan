using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Windows;

/// <summary>
/// Windows 提权：检测当前进程是否管理员，并以 <c>runas</c> 重新启动自己。
/// <para>
/// 为什么需要它：UIPI 会让普通权限进程的全局热键在管理员窗口（任务管理器等）位于前台时收不到
/// <c>WM_HOTKEY</c>，换成低级键盘钩子也一样。提权是最直接的解法，但整个程序提权会让
/// 资源管理器往窗口拖放文件失效，所以做成"用户按需点一下"而不是默认提权。
/// </para>
/// </summary>
internal sealed class WindowsElevation : IElevation
{
    private const int TokenQuery = 0x0008;
    private const int TokenElevation = 20;
    private const uint SeeMaskNoAsync = 0x00000100;
    private const int SwShownormal = 1;

    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>ShellExecuteEx 结构体的字节大小（布局护栏测试用；x64 应为 112）。</summary>
    internal static int ShellExecuteInfoSize => Marshal.SizeOf<ShellExecuteInfo>();

    public bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            var process = GetCurrentProcess();
            if (!OpenProcessToken(process, TokenQuery, out var token))
            {
                return false;
            }

            try
            {
                var buffer = Marshal.AllocHGlobal(sizeof(int));
                try
                {
                    return GetTokenInformation(token, TokenElevation, buffer, sizeof(int), out _)
                        && Marshal.ReadInt32(buffer) != 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
    }

    public Task<OperationResult> RestartElevatedAsync(string? arguments = null, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            return Task.FromResult(OperationResult.Fail("当前平台不支持以管理员身份重新启动。"));
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            return Task.FromResult(OperationResult.Fail("无法确定程序自身路径。"));
        }

        return Task.Run(() =>
        {
            var info = new ShellExecuteInfo
            {
                Size = Marshal.SizeOf<ShellExecuteInfo>(),
                Mask = SeeMaskNoAsync,
                Verb = "runas",
                File = executable,
                Parameters = string.IsNullOrWhiteSpace(arguments) ? null : arguments,
                Show = SwShownormal,
            };

            try
            {
                if (!ShellExecuteExW(ref info))
                {
                    var error = Marshal.GetLastWin32Error();

                    // 1223 = 用户取消了 UAC 提示；不用报错，界面自己会恢复
                    return error == 1223
                        ? OperationResult.Fail("已取消提权。")
                        : OperationResult.Fail($"以管理员身份启动失败（Win32 错误 {error}）。");
                }

                return OperationResult.Ok();
            }
            catch (Win32Exception ex)
            {
                return OperationResult.Fail(ex.Message);
            }
        }, cancellationToken);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show;
        public IntPtr InstApp;
        public IntPtr IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public IntPtr ClassKey;
        public uint HotKey;

        // union { HICON hIcon; HANDLE hMonitor; } —— 这个 8 字节不能省：
        // 少写它会让 hProcess 的偏移错位、cbSize 偏小，ShellExecuteEx 直接返回失败（UAC 都不弹）。
        public IntPtr IconOrMonitor;

        public IntPtr Process;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, int desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int length, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfo info);
}
