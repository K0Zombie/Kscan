using Avalonia;
using Avalonia.Media;
using Kscan.App.Services;
using Kscan.Platform;

namespace Kscan.App;

internal static class Program
{
    // Avalonia 需要 STA 线程；不能用顶层语句，否则拿不到 [STAThread]
    [STAThread]
    public static void Main(string[] args)
    {
        // 兜底日志的挂载在 StartupDiagnostics（模块初始化器）里做：它比这里更早，
        // 能覆盖"Main 本身 JIT 失败 / 依赖程序集加载失败"这种根本跑不到这一行的情况。
        CrashLog.Write("main-enter", note: string.Join(' ', args));

        try
        {
            Run(args);
            CrashLog.Write("main-exit", note: "正常退出");
        }
        catch (Exception ex)
        {
            CrashLog.Write("main-crash", ex);
            throw;   // 保持原来的崩溃/退出码行为，WER 照旧记录
        }
    }

    private static void Run(string[] args)
    {
        // 配置必须在建窗口之前读出来：语言之类的设置会影响第一个窗口的样子。
        // 读失败（文件损坏/无权限）也不阻塞启动 —— 服务内部会回退默认值。
        AppSettingsService.Current.Load();

        // 单实例：已经有实例在跑就把本次启动的意图交给它，然后直接退出。
        // 这一层刻意放在 Avalonia 启动之前 —— 在生命周期里再 Shutdown 会抛异常，
        // 而且启动整个 UI 框架只为退出也太浪费（niri 的 binds 会反复 spawn）。
        var channel = PlatformServices.SingleInstance;

        // 「让已经在跑的那个退出」：除了经 IPC 投递一次，还落一个**退出标记文件**。
        // 文件方式不依赖管道，跨权限级别（一边管理员一边普通）/便携副本都能生效；
        // 而且这里绝不建 UI —— 否则 --exit 会降级成"又开一个窗口"，看起来像没反应。
        if (App.StartupCommand(args) == "exit")
        {
            var delivered = channel.SendAsync("exit").GetAwaiter().GetResult();
            App.RequestExit(!delivered);
            CrashLog.Write(
                "single-instance",
                note: delivered ? "已通过 IPC 请求退出" : "已写入退出标记，等对方自行退出");
            return;
        }

        // **硬性单实例**：内核 Mutex 抢不到就直接退出（最多转发一次命令），
        // 绝不"降级再开一个" —— 以前那条降级路会让两个 kscan 同时跑：
        // 两个托盘、两个库监视器、互相覆盖 library.json，问题表现得很诡异。
        var instanceLock = PlatformServices.InstanceLock;

        if (!instanceLock.TryAcquire())
        {
            var delivered = channel.SendAsync(App.StartupCommand(args)).GetAwaiter().GetResult();
            CrashLog.Write(
                "single-instance",
                note: delivered
                    ? "已有实例在跑，命令已转发，本次退出（正常）"
                    : "已有实例在跑（命令没送到），本次退出：不会再开第二个实例");

            instanceLock.Dispose();
            return;
        }

        // 命令端点只是"附加能力"：拿不到也照样运行（单实例已经由 Mutex 保证）
        if (!channel.TryAcquire(App.HandleCommandAsync))
        {
            CrashLog.Write(
                "single-instance",
                note: "拿到单实例锁，但命令端点被占用（可能上一个实例正在退出）：本次只运行，暂不接收外部命令");
        }

        CrashLog.Write("instance-acquired", note: "拿到单实例锁，开始建 UI");
        App.InitialCommand = App.StartupCommand(args);
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally
        {
            channel.Release();
            instanceLock.Dispose();
        }

    }

    /// <summary>供设计器与单元测试复用。</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        // 显式指定默认字体家族：某些精简环境（例如没跑完整桌面会话的 WSL）里
        // Avalonia 探测不到默认字体名会直接抛 "Default font family name can't be null or empty"。
        // "sans-serif" 是 fontconfig 的标准别名，任何正常 Linux 桌面都能解析。
        .With(new FontManagerOptions { DefaultFamilyName = "sans-serif" })
        // 限制 Skia 的 GPU 资源缓存：界面很简单，缓存用不到默认那么大，常驻内存能省一些
        .With(new SkiaOptions { MaxGpuResourceSizeBytes = 32L * 1024 * 1024 })

        // 注册内嵌的 Inter 字体：Fluent 主题的控件会引用它，不注册就会在渲染文本时抛
        // "Could not create glyphTypeface. Font family: Inter"。
        .WithInterFont()
        .LogToTrace();
}
