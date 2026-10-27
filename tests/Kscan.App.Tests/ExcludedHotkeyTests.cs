using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kscan.App.Services;
using Kscan.Core.Configuration;
using Kscan.Platform.Abstractions;

namespace Kscan.App.Tests;

/// <summary>
/// 排除应用：普通热键在排除列表里的前台应用下不触发，带强制修饰键的变体照常触发；
/// 热键自身已含全部强制修饰键时也不受排除列表限制。
/// </summary>
public sealed class ExcludedHotkeyTests
{
    [AvaloniaFact]
    public void Ordinary_hotkey_is_suppressed_while_an_excluded_app_has_focus()
    {
        var (service, fake, foreground) = Create(
            new HotkeyBinding(HotkeyActionIds.CaptureRegion, "F2"),
            ignoredApps: ["taskmgr.exe"],
            foregroundApp: new ForegroundAppInfo("taskmgr.exe", null));

        var fired = 0;
        service.Triggered += (_, _) => fired++;
        service.ApplyFromSettings();

        var route = fake.Registered[0].ActionId;

        fake.Press(route);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, fired);                       // 普通键被排除列表拦下

        fake.Press(route, forced: true);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, fired);                       // 按住强制修饰键的变体照常触发

        foreground.Info = new ForegroundAppInfo("explorer.exe", null);
        fake.Press(route);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, fired);                       // 焦点离开排除应用后恢复
    }

    [AvaloniaFact]
    public void Hotkey_that_already_contains_the_force_modifiers_is_never_suppressed()
    {
        var (service, fake, _) = Create(
            new HotkeyBinding(HotkeyActionIds.CaptureRegion, "Ctrl+Alt+F5"),
            ignoredApps: ["taskmgr.exe"],
            foregroundApp: new ForegroundAppInfo("taskmgr.exe", null));

        var fired = 0;
        service.Triggered += (_, _) => fired++;
        service.ApplyFromSettings();

        fake.Press(fake.Registered[0].ActionId);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, fired);
    }

    [AvaloniaFact]
    public void Empty_force_modifiers_leave_the_hotkey_suppressed_without_an_escape_hatch()
    {
        var (service, fake, _) = Create(
            new HotkeyBinding(HotkeyActionIds.CaptureRegion, "F2"),
            ignoredApps: ["taskmgr.exe"],
            foregroundApp: new ForegroundAppInfo("taskmgr.exe", null),
            forceModifiers: null);

        var fired = 0;
        service.Triggered += (_, _) => fired++;
        service.ApplyFromSettings();

        var route = fake.Registered[0].ActionId;

        fake.Press(route);
        fake.Press(route, forced: true);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, fired);
    }

    private static (GlobalHotkeyService Service, FakeHotkey Hotkey, FakeForegroundApp Foreground) Create(
        HotkeyBinding binding,
        IReadOnlyList<string> ignoredApps,
        ForegroundAppInfo foregroundApp,
        string? forceModifiers = "Ctrl+Alt")
    {
        var settings = AppSettings.Default with
        {
            Version = AppSettings.CurrentVersion,
            Hotkeys = new HotkeyBindings([binding]),
            IgnoredApps = new AppList(ignoredApps),
            ForceHotkeyModifiers = forceModifiers,
        };

        var fake = new FakeHotkey();
        var foreground = new FakeForegroundApp { Info = foregroundApp };
        return (new GlobalHotkeyService(fake, () => settings, foreground), fake, foreground);
    }

    private sealed class FakeHotkey : IGlobalHotkey
    {
        public bool IsSupported => true;
        public string Description => "test";
        public string? DescriptionKey => null;
        public event EventHandler<GlobalHotkeyPressedEventArgs>? Pressed;
        public IReadOnlyList<GlobalHotkeyBinding> Registered { get; private set; } = [];

        public IReadOnlyList<GlobalHotkeyFailure> Apply(IReadOnlyList<GlobalHotkeyBinding> hotkeys)
        {
            Registered = hotkeys;
            return [];
        }

        public void UnregisterAll() => Registered = [];

        public void Press(string id, bool forced = false) =>
            Pressed?.Invoke(this, new GlobalHotkeyPressedEventArgs(id, forced));
    }

    private sealed class FakeForegroundApp : IForegroundApp
    {
        public ForegroundAppInfo? Info { get; set; }

        public bool IsSupported => true;

        public ForegroundAppInfo? GetForeground() => Info;

        public ForegroundAppInfo? GetAt(int screenX, int screenY, IntPtr ignoreHandle = default) => Info;

        public ScreenRect? GetRectAt(int screenX, int screenY, IntPtr ignoreHandle = default) => null;

        public IntPtr GetWindowHandleAt(int screenX, int screenY, IntPtr ignoreHandle = default) => IntPtr.Zero;

        public ScreenRect? GetElementRectAt(int screenX, int screenY, IntPtr ignoreHandle = default) => null;

        public ScreenPoint? GetCursorPosition() => null;

        public bool IsWindowCovered(IntPtr handle, IntPtr ignoreHandle = default) => false;
    }
}
