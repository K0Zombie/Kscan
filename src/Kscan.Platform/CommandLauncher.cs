using System.ComponentModel;
using System.Diagnostics;
using Kscan.Core.Configuration;

namespace Kscan.Platform;

/// <summary>显式执行用户配置的程序；参数原样传递，shell 由用户自己指定。</summary>
public static class CommandLauncher
{
    public static void Start(HotkeyBinding binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.Command);

        if (binding.RunAsAdmin)
        {
            StartElevated(binding);
            return;
        }

        var start = new ProcessStartInfo
        {
            FileName = binding.Command,
            Arguments = binding.Arguments ?? string.Empty,
            WorkingDirectory = ResolveWorkingDirectory(binding),
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Unable to start the configured command.");
    }

    /// <summary>
    /// 以管理员身份启动。
    /// <para>
    /// Windows：<c>UseShellExecute + Verb=runas</c>，由系统弹 UAC 授权框（用户点是才真正启动，
    /// 点否会拿到 <c>ERROR_CANCELLED(1223)</c>，这里翻译成一句人话）。已提权的进程再跑 command 不会弹框。
    /// </para>
    /// <para>
    /// Linux：套一层 <c>pkexec sh -c "…"</c>（Polkit 弹认证框）；没装 polkit 会明确报错。
    /// pkexec 默认清环境变量，脚本里别依赖登录环境（需要就读 <c>/etc/environment</c>）。
    /// </para>
    /// </summary>
    private static void StartElevated(HotkeyBinding binding)
    {
        var workingDirectory = ResolveWorkingDirectory(binding);

        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo
            {
                FileName = binding.Command,
                Arguments = binding.Arguments ?? string.Empty,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,   // 提权必须走 ShellExecute（UAC 由它负责）
                Verb = "runas",
            };

            try
            {
                // UseShellExecute=true 时 Process.Start 会等 UAC 结果；成功返回的进程句柄用完即释放
                using var process = Process.Start(start);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                throw new InvalidOperationException("已取消管理员授权（UAC）。", ex);
            }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException($"以管理员身份启动失败：{ex.Message}", ex);
            }

            return;
        }

        if (OperatingSystem.IsLinux())
        {
            var commandLine = string.IsNullOrWhiteSpace(binding.Arguments)
                ? binding.Command!
                : $"{binding.Command} {binding.Arguments}";

            var start = new ProcessStartInfo
            {
                FileName = "pkexec",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            start.ArgumentList.Add("sh");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(commandLine);

            try
            {
                using var process = Process.Start(start)
                    ?? throw new InvalidOperationException("unable to start pkexec.");
            }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException(
                    $"以 root 启动失败（需要 Polkit 的 pkexec）：{ex.Message}", ex);
            }

            return;
        }

        throw new PlatformNotSupportedException("当前平台不支持以管理员身份运行命令。");
    }

    private static string ResolveWorkingDirectory(HotkeyBinding binding) =>
        string.IsNullOrWhiteSpace(binding.WorkingDirectory)
            ? Environment.CurrentDirectory
            : binding.WorkingDirectory;

    /// <summary>ERROR_CANCELLED：用户在 UAC 里点了「否」。</summary>
    private const int ErrorCancelled = 1223;
}
