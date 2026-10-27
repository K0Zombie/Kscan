using Kscan.Core.Imaging;
using Kscan.Platform.Abstractions;
using Kscan.Platform.Linux;
using Kscan.Platform.Windows;

namespace Kscan.Platform;

/// <summary>当前环境的平台能力快照（设置页展示，也用于决定界面上的按钮是否可用）。</summary>
public sealed record PlatformCapabilities(
    string OperatingSystem,
    string SessionType,
    string ScreenCapture,
    bool ScreenCaptureSupported,
    string Notification,
    bool NotificationSupported,
    string AutoStart,
    bool AutoStartSupported,
    string ClipboardWatch,
    bool ClipboardWatchSupported)
{
    public string Summary =>
        $"{OperatingSystem} / {SessionType} · 截图：{ScreenCapture} · 通知：{Notification} · 自启：{AutoStart}";
}

/// <summary>
/// 平台服务的统一入口：按运行时操作系统选实现。
/// 全部走"能力探测 + 优雅降级"，不在这里抛异常 —— 上层拿到 null / false 后自行提示。
/// </summary>
public static class PlatformServices
{
    /// <summary>当前仅启用 Windows 系统托盘；niri 继续使用 CLI/IPC 常驻入口。</summary>
    public static bool TrayAvailable => OperatingSystem.IsWindows() && WindowsTraySupport.IsAvailable;
    private static readonly Lazy<IScreenCapture> LazyScreenCapture = new(CreateScreenCapture);
    private static readonly Lazy<INotification> LazyNotification = new(CreateNotification);
    private static readonly Lazy<IAutoStart> LazyAutoStart = new(CreateAutoStart);
    private static readonly Lazy<IMouseGesture> LazyMouseGesture = new(CreateMouseGesture);
    private static readonly Lazy<IClipboardWatcher> LazyClipboardWatcher = new(CreateClipboardWatcher);

    // 端点名带用户名：同一台机器上多用户各自一个实例（Linux 那边的 socket 本来就在用户私有的 runtime 目录里）
    private static readonly Lazy<ISingleInstanceChannel> LazySingleInstance =
        new(() => new SingleInstanceChannel(PlatformPaths.InstanceName));

    /// <summary>
    /// 内核级单实例锁（比管道可靠：抢不到就没有降级余地）。
    /// 名字与管道一致，同一用户同一会话只允许一个 kscan。
    /// </summary>
    public static SingleInstanceLock InstanceLock => LazyInstanceLock.Value;

    private static readonly Lazy<SingleInstanceLock> LazyInstanceLock =
        new(() => new SingleInstanceLock(PlatformPaths.InstanceName));

    // Windows 靠窗口样式 WS_EX_TRANSPARENT；其它平台没有穿透能力（Wayland 要靠 layer-shell 的 set_input_region）
    private static readonly Lazy<IClickThrough> LazyClickThrough =
        new(() => OperatingSystem.IsWindows() ? new WindowsClickThrough() : new UnsupportedClickThrough());

    // Windows 有 RegisterHotKey；Wayland 没有等价能力（portal 未实现），改用 niri binds
    private static readonly Lazy<IGlobalHotkey> LazyGlobalHotkey =
        new(() => OperatingSystem.IsWindows() ? new WindowsGlobalHotkey() : new UnsupportedGlobalHotkey());

    // 修饰键"按住了吗"：Windows 用 GetAsyncKeyState（不抢焦点），其它平台不支持 → 功能隐藏
    private static readonly Lazy<IKeyboardState> LazyKeyboardState =
        new(() => OperatingSystem.IsWindows() ? new WindowsKeyboardState() : new UnsupportedKeyboardState());

    // 前台应用探测：热键排除列表与"框选排除应用"都要用
    private static readonly Lazy<IForegroundApp> LazyForegroundApp =
        new(() => OperatingSystem.IsWindows() ? new WindowsForegroundApp() : new UnsupportedForegroundApp());

    // 光标图像采集：框选遮罩上"定格光标"用（Windows 走 GetCursorInfo + DrawIconEx）
    private static readonly Lazy<ICursorCapture> LazyCursorCapture =
        new(() => OperatingSystem.IsWindows() ? new WindowsCursorCapture() : new UnsupportedCursorCapture());

    // 整窗离屏渲染：窗口超出屏幕时用 PrintWindow 拿完整窗口（含屏幕外部分）
    private static readonly Lazy<IWindowCapture> LazyWindowCapture =
        new(() => OperatingSystem.IsWindows() ? new WindowsWindowCapture() : new UnsupportedWindowCapture());

    private static readonly Lazy<IWindowPlacement> LazyWindowPlacement =
        new(() => OperatingSystem.IsWindows() ? new WindowsWindowPlacement() : new UnsupportedWindowPlacement());

