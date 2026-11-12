using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kscan.App.Services;
using Kscan.App.ViewModels;
using Kscan.Platform;
using Kscan.Platform.Abstractions;

namespace Kscan.App.Views;

public partial class MainWindow : Window
{
    private (bool Enabled, string Modifiers, string Button)? _gestureConfiguration;

    public MainWindow()
    {
        InitializeComponent();
        WindowMaterial.Attach(this);

        Opened += (_, _) => StartMouseGesture();
        AppSettingsService.Current.Changed += OnGestureSettingsChanged;
        Closed += (_, _) =>
        {
            AppSettingsService.Current.Changed -= OnGestureSettingsChanged;
            PlatformServices.MouseGesture.Triggered -= OnGestureTriggered;
            PlatformServices.MouseGesture.Stop();
        };

        // 语言切换后要重新测量一次：TabItem 的 Header 是字符串，换字典后它的宽度不会自动重算，
        // 英文标题（Settings）会被按中文宽度（设置）裁掉一截。
        I18n.Current.Changed += (_, _) => InvalidateForLanguageChange();
    }

    private void InvalidateForLanguageChange()
    {
        foreach (var control in this.GetVisualDescendants().OfType<Control>())
        {
            control.InvalidateMeasure();
        }

        InvalidateMeasure();
    }

    /// <summary>
    /// 仅按用户设置启用全局鼠标手势，默认不挂钩。
    /// 钩子必须在 UI 线程安装，且回调里绝不能做耗时的事 —— 否则会卡住全局鼠标输入。
    /// </summary>
    internal void StartMouseGesture()
    {
        var gesture = PlatformServices.MouseGesture;
        var settings = AppSettingsService.Current.Settings;
        var configuration = (settings.MouseGestureEnabled, settings.MouseGestureModifiers, settings.MouseGestureButton);
        if (!gesture.IsSupported || _gestureConfiguration == configuration)
        {
            return;
        }

        _gestureConfiguration = configuration;
        gesture.Stop();
        gesture.Triggered -= OnGestureTriggered;
        if (!settings.MouseGestureEnabled) return;
        var modifiers = GlobalHotkeyService.ParseForceModifiers(settings.MouseGestureModifiers);
        if (modifiers == HotkeyModifiers.None) return;
        var options = new MouseGestureOptions
        {
            RequireWinKey = modifiers.HasFlag(HotkeyModifiers.Win),
            RequireCtrl = modifiers.HasFlag(HotkeyModifiers.Control),
            RequireAlt = modifiers.HasFlag(HotkeyModifiers.Alt),
            RequireShift = modifiers.HasFlag(HotkeyModifiers.Shift),
            Button = settings.MouseGestureButton switch
            {
                "Right" => MouseGestureButton.Right,
                "Middle" => MouseGestureButton.Middle,
                _ => MouseGestureButton.Left,
            },
        };
        gesture.Triggered += OnGestureTriggered;

        if (gesture.Start(options))
        {
            Log($"mouse gesture enabled: {gesture.CurrentGesture}");
        }
        else
        {
            gesture.Triggered -= OnGestureTriggered;
            Log("mouse gesture hook failed (SetWindowsHookEx returned 0)");
        }
    }

    private void OnGestureSettingsChanged(object? sender, EventArgs e) => StartMouseGesture();

    /// <summary>
    /// GUI 没有控制台，诊断信息交给 <see cref="GuiLog"/>（写到 %APPDATA%\kscan\gui.log）。
    /// <para>
    /// 别在这里自己 File.AppendAllText：以前那句只捕 IOException，而"文件被锁成只读"抛的是
    /// UnauthorizedAccessException —— 它从 Opened 事件冒出去会把整个进程打死（双击后闪退）。
    /// </para>
    /// </summary>
    private static void Log(string message) => GuiLog.Write(message);

    private void OnGestureTriggered(object? sender, EventArgs e)
    {
        // 钩子回调里只投递任务，真正的截图识别放到 UI 线程的消息循环里做
        Dispatcher.UIThread.Post(() =>
        {
            if (!AppSettingsService.Current.Settings.MouseGestureEnabled) return;
            if (DataContext is not MainViewModel model)
            {
                Log("gesture triggered but DataContext is not MainViewModel");
                return;
            }

            Log("gesture triggered: starting region capture");
            Show();
            Activate();
            _ = model.Scan.CaptureRegionCommand.ExecuteAsync(null);
        });
    }
}
