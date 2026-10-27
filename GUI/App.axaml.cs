using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Kscan.App.Services;
using Kscan.App.ViewModels;
using Kscan.App.Views;
using Kscan.Core.Pipeline;
using Kscan.Platform;

namespace Kscan.App;

public partial class App : Application
{
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> PendingCommands = new();
    private static bool _commandsReady;
    internal static string InitialCommand { get; set; } = "activate";
    private DesktopBackgroundController? _background;
    public void ExitApplication() => _background?.Exit();
    public void ShowMainWindow() => _background?.Show();
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// 退出标记文件：别的进程写它一下，本实例看到就自己退出。
    /// <para>
    /// 比命名管道可靠：管道在"一边管理员一边普通"或不同便携副本之间可能连不上，
    /// 而按用户配置目录写文件基本总能成功（不行还有 IPC 那条路兜着）。
    /// </para>
    /// </summary>
    internal static string QuitFlagFile => Path.Combine(PlatformPaths.ConfigDirectory, "quit.flag");

    /// <summary>请求"已在运行的那个实例"退出（<paramref name="writeFlag"/> 为 true 时落标记文件）。</summary>
    internal static void RequestExit(bool writeFlag)
    {
        if (!writeFlag)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(PlatformPaths.ConfigDirectory);
            File.WriteAllText(QuitFlagFile, DateTimeOffset.Now.ToString("O"));
        }
        catch (Exception ex)
        {
            Log($"[ipc] 写退出标记失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>退出前把屏幕上各张贴图的位置/朝向/透明度刷进库参数（贴图本身留在库里）。</summary>
    private static void FlushPins(string reason)
    {
        try
        {
            foreach (var pin in PinManager.Current.Windows)
            {
                if (pin.LibraryPath is { Length: > 0 })
                {
                    PinLibraryService.SaveWindowState(pin, immediate: true);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[library] 退出前刷新贴图状态失败（{reason}）：{ex.Message}");
        }
    }

    /// <summary>每秒看一次退出标记（代价极低，且不需要 IPC 连接）。</summary>
    private static void WatchQuitFlag(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        timer.Tick += (_, _) =>
        {
            try
            {
                if (!File.Exists(QuitFlagFile))
                {
                    return;
                }

                File.Delete(QuitFlagFile);
                Log("[ipc] 看到退出标记，退出");
                timer.Stop();

                // 与托盘退出一样：先把各张贴图的状态刷进库参数，再关
                FlushPins("quit flag");

                desktop.Shutdown();
            }
            catch (Exception)
            {
                // 删不掉/读不到就下次再说
            }
        };

        timer.Start();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 上一次 --exit 留下的标记不能影响这次正常启动
        try
        {
            File.Delete(QuitFlagFile);
        }
        catch (Exception)
        {
            // 文件不存在或被占用：无所谓
        }

        // 语言必须在创建窗口之前定下来：字典换好之后 DynamicResource 第一次取值就是对的，
        // 否则第一帧会闪一下默认（其实是"缺 key"，T() 会退化成返回 key 本身）。
        I18n.Current.Apply(AppSettingsService.Current.Settings.Language);

        // 用户命令（user/cmd/*.json）要在建热键服务之前加载好
        UserCommandStore.Current.Reload();

        // 主题要在建窗口之前应用：否则第一帧会用默认配色闪一下
        ThemeService.Current.Initialize();

        // 预处理方法目录的分享说明（user/method/README.md）：首次启动生成，之后不动
        MethodStore.EnsureReadme(PlatformPaths.MethodsDirectory);

        // 贴图库：读 library.json + 认领 data\user\pins 下的分组目录 + 挂目录监控。
        // 注意：只建"库"，**不会**自动把图贴到屏幕上 —— 用户自己在贴图管理里双击取用。
        PinLibraryService.Load();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // 单实例的"成为服务端"那一步在 Program.Main 里做（早于 Avalonia 启动）；
            // 这里只负责退出时把端点收掉。
            desktop.ShutdownRequested += (_, _) =>
            {
                FlushPins("shutdown");
                PlatformServices.SingleInstance.Release();
            };

            // 必须在这里挂 DataContext：XAML 里的 x:DataType 只是编译期类型提示，
            // 不设 DataContext 的话所有 {Binding ...} 都是死的 —— 而界面看上去完全正常。
            var window = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
            desktop.MainWindow = window;
            WatchQuitFlag(desktop);

            _background = new DesktopBackgroundController(window,
                () => AppSettingsService.Current.Settings,
                () =>
                {
                    FlushPins("tray quit");
                    desktop.Shutdown();
                },
                new DesktopTray(this));
            desktop.Exit += (_, _) => _background.Dispose();

            void Ready()
            {
                if (_commandsReady) return;
                window.StartMouseGesture();
                _commandsReady = true;
                if (InitialCommand == "capture-region") PendingCommands.Enqueue("capture-region");
                while (PendingCommands.TryDequeue(out var pending)) _ = HandleCommandAsync(pending);
            }

            window.Opened += (_, _) => Ready();
            if (_background.ShouldStartHidden(InitialCommand))
            {
                // lifetime.Start 自动 Show(MainWindow)，先留空，到消息循环启动后再赋回，避免闪窗。
                desktop.MainWindow = null;
                Dispatcher.UIThread.Post(() => { desktop.MainWindow = window; Ready(); });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 本次启动想让已在运行的实例做什么。命令行传 <c>--capture</c> 就是"唤起并直接框选"；
    /// 传 <c>--exit</c> 就是"让已经在跑的那个退出"（部署/更新时用：不必手动去托盘里点退出）。
    /// </summary>
    internal static string StartupCommand(string[] args) =>
        args.Any(a => a is "--exit" or "-x")
            ? "exit"
            : args.Any(a => a is "--capture" or "-c") ? "capture-region" : "activate";

    /// <summary>处理来自另一个进程的命令（可能在后台线程 → 一律投递到 UI 线程执行）。</summary>
    internal static Task HandleCommandAsync(string command)
    {
        Log($"[ipc] 收到命令：{command}");

        Dispatcher.UIThread.Post(() =>
        {
            if (!_commandsReady)
            {
                PendingCommands.Enqueue(command);
                return;
            }
            if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
                desktop.MainWindow is not { } window)
            {
                Log("[ipc] 窗口已关闭，命令被忽略");
                return;
            }

            // 退出命令：更新程序时不必手动去托盘里点，直接投递这一条即可
            if (command == "exit")
            {
                Log("[ipc] 收到退出命令，准备退出");
                desktop.Shutdown();
                return;
            }

            (Current as App)?.ShowMainWindow();

            if (command != "capture-region")
            {
                return;
            }

            if (window.DataContext is MainViewModel model)
            {
                Log("[ipc] 触发框选截图");

                // 页签不在这里切：只有真的识别出结果时 ScanViewModel 才通知切回「识别」页
                _ = model.Scan.CaptureRegionCommand.ExecuteAsync(null);
            }
            else
            {
                Log($"[ipc] DataContext 不是 MainViewModel（{window.DataContext?.GetType().Name ?? "null"}）");
            }
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// GUI 没有控制台，诊断信息交给 <see cref="GuiLog"/>（写到 %APPDATA%\kscan\gui.log）。
    /// <para>
    /// 以前这里自己 File.AppendAllText 且只捕 IOException：文件被锁成只读时抛的
    /// UnauthorizedAccessException 会一路冒到 Main，进程直接崩（双击后闪退）。
    /// </para>
    /// </summary>
    private static void Log(string message) => GuiLog.Write(message);
}
