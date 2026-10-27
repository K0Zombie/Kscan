using System.Text;

namespace Kscan.App.Services;

/// <summary>
/// GUI 的诊断日志（没有控制台，只能落文件）。
/// <para>
/// 位置：<c>%APPDATA%\kscan\gui.log</c>，写不进去就依次退到 <c>&lt;exe&gt;\gui.log</c>、
/// <c>%TEMP%\kscan-gui.log</c>。
/// </para>
/// <para>
/// 为什么不再直接写 <c>%TEMP%\kscan-gui.log</c>：实测某些机器上那个文件会被"锁成只读"
/// （可读、写被拒），而日志<b>绝不能影响功能</b> —— 更要命的是它以前只捕 <see cref="IOException"/>，
/// 写失败抛的是 <see cref="UnauthorizedAccessException"/>，会从 <c>Opened</c> 事件一路冒到 Main
/// 把进程打死（用户看到的就是"双击后闪退"）。这里任何异常都吞掉。
/// </para>
/// </summary>
internal static class GuiLog
{
    private static readonly object Gate = new();

    /// <summary>追加一行诊断日志；失败一律忽略。</summary>
    public static void Write(string message)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}";
            lock (Gate)
            {
                foreach (var path in Candidates())
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.AppendAllText(path, line, new UTF8Encoding(false));
                        return;   // 写成功一处就够了
                    }
                    catch (Exception)
                    {
                        // 换下一个位置再试
                    }
                }
            }
        }
        catch (Exception)
        {
            // 记日志本身失败也不能抛
        }
    }

    private static IEnumerable<string> Candidates()
    {
        var appData = string.Empty;
        try
        {
            appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }
        catch (Exception)
        {
            appData = string.Empty;
        }

        if (!string.IsNullOrEmpty(appData))
        {
            yield return Path.Combine(appData, "kscan", "gui.log");
        }

        yield return Path.Combine(AppContext.BaseDirectory, "gui.log");

        yield return Path.Combine(Path.GetTempPath(), "kscan-gui.log");
    }
}
