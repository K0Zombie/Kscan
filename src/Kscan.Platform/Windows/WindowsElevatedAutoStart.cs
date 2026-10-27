using System.Diagnostics;
using System.Globalization;
using System.Text;
using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Windows;

/// <summary>
/// "以最高权限开机自启"：走任务计划程序（<c>schtasks /SC ONLOGON /RL HIGHEST</c>），
/// 登录时静默以管理员权限启动，不弹 UAC。
/// <para>
/// 为什么不写 <c>HKCU\...\Run</c>：那条路径只能以当前（普通）权限启动，而提权状态没法持久化。
/// </para>
/// <para>
/// 创建 / 删除<b>最高权限</b>的计划任务需要管理员权限。所以这里先按普通权限试一次，
/// 失败就<b>自动用 UAC 提权重试一次</b>（用户点一次「是」就行），而不是让用户自己"以管理员身份重启"。
/// </para>
/// </summary>
internal sealed class WindowsElevatedAutoStart : IElevatedAutoStart
{
    private const string TaskName = "Kscan";

    /// <summary>
    /// <c>schtasks.exe</c> 的输出是<b>当前系统代码页</b>（中文 Windows 是 GBK），不是 UTF-8。
    /// 用 UTF-8 去读会变乱码，连"拒绝访问"都判断不出来 —— 之前的"创建计划任务失败"就是被这个坑住的。
    /// </summary>
    private static readonly Encoding OutputEncoding = ResolveOutputEncoding();

    public bool IsSupported => OperatingSystem.IsWindows();



    public bool IsEnabled()
    {
        if (!IsSupported)
        {
            return false;
        }

        return RunSchtasks(["/Query", "/TN", TaskName]).ExitCode == 0;
    }

    public async Task<OperationResult> SetEnabledAsync(bool enabled)
    {
        if (!IsSupported)
        {
            return OperationResult.Fail("当前平台不支持以最高权限开机自启。");
        }

        return enabled
            ? await EnableAsync()
            : await DisableAsync();
    }

