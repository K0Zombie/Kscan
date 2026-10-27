using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Kscan.Core.Configuration;
using Kscan.Platform;

namespace Kscan.App.Services;

internal interface IDesktopTray : IDisposable
{
    bool IsAvailable { get; }

    /// <summary>托盘图标是否真的挂上了（用于"隐藏后自检"，防止变成隐形进程）。</summary>
    bool IsIconAttached => IsAvailable;

    event EventHandler? ShowRequested;
    event EventHandler? ExitRequested;
}

internal sealed class DesktopTray : IDesktopTray
{
    private readonly Application _application;
    private readonly TrayIcon? _icon;
    private readonly NativeMenuItem _show = new();
    private readonly NativeMenuItem _exit = new();

    public DesktopTray(Application application)
    {
        _application = application;
        if (!PlatformServices.TrayAvailable)
        {
            GuiLog.Write("tray: platform reports no tray support");
            return;
        }

        using var stream = AssetLoader.Open(new Uri("avares://kscan-gui/Assets/kscan.ico"));

        // 保持 0.1.10 验证过的顺序：**创建时就是可见的**，然后再挂到 Application。
        // （曾改成"先不可见、挂上再置可见"，结果 0.1.11 里图标直接不显示 —— 不能动这个顺序。）
        _icon = new TrayIcon { Icon = new WindowIcon(stream), ToolTipText = "Kscan", IsVisible = true };

        var menu = new NativeMenu();
        menu.Items.Add(_show);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_exit);
        _icon.Menu = menu;
        _show.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        _exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        _icon.Clicked += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        TrayIcon.SetIcons(application, new TrayIcons { _icon });

        UpdateLanguage();
        I18n.Current.Changed += OnLanguageChanged;

        GuiLog.Write(
            $"tray: created (platformAvailable={PlatformServices.TrayAvailable}, exporter={_icon.NativeMenuExporter is not null}, visible={_icon.IsVisible})");
    }

    public bool IsAvailable => PlatformServices.TrayAvailable && _icon?.NativeMenuExporter is not null;

    /// <summary>
    /// 图标是否真的挂在系统托盘上。Avalonia 的 <see cref="TrayIcon"/> 挂上之后才有原生导出器；
    /// 只有它非空、且 <c>IsVisible</c> 为真，才认为"用户点得到它"。
    /// </summary>
    public bool IsIconAttached => _icon is { IsVisible: true } && _icon.NativeMenuExporter is not null;
    public event EventHandler? ShowRequested;
    public event EventHandler? ExitRequested;
    private void OnLanguageChanged(object? sender, EventArgs e) => UpdateLanguage();
    private void UpdateLanguage()
    {
        _show.Header = I18n.T("Str.Tray.Show");
        _exit.Header = I18n.T("Str.Tray.Exit");
    }
    public void Dispose()
    {
        I18n.Current.Changed -= OnLanguageChanged;
        _icon?.Dispose();
        if (_icon is not null) TrayIcon.SetIcons(_application, null);
    }
}

/// <summary>隐藏不关闭窗口；只有明确退出或真正关闭窗口时结束整个应用。</summary>
internal sealed class DesktopBackgroundController : IDisposable
{
    private readonly Window _window;
    private readonly Func<AppSettings> _settings;
    private readonly Action _exit;
    private readonly IDesktopTray _tray;
    private bool _exiting;

    public DesktopBackgroundController(Window window, Func<AppSettings> settings, Action exit, IDesktopTray tray)
    {
        _window = window;
        _settings = settings;
        _exit = exit;
        _tray = tray;
        window.Closing += OnClosing;
        window.Closed += OnClosed;
        window.PropertyChanged += OnWindowChanged;
        tray.ShowRequested += OnShowRequested;
        tray.ExitRequested += OnExitRequested;
    }

    /// <summary>
    /// 能不能"隐藏到托盘"。判据是<b>图标真的挂上了</b>，而不只是"平台声称支持托盘"：
    /// 否则会出现"窗口藏了、图标没有、进程找不到"的隐形状态。
    /// </summary>
    public bool CanHide => _tray.IsIconAttached;
    public bool ShouldStartHidden(string initialCommand) => _settings().StartSilently && CanHide && initialCommand == "activate";
    public void Show()
    {
        if (_exiting) return;
        _window.WindowState = WindowState.Normal;
        _window.Show();
        _window.Activate();
    }
    public void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _exit();
    }
    private void OnShowRequested(object? sender, EventArgs e) => Show();
    private void OnExitRequested(object? sender, EventArgs e) => Exit();
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            _exiting = true;
            return;
        }
        if (!_exiting && _settings().CloseToTray && CanHide &&
            e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined)
        {
            e.Cancel = true;
            HideToTray();
        }
    }
    private void OnClosed(object? sender, EventArgs e) => Exit();
    private void OnWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty && _window.WindowState == WindowState.Minimized &&
            !_exiting && _settings().MinimizeToTray && CanHide) HideToTray();
    }

    /// <summary>
    /// 隐藏到托盘（恢复原来的行为），并顺手做一次非压缩的后台回收。
    /// <para>
    /// 托盘图标本身照常创建；如果外壳没显示它，还有两条固定入口可以叫回窗口：
    /// 「唤起主窗口」热键（默认 <c>Ctrl+Alt+F5</c>）与再次启动 kscan（单实例转发 activate）。
    /// </para>
    /// </summary>
    private void HideToTray()
    {
        _window.Hide();

        GuiLog.Write($"tray: window hidden (icon attached={_tray.IsIconAttached})");

        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false),
            Avalonia.Threading.DispatcherPriority.Background);
    }
    public void Dispose()
    {
        _window.Closing -= OnClosing;
        _window.Closed -= OnClosed;
        _window.PropertyChanged -= OnWindowChanged;
        _tray.ShowRequested -= OnShowRequested;
        _tray.ExitRequested -= OnExitRequested;
        _tray.Dispose();
    }
}
