using System.Text;

namespace Kscan.App.Services;

/// <summary>
/// 启动 / 未处理异常的兜底记录：写在 <c>%APPDATA%\kscan\crash.log</c>。
/// <para>
/// 只依赖 BCL：连 <c>Kscan.Platform</c> 都不碰 —— 那个程序集恰好可能是加载失败的那个，
/// 让日志本身跟着失败就等于什么都没记。
/// </para>
/// <para>
/// 为什么不用配置目录：便携副本的配置目录（<c>&lt;exe&gt;\data</c>）恰好是"可能不可写"的那个位置，
/// 崩溃原因往往就在那上面 —— 把证据写进同一处会连日志一起丢掉。
/// </para>
/// </summary>
internal static class CrashLog
{
    private static readonly object Gate = new();

    /// <summary>写一条记录。<paramref name="exception"/> 可以为 null（只记阶段与说明）。</summary>
    public static void Write(string stage, Exception? exception = null, string? note = null)
    {
        try
        {
            var text = new StringBuilder();
            text.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(stage).Append(']');

            if (!string.IsNullOrEmpty(note))
            {
                text.Append(' ').Append(note);
            }

            text.Append(" | pid=").Append(Environment.ProcessId)
                .Append(" dir=").Append(AppContext.BaseDirectory);

            if (exception is not null)
            {
                text.AppendLine().Append(exception);
            }

            text.AppendLine();

            lock (Gate)
            {
                var text2 = text.ToString();
                foreach (var path in ResolvePaths())
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.AppendAllText(path, text2, new UTF8Encoding(false));
                        break;   // 写成功一处就够了
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
            // 记日志本身失败就放弃：不能在崩溃路径上再抛一次
        }
    }

    /// <summary>
    /// 日志候选位置，按优先级：<c>%APPDATA%\kscan\crash.log</c> → <c>&lt;exe&gt;\crash.log</c>。
    /// 两个都试是刻意的：崩溃原因很可能就是"某个目录不可写"，只认一个位置等于把证据一起丢掉。
    /// </summary>
    public static IEnumerable<string> ResolvePaths()
    {
        // 先用普通 try 取值（不能在带 catch 的 try 里 yield）
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
            yield return Path.Combine(appData, "kscan", "crash.log");
        }

        yield return Path.Combine(AppContext.BaseDirectory, "crash.log");

        yield return Path.Combine(Path.GetTempPath(), "kscan-crash.log");
    }
}