    private async Task<OperationResult> EnableAsync()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            return OperationResult.Fail("无法确定程序自身路径。");
        }

        var arguments = new List<string>
        {
            "/Create",
            "/TN", TaskName,
            "/SC", "ONLOGON",
            "/RL", "HIGHEST",
            "/TR", $"\"{executable}\"",
            "/F",
        };

        var direct = await Task.Run(() => RunSchtasks(arguments));
        if (direct.ExitCode == 0)
        {
            _ = await Task.Run(() => new WindowsAutoStart().SetEnabledAsync(false));
            return OperationResult.Ok();
        }

        // 普通权限创建最高权限任务必然被拒：直接交给 UAC 提权重试（用户点一次「是」）
        var elevated = await Task.Run(() => RunSchtasksElevated(arguments));
        if (!elevated.Ok)
        {
            return OperationResult.Fail(DescribeFailure(elevated.Output, direct.Output));
        }

        _ = await Task.Run(() => new WindowsAutoStart().SetEnabledAsync(false));
        return OperationResult.Ok();
    }

    private async Task<OperationResult> DisableAsync()
    {
        var arguments = new List<string> { "/Delete", "/TN", TaskName, "/F" };

        var direct = await Task.Run(() => RunSchtasks(arguments));
        if (direct.ExitCode == 0)
        {
            return OperationResult.Ok();
        }

        // 普通权限删不掉（最高权限任务是管理员建的）→ UAC 提权重试一次
        var elevated = await Task.Run(() => RunSchtasksElevated(arguments));
        if (elevated.Ok)
        {
            return OperationResult.Ok();
        }

        // 任务本来就不存在：也算"已经关掉了"
        if (IsTaskMissing(elevated.Output) || IsTaskMissing(direct.Output))
        {
            return OperationResult.Ok();
        }

        return OperationResult.Fail(DescribeFailure(elevated.Output, direct.Output));
    }

    /// <summary>把失败原因拼成一句人话（优先用提权那次的输出，其次普通那次）。</summary>
    private static string DescribeFailure(string elevatedOutput, string directOutput)
    {
        var text = new[] { elevatedOutput, directOutput }
            .Select(Format)
            .FirstOrDefault(candidate => candidate.Length > 0);

        if (text is null)
        {
            return "未知原因（schtasks 没有输出）";
        }

        if (IsAccessDenied(text))
        {
            return "创建计划任务需要管理员授权，但提权被拒绝了。请重试并在 UAC 弹窗里点「是」。";
        }

        return text;
    }

    /// <summary>schtasks 说"这个任务不存在"（各语言写法都兜一下）。</summary>
    private static bool IsTaskMissing(string text) =>
        text.Contains("cannot find the task", StringComparison.OrdinalIgnoreCase)
        || text.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
        || text.Contains("找不到", StringComparison.OrdinalIgnoreCase)
        || text.Contains("不存在", StringComparison.OrdinalIgnoreCase);

    private static bool IsAccessDenied(string text) =>
        text.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Zugriff verweigert", StringComparison.OrdinalIgnoreCase);

    /// <summary>把 schtasks 的原始输出收敛成一行（空输出返回空串）。</summary>
    private static string Format(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var line = trimmed
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? trimmed;

        return line.Length > 200 ? line[..200] + "…" : line;
    }

    private readonly record struct SchtasksResult(int ExitCode, string StdOut, string StdErr)
    {
        public string Output => (StdOut + "\n" + StdErr).Trim();
    }

    private readonly record struct ElevatedResult(bool Ok, string Output);

    private static SchtasksResult RunSchtasks(IEnumerable<string> arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = OutputEncoding,
                StandardErrorEncoding = OutputEncoding,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new SchtasksResult(-1, string.Empty, "无法启动 schtasks.exe");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(15_000);
            return new SchtasksResult(process.HasExited ? process.ExitCode : -1, stdout, stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new SchtasksResult(-1, string.Empty, ex.Message);
        }
    }

    /// <summary>
    /// 用 UAC 提权跑一次 schtasks。提权进程的 stdout 拿不到，所以让 <c>cmd /c</c> 把输出重定向到临时文件，
    /// 跑完读文件再删掉 —— 这样失败原因还是能显示给用户。
    /// </summary>
    private static ElevatedResult RunSchtasksElevated(IReadOnlyList<string> arguments)
    {
        var log = Path.Combine(Path.GetTempPath(), $"kscan-task-{Guid.NewGuid():N}.txt");

        try
        {
            // 先由本进程建好文件：这样它的属主是我们，提权进程只是往里写，
            // 事后普通权限才删得掉（提权进程新建的文件属主是管理员，删不掉）
            File.WriteAllText(log, string.Empty);
            // /v:on + !ERRORLEVEL! 才能拿到 schtasks 执行**之后**的退出码
            //（/c 整行是一次解析，%ERRORLEVEL% 会在 schtasks 跑之前就展开，拿不到真实结果）
            var commandLine = "/v:on /c schtasks.exe " + string.Join(' ', arguments.Select(Quote))
                              + " > \"" + log + "\" 2>&1 & echo __EXIT__!ERRORLEVEL!>> \"" + log + "\"";

            var startInfo = new ProcessStartInfo("cmd.exe", commandLine)
            {
                UseShellExecute = true,   // 提权必须走 ShellExecute
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new ElevatedResult(false, "无法启动提权进程");
            }

            process.WaitForExit(60_000);

            var raw = File.Exists(log) ? ReadLog(log) : string.Empty;
            var (exitCode, text) = ParseExitCode(raw);

            // 拿不到退出码（老系统 / 被安全软件拦住）时保守地认为成功，让调用方再用查询兜底
            return new ElevatedResult(exitCode is null || exitCode == 0, text);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = 用户在 UAC 里点了「否」
            return new ElevatedResult(false, "已取消管理员授权（UAC）。");
        }
        catch (Exception ex)
        {
            return new ElevatedResult(false, ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(log))
                {
                    File.Delete(log);
                }
            }
            catch (Exception)
            {
                // 提权进程写的临时文件普通权限可能删不掉 —— 不影响功能，留给系统清理
            }
        }
    }

    /// <summary>从日志里取出 <c>__EXIT__&lt;code&gt;</c> 那一行，剩下的当正文。</summary>
    private static (int? ExitCode, string Text) ParseExitCode(string raw)
    {
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var exit = lines
            .FirstOrDefault(line => line.StartsWith("__EXIT__", StringComparison.Ordinal));

        int? code = exit is not null && int.TryParse(exit["__EXIT__".Length..].Trim(), out var parsed)
            ? parsed
            : null;

        var text = string.Join('\n', lines.Where(line => !line.StartsWith("__EXIT__", StringComparison.Ordinal)));
        return (code, text);
    }

    private static string ReadLog(string path)
    {
        try
        {
            return OutputEncoding.GetString(File.ReadAllBytes(path));
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static string Quote(string argument) =>
        argument.Contains(' ') ? $"\"{argument}\"" : argument;

    private static Encoding ResolveOutputEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception)
        {
            return Encoding.UTF8;
        }
    }
}