    // 按需提权：只有 Windows 有 UIPI 这层麻烦
    private static readonly Lazy<IElevation> LazyElevation =
        new(() => OperatingSystem.IsWindows() ? new WindowsElevation() : new UnsupportedElevation());

    // "以最高权限开机自启"（Windows 计划任务）
    private static readonly Lazy<IElevatedAutoStart> LazyElevatedAutoStart =
        new(() => OperatingSystem.IsWindows() ? new WindowsElevatedAutoStart() : new UnsupportedElevatedAutoStart());

    private static readonly Lazy<PlatformCapabilities> LazyCapabilities = new(Detect);

    public static IScreenCapture ScreenCapture => LazyScreenCapture.Value;

    public static INotification Notification => LazyNotification.Value;

    public static IAutoStart AutoStart => LazyAutoStart.Value;

    /// <summary>全局鼠标手势。Windows 才有实现；Linux 交给合成器（niri binds）。</summary>
    public static IMouseGesture MouseGesture => LazyMouseGesture.Value;

    /// <summary>剪贴板变更监听。Windows 有实现；Wayland 无变更通知，返回不支持。</summary>
    public static IClipboardWatcher ClipboardWatcher => LazyClipboardWatcher.Value;

    /// <summary>单实例通道（Windows 具名管道 / Linux Unix socket）：把命令交给已运行的实例。</summary>
    public static ISingleInstanceChannel SingleInstance => LazySingleInstance.Value;

    /// <summary>鼠标穿透（贴图窗口用）。Windows 有实现；Wayland 要靠 layer-shell 的 set_input_region。</summary>
    public static IClickThrough ClickThrough => LazyClickThrough.Value;

    /// <summary>全局热键。Windows 有 RegisterHotKey；Wayland 只能靠 niri binds + CLI。</summary>
    public static IGlobalHotkey GlobalHotkey => LazyGlobalHotkey.Value;

    /// <summary>修饰键的物理按下状态（动图定格用；不依赖窗口焦点）。</summary>
    public static IKeyboardState KeyboardState => LazyKeyboardState.Value;

    /// <summary>前台应用探测（热键排除列表、框选排除应用）。Windows 有实现。</summary>
    public static IForegroundApp ForegroundApp => LazyForegroundApp.Value;

    /// <summary>光标图像采集（框选遮罩上的"定格光标"）。Windows 有实现；Wayland 拿不到光标图像。</summary>
    public static ICursorCapture CursorCapture => LazyCursorCapture.Value;

    /// <summary>整窗离屏渲染（窗口超出屏幕时截完整窗口）。Windows 用 PrintWindow。</summary>
    public static IWindowCapture WindowCapture => LazyWindowCapture.Value;

    /// <summary>窗口层级控制（把框选遮罩压到全屏程序上面）。Windows 有实现。</summary>
    public static IWindowPlacement WindowPlacement => LazyWindowPlacement.Value;

    /// <summary>按需提权（解决管理员窗口前台下热键失效）。Windows 有实现。</summary>
    public static IElevation Elevation => LazyElevation.Value;

    /// <summary>"以最高权限开机自启"（Windows 计划任务）。</summary>
    public static IElevatedAutoStart ElevatedAutoStart => LazyElevatedAutoStart.Value;

    public static PlatformCapabilities Capabilities => LazyCapabilities.Value;

    /// <summary>当前会话类型：wayland / x11 / 未知。</summary>
    public static string SessionType =>
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") is { Length: > 0 } value
            ? value.ToLowerInvariant()
            : "unknown";

    public static PlatformCapabilities Detect()
    {
        var screen = ScreenCapture;
        var notification = Notification;
        var autoStart = AutoStart;
        var clipboard = ClipboardWatcher;

        var os = OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsLinux() ? "Linux"
            : OperatingSystem.IsMacOS() ? "macOS"
            : "未知系统";

        return new PlatformCapabilities(
            OperatingSystem: os,
            SessionType: OperatingSystem.IsLinux() ? SessionType : "n/a",
            ScreenCapture: screen.Description,
            ScreenCaptureSupported: screen.IsSupported,
            Notification: notification.Description,
            NotificationSupported: notification.IsSupported,
            AutoStart: autoStart.Description,
            AutoStartSupported: autoStart.IsSupported,
            ClipboardWatch: clipboard.Description,
            ClipboardWatchSupported: clipboard.IsSupported);
    }

