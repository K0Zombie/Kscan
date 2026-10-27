using System.Diagnostics;

namespace Kscan.Platform.Linux;

/// <summary>在 Linux 上跑外部命令的小工具（截图与通知都要用）。</summary>
internal static class ShellRunner
{
    /// <summary>在当前 PATH 里找可执行文件（用于能力探测，避免"命令不存在"的报错糊在脸上）。</summary>
    public static bool Exists(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var directory in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, executable);
                if (File.Exists(candidate) && (OperatingSystem.IsWindows() ||
                    (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // PATH 里有非法路径，跳过
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return false;
    }

    /// <summary>
    /// 用 <c>/bin/sh -c</c> 执行命令并拿到结果。
    /// stdout 以二进制读（截图工具会把 PNG 直接吐到 stdout），stderr 作为文本返回。
    /// </summary>
    public static async Task<ShellResult> RunAsync(string command, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new ShellResult(-1, [], "无法启动 /bin/sh");
        }

        // stdout 与 stderr 必须并行读，否则管道写满会互相阻塞
        using var stdout = new MemoryStream();
        var copyOut = process.StandardOutput.BaseStream.CopyToAsync(stdout, cancellationToken);
        var readError = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await copyOut.ConfigureAwait(false);
            var error = await readError.ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new ShellResult(process.ExitCode, stdout.ToArray(), error);
        }
        catch
        {
            // 取消截图时同时结束 slurp/grim，避免后台仍挂着选区或占住管道。
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            await Task.WhenAll(copyOut, readError).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }
    }
}

internal sealed record ShellResult(int ExitCode, byte[] StandardOutput, string StandardError);
