namespace Kscan.App.Services;

/// <summary>
/// 模块初始化器：在 <c>Program.Main</c> 的第一条语句之前执行。
/// <para>
/// 存在的唯一理由：崩溃可能发生在 <c>Main</c> 的 JIT / 依赖程序集加载阶段
/// （实测 <c>crash.log</c> 一行都没有、Windows 只记到 <c>0xe0434352</c>），
/// 那种情况下写在 <c>Main</c> 开头的日志永远来不及跑。挂在这里才能把异常原文留下来。
/// </para>
/// <para>
/// 这里只用 BCL：<c>Kscan.Platform</c> 正是可能加载失败的那个程序集。
/// </para>
/// </summary>
internal static class StartupDiagnostics
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init()
    {
        CrashLog.Write("module-init", note: "模块初始化器已执行");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write("unhandled", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
            CrashLog.Write("task", e.Exception);
    }
}
