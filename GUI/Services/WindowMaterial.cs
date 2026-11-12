using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Kscan.App.Services;

internal static class WindowMaterial
{
    internal static string Selected(Kscan.Core.Configuration.AppSettings settings) => settings.WindowMaterial switch
    {
        "Mica" => "Mica",
        "Acrylic" => "Acrylic",
        null when settings.MicaEnabled => "Mica",
        _ => "None",
    };

    internal static IReadOnlyList<WindowTransparencyLevel> Hints(string material, bool micaSupported, bool acrylicSupported) => material switch
    {
        "Mica" when micaSupported => [WindowTransparencyLevel.Mica, WindowTransparencyLevel.None],
        "Acrylic" when acrylicSupported => [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None],
        _ => [WindowTransparencyLevel.None],
    };

    internal static Color SurfaceColor(Color themeBackground, WindowTransparencyLevel actual)
    {
        // The compositor backdrop may be black (Acrylic) or wallpaper-tinted (Mica).
        // Tint it with the current theme so foreground contrast does not depend on the desktop.
        return actual == WindowTransparencyLevel.Mica || actual == WindowTransparencyLevel.AcrylicBlur
            ? Color.FromArgb(224, themeBackground.R, themeBackground.G, themeBackground.B)
            : Color.FromArgb(255, themeBackground.R, themeBackground.G, themeBackground.B);
    }

    public static void Attach(Window window)
    {
        void Background()
        {
            var solid = new SolidColorBrush(Color.Parse(ThemeService.Current.CurrentTheme.Colors.Background));
            window.TransparencyBackgroundFallback = solid;
            window.Background = new SolidColorBrush(SurfaceColor(solid.Color, window.ActualTransparencyLevel));
        }
        void Apply(object? sender, EventArgs e)
        {
            window.TransparencyLevelHint = Hints(Selected(AppSettingsService.Current.Settings),
                OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000),
                OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134));
            Background();
        }
        void Changed(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TopLevel.ActualTransparencyLevelProperty) Background();
        }
        AppSettingsService.Current.Changed += Apply;
        ThemeService.Current.Changed += Apply;
        window.PropertyChanged += Changed;
        window.Closed += (_, _) =>
        {
            AppSettingsService.Current.Changed -= Apply;
            ThemeService.Current.Changed -= Apply;
            window.PropertyChanged -= Changed;
        };
        Apply(null, EventArgs.Empty);
    }
}
