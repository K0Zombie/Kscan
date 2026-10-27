using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Kscan.App.Services;
using Kscan.Core.Configuration;

namespace Kscan.App.Tests;

public sealed class DesktopBackgroundTests
{
    private sealed class FakeTray(bool available = true) : IDesktopTray
    {
        public bool IsAvailable { get; set; } = available;
        public bool Disposed { get; private set; }
        public event EventHandler? ShowRequested;
        public event EventHandler? ExitRequested;
        public void Restore() => ShowRequested?.Invoke(this, EventArgs.Empty);
        public void Quit() => ExitRequested?.Invoke(this, EventArgs.Empty);
        public void Dispose() => Disposed = true;
    }
    private sealed class Session(bool trayAvailable = true) : IDisposable
    {
        public AppSettings Settings = AppSettings.Default;
        public Window Window { get; } = new();
        public FakeTray Tray { get; } = new(trayAvailable);
        public bool Exited { get; private set; }
        private DesktopBackgroundController? _controller;
        public DesktopBackgroundController Controller => _controller ??= new(Window, () => Settings,
            () => { Exited = true; Window.Close(); }, Tray);
        public void Dispose() { Controller.Dispose(); Window.Close(); }
    }

    [AvaloniaFact]
    public void Default_close_hides_and_tray_restores_the_same_window()
    {
        using var session = new Session();
        _ = session.Controller;
        session.Window.Show();
        session.Window.Close();
        Assert.False(session.Exited);
        Assert.False(session.Window.IsVisible);
        session.Tray.Restore();
        Assert.True(session.Window.IsVisible);
    }

    [AvaloniaFact]
    public void Turning_off_close_to_tray_takes_effect_without_restart()
    {
        using var session = new Session();
        _ = session.Controller;
        session.Window.Show();
        session.Window.Close();
        session.Tray.Restore();
        session.Settings = session.Settings with { CloseToTray = false };
        session.Window.Close();
        Assert.True(session.Exited);
    }

    [AvaloniaFact]
    public void Tray_exit_bypasses_close_to_tray_and_is_idempotent()
    {
        using var session = new Session();
        _ = session.Controller;
        session.Window.Show();
        session.Window.Close();
        session.Tray.Quit();
        Assert.True(session.Exited);
        session.Controller.Exit();
        Assert.False(session.Window.IsVisible);
    }

    [AvaloniaFact]
    public void Minimize_switch_hides_and_restores_to_normal_state()
    {
        using var session = new Session();
        _ = session.Controller;
        session.Settings = session.Settings with { MinimizeToTray = true };
        session.Window.Show();
        session.Window.WindowState = WindowState.Minimized;
        Assert.False(session.Window.IsVisible);
        session.Tray.Restore();
        Assert.True(session.Window.IsVisible);
        Assert.Equal(WindowState.Normal, session.Window.WindowState);
    }

    [AvaloniaFact]
    public void Ordinary_minimize_does_not_hide_without_the_switch()
    {
        using var session = new Session();
        _ = session.Controller;
        session.Window.Show();
        session.Window.WindowState = WindowState.Minimized;
        Assert.True(session.Window.IsVisible);
    }

    [AvaloniaFact]
    public void No_tray_means_visible_startup_and_real_close()
    {
        using var session = new Session(false);
        session.Settings = session.Settings with { StartSilently = true, MinimizeToTray = true };
        Assert.False(session.Controller.ShouldStartHidden("activate"));
        session.Window.Show();
        session.Window.WindowState = WindowState.Minimized;
        Assert.True(session.Window.IsVisible);
        session.Window.Close();
        Assert.True(session.Exited);
    }

    [AvaloniaFact]
    public void Silent_startup_does_not_suppress_an_explicit_capture()
    {
        using var session = new Session();
        session.Settings = session.Settings with { StartSilently = true };
        Assert.True(session.Controller.ShouldStartHidden("activate"));
        Assert.False(session.Controller.ShouldStartHidden("capture-region"));
    }

    [AvaloniaFact]
    public void Tray_disappearing_does_not_trap_the_close_button()
    {
        using var session = new Session();
        _ = session.Controller;
        session.Window.Show();
        session.Tray.IsAvailable = false;
        session.Window.Close();
        Assert.True(session.Exited);
    }
}
