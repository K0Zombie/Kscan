using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kscan.App.Services;
using Kscan.Core.Configuration;
using Kscan.Platform.Abstractions;

namespace Kscan.App.Tests;

public sealed class CommandHotkeyTests
{
    [AvaloniaFact]
    public void Commands_route_to_their_own_rows_and_removed_commands_cannot_fire()
    {
        var first = new HotkeyBinding(HotkeyActionIds.RunCommand, "Ctrl+Alt+F8") { Command = "first" };
        var second = new HotkeyBinding(HotkeyActionIds.RunCommand, "Ctrl+Alt+F9") { Command = "second", Arguments = "a b" };
        var settings = AppSettings.Default with { Hotkeys = new HotkeyBindings([first, second]) };
        var platform = new FakeHotkey();
        var service = new GlobalHotkeyService(platform, () => settings);
        var received = new List<HotkeyBinding>();
        service.CommandTriggered += received.Add;
        service.ApplyFromSettings();
        var oldId = platform.Registered[0].ActionId;
        platform.Press(platform.Registered[1].ActionId);
        platform.Press(oldId);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { second, first }, received);
        platform.Press(oldId); // 已排队但尚未在 UI 线程执行
        settings = settings with { Hotkeys = new HotkeyBindings([second]) };
        service.ApplyFromSettings();
        platform.Press(oldId); // 已移除的旧路由
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, received.Count);
        platform.Press(platform.Registered[0].ActionId);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(second, received.Last());
        service.UnregisterAll();
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
        public void Press(string id) => Pressed?.Invoke(this, new GlobalHotkeyPressedEventArgs(id));
    }
}