    private static IScreenCapture CreateScreenCapture()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsScreenCapture();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxScreenCapture();
        }

        return new UnsupportedScreenCapture();
    }

    private static INotification CreateNotification()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsNotification();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxNotification();
        }

        return new UnsupportedNotification();
    }

    private static IAutoStart CreateAutoStart()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsAutoStart();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxAutoStart();
        }

        return new UnsupportedAutoStart();
    }

    private static IMouseGesture CreateMouseGesture()
    {
        // Wayland 与 X11 都不给应用全局鼠标钩子，Linux 上这个手势由合成器负责：
        //   niri: binds { Mod+MouseLeft { spawn "kscan" "capture" "--region" "--scan"; } }
        return OperatingSystem.IsWindows() ? new WindowsMouseGesture() : new UnsupportedMouseGesture();
    }

    private static IClipboardWatcher CreateClipboardWatcher()
    {
        // Wayland：核心协议不给普通客户端剪贴板变更通知（只有拿到焦点的客户端才可能看到），
        // 因此 Linux 侧统一降级为"不支持"——需要时用 niri binds 显式触发 CLI 更可靠。
        return OperatingSystem.IsWindows() ? new WindowsClipboardWatcher() : new UnsupportedClipboardWatcher();
    }
}

internal sealed class UnsupportedScreenCapture : IScreenCapture
{
    public bool IsSupported => false;

    public string Description => "当前平台不支持屏幕采集";

    public string? DescriptionKey => "Platform.Capture.Unsupported";

    public Task<ImageFrame?> CaptureDesktopAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<ImageFrame?>(null);

    public Task<ImageFrame?> CaptureRegionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<ImageFrame?>(null);
}

internal sealed class UnsupportedNotification : INotification
{
    public bool IsSupported => false;

    public string Description => "当前平台不支持系统通知";

    public string? DescriptionKey => "Platform.Notification.Unsupported";

    public Task ShowAsync(string title, string body, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal sealed class UnsupportedAutoStart : IAutoStart
{
    public bool IsSupported => false;

    public string Description => "当前平台不支持开机自启";

    public string? DescriptionKey => "Platform.AutoStart.Unsupported";

    public bool IsEnabled() => false;

    public Task<OperationResult> SetEnabledAsync(bool enabled) =>
        Task.FromResult(OperationResult.Fail("当前平台不支持开机自启。"));
}

internal sealed class UnsupportedClipboardWatcher : IClipboardWatcher
{
    public bool IsSupported => false;

    public long? SequenceNumber => null;

    public string Description => OperatingSystem.IsLinux()
        ? "Wayland/X11 不给应用剪贴板变更通知，请用合成器热键触发 CLI"
        : "当前平台不支持剪贴板监听";

    public string? DescriptionKey => OperatingSystem.IsLinux()
        ? "Platform.Clipboard.Linux"
        : "Platform.Clipboard.Unsupported";

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public void Start()
    {
    }

    public void Stop()
    {
    }

    public void MarkHandled()
    {
    }
}

internal sealed class UnsupportedClickThrough : IClickThrough
{
    public bool IsSupported => false;

    public string Description => OperatingSystem.IsLinux()
        ? "Wayland 下要靠 layer-shell 的 set_input_region（Avalonia toplevel 降级路径做不到）"
        : "当前平台不支持鼠标穿透";

    public string? DescriptionKey => OperatingSystem.IsLinux()
        ? "Platform.ClickThrough.Linux"
        : "Platform.ClickThrough.Unsupported";

    public bool TrySetEnabled(IntPtr windowHandle, bool enabled) => false;
}

internal sealed class UnsupportedGlobalHotkey : IGlobalHotkey
{
    public bool IsSupported => false;

    public string Description => OperatingSystem.IsLinux()
        ? "Wayland 没有全局热键能力（niri 未实现 GlobalShortcuts portal），请用 niri 的 binds 绑到 kscan CLI"
        : "当前平台不支持全局热键";

    public string? DescriptionKey => OperatingSystem.IsLinux()
        ? "Platform.Hotkey.Linux"
        : "Platform.Hotkey.Unsupported";

    public IReadOnlyList<GlobalHotkeyBinding> Registered { get; } = [];

    public event EventHandler<GlobalHotkeyPressedEventArgs>? Pressed
    {
        add { }
        remove { }
    }

    public IReadOnlyList<GlobalHotkeyFailure> Apply(IReadOnlyList<GlobalHotkeyBinding> hotkeys) =>
    [
        .. hotkeys.Select(binding => new GlobalHotkeyFailure(binding.ActionId, binding.Hotkey.ToString(), Description)),
    ];

    public void UnregisterAll()
    {
    }
}

internal sealed class UnsupportedMouseGesture : IMouseGesture
{
    public bool IsSupported => false;

    public string Description => OperatingSystem.IsLinux()
        ? "由合成器处理（niri：把 Mod+MouseLeft 绑到 kscan）"
        : "当前平台不支持全局鼠标手势";

    public string CurrentGesture => string.Empty;

    public event EventHandler? Triggered
    {
        add { }
        remove { }
    }

    public bool Start(MouseGestureOptions options) => false;

    public void Stop()
    {
    }
}
