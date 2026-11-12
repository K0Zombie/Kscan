using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Kscan.Platform;

/// <summary>Use the interactive desktop user's token, never inherit an elevated parent token.</summary>
internal static class WindowsUnelevatedLauncher
{
    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// 当前桌面是否提供可用于“普通权限启动”的 shell 令牌。
    /// <para>
    /// CI / 服务会话里没有 Explorer，或 Explorer 本身已提权时返回 <c>false</c>；
    /// 测试用它判断“拒绝降权启动”是不是当前环境的预期行为。
    /// </para>
    /// </summary>
    internal static bool CanLaunchUnelevated()
    {
        if (!OperatingSystem.IsWindows()) return true;

        try
        {
            var shell = GetShellWindow();
            if (shell == IntPtr.Zero) return false;
            GetWindowThreadProcessId(shell, out var pid);
            using var process = OpenProcess(0x1000, false, pid);
            if (process.IsInvalid) return false;
            if (!OpenProcessToken(process, 0x000A, out var token)) return false;
            using (token)
            {
                return GetTokenInformation(token, 20, out var elevation, sizeof(int), out _)
                    && elevation == 0;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static void Start(string command, string? arguments, string directory)
    {
        try
        {
            var shell = GetShellWindow();
            if (shell == IntPtr.Zero) throw new InvalidOperationException("未找到普通用户桌面进程。");
            GetWindowThreadProcessId(shell, out var pid);
            using var process = OpenProcess(0x1000, false, pid);
            if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!OpenProcessToken(process, 0x000A, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            {
                // An elevated Explorer must never be used as a 'normal' launch token.
                if (!GetTokenInformation(token, 20, out var elevation, sizeof(int), out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (elevation != 0) throw new InvalidOperationException("桌面进程本身已提权，无法安全使用普通权限。");
                if (!DuplicateTokenEx(token, 0x02000000, IntPtr.Zero, 2, 1, out var primary))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                using (primary)
                {
                    var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
                    var line = new StringBuilder(BuildCommandLine(command, arguments));
                    if (!CreateProcessWithTokenW(primary, 1, null, line, 0x08000000, IntPtr.Zero,
                            directory, ref startup, out var child))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    CloseHandle(child.Thread);
                    CloseHandle(child.Process);
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法以普通用户权限启动命令：{ex.Message}。未改为管理员运行。", ex);
        }
    }

    internal static string BuildCommandLine(string command, string? arguments)
    {
        if (command.Contains('"')) throw new ArgumentException("程序路径中不能包含双引号。", nameof(command));
        return $"\"{command}\"" + (string.IsNullOrEmpty(arguments) ? "" : " " + arguments);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedPointer, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out int value, int size, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes, int level, int type, out SafeAccessTokenHandle primary);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, uint flags, string? application,
        StringBuilder command, uint creationFlags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
