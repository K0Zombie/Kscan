using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kscan.App.Services;
using Kscan.App.Views;
using Kscan.Core.Configuration;
using Kscan.Platform.Abstractions;

namespace Kscan.App.Tests;

public class InputSettingsTests
{
    [Theory]
    [InlineData("#FAFAFA")]
    [InlineData("#18181C")]
    public void Material_surface_keeps_theme_contrast_over_both_black_and_white_backdrops(string background)
    {
        var color = Avalonia.Media.Color.Parse(background);
        foreach (var effect in new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur })
        {
            var surface = WindowMaterial.SurfaceColor(color, effect);
            Assert.InRange(surface.A, 210, 240);
            foreach (var behind in new[] { 0, 255 })
            {
                var composite = surface.R * surface.A / 255.0 + behind * (255 - surface.A) / 255.0;
                if (color.R > 128) Assert.True(composite > 210);
                else Assert.True(composite < 60);
            }
        }
        Assert.Equal((byte)255, WindowMaterial.SurfaceColor(color, WindowTransparencyLevel.None).A);
    }

    [Theory]
    [InlineData("F1", "Ctrl+Alt+F1")]
    [InlineData("Ctrl+F3", "Ctrl+Alt+F3")]
    [InlineData("Ctrl+Q", "Ctrl+Alt+Q")]
    public void Automatic_force_combination_does_not_conflict_with_existing_explicit_binding(string primary, string existing)
    {
        Assert.True(HotkeyDefinition.TryParse(primary, out var key, out _));
        var binding = new GlobalHotkeyBinding("capture", key!, HotkeyModifiers.Control | HotkeyModifiers.Alt);
        Assert.Null(Kscan.Platform.Windows.WindowsGlobalHotkey.ForcedVariant(binding, [existing]));
        Assert.Equal(existing, Kscan.Platform.Windows.WindowsGlobalHotkey.ForcedVariant(binding, [])!.ToString());
    }

    [Fact]
    public void Native_registration_does_not_report_its_own_derived_combination_as_failure()
    {
        if (!OperatingSystem.IsWindows()) return;
        var native = new Kscan.Platform.Windows.WindowsGlobalHotkey();
        try
        {
            var failures = native.Apply([
                new GlobalHotkeyBinding("first", new HotkeyDefinition(HotkeyModifiers.Control | HotkeyModifiers.Shift, "F23"), HotkeyModifiers.Alt),
                new GlobalHotkeyBinding("explicit", new HotkeyDefinition(HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt, "F23")),
            ]);
            Assert.Empty(failures);
            Assert.Equal(2, native.Registered.Count);
            Assert.Equal(new[] { "Ctrl+Shift+F23", "Ctrl+Alt+Shift+F23" }, native.RegisteredKeys);
        }
        finally { native.UnregisterAll(); }
        Assert.Empty(native.RegisteredKeys);
    }

    [Theory]
    [InlineData("None", true, true, "None")]
    [InlineData("Mica", true, true, "Mica")]
    [InlineData("Mica", false, true, "None")]
    [InlineData("Acrylic", false, true, "AcrylicBlur")]
    [InlineData("Acrylic", true, false, "None")]
    public void Material_selection_uses_requested_effect_or_solid_fallback(string material, bool mica, bool acrylic, string expected)
    {
        var first = WindowMaterial.Hints(material, mica, acrylic)[0];
        Assert.Equal(expected == "Mica" ? WindowTransparencyLevel.Mica : expected == "AcrylicBlur"
            ? WindowTransparencyLevel.AcrylicBlur : WindowTransparencyLevel.None, first);
        Assert.Equal(WindowTransparencyLevel.None, WindowMaterial.Hints(material, mica, acrylic)[^1]);
    }

    [Fact]
    public void Old_mica_choice_is_preserved_and_new_explicit_choice_wins()
    {
        Assert.Equal("Mica", WindowMaterial.Selected(new AppSettings { MicaEnabled = true }));
        Assert.Equal("Acrylic", WindowMaterial.Selected(new AppSettings { MicaEnabled = true, WindowMaterial = "Acrylic" }));
        Assert.Equal("None", WindowMaterial.Selected(new AppSettings { MicaEnabled = true, WindowMaterial = "None" }));
    }
    [Fact]
    public void Old_and_new_configuration_do_not_enable_hidden_mouse_gesture()
    {
        Assert.False(new AppSettings().Migrate().MouseGestureEnabled);
        Assert.False(AppSettings.Default.MouseGestureEnabled);
        var configured = new AppSettings { MouseGestureEnabled = true, MouseGestureModifiers = "Alt+Shift", MouseGestureButton = "Right" }.Migrate();
        Assert.True(configured.MouseGestureEnabled);
        Assert.Equal("Right", configured.MouseGestureButton);
        Assert.Equal(HotkeyModifiers.Alt | HotkeyModifiers.Shift, GlobalHotkeyService.ParseForceModifiers(configured.MouseGestureModifiers));
    }

    [AvaloniaFact]
    public async Task Modifier_only_recording_can_be_confirmed()
    {
        var window = new HotkeyCaptureWindow(null, true, null);
        window.Show(); Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, "Alt");
        window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, "Alt");
        var confirm = window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, I18n.T("Str.Settings.HotkeyCaptureConfirm")));
        confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Alt", await window.Result.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [AvaloniaTheory]
    [InlineData(Key.NumPad7, "Ctrl+NumPad7")]
    [InlineData(Key.OemPlus, "Ctrl+OemPlus")]
    [InlineData(Key.MediaPlayPause, "Ctrl+MediaPlayPause")]
    [InlineData(Key.F24, "Ctrl+F24")]
    public async Task Record_system_reported_extended_keys(Key key, string expected)
    {
        var window = new HotkeyCaptureWindow(null, false, null);
        window.Show(); Dispatcher.UIThread.RunJobs();
        window.KeyPress(key, RawInputModifiers.Control, PhysicalKey.None, "");
        window.KeyRelease(key, RawInputModifiers.Control, PhysicalKey.None, "");
        var confirm = window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, I18n.T("Str.Settings.HotkeyCaptureConfirm")));
        confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(expected, await window.Result.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(HotkeyDefinition.TryParse(expected, out _, out _));
    }

    [AvaloniaFact]
    public void Window_material_preserves_opaque_fallback_when_mica_unavailable()
    {
        var window = new Window();
        WindowMaterial.Attach(window);
        window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.NotNull(window.TransparencyBackgroundFallback);
            if (window.ActualTransparencyLevel != WindowTransparencyLevel.Mica && window.ActualTransparencyLevel != WindowTransparencyLevel.AcrylicBlur)
                Assert.NotEqual(Avalonia.Media.Colors.Transparent, ((Avalonia.Media.ISolidColorBrush)window.Background!).Color);
        }
        finally { window.Close(); }
    }
}
