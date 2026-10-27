using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kscan.App.Services;
using Kscan.App.Views;
using Kscan.Core.Configuration;
using Kscan.Core.Imaging;
using SkiaSharp;
using Colors = Avalonia.Media.Colors;

namespace Kscan.App.Tests;

public sealed class AnnotationThemeTests
{
    [AvaloniaFact]
    public void Independent_editor_tracks_live_application_theme_without_recoloring_image()
    {
        var previous = ThemeService.Current.CurrentTheme;
        AnnotationWindow? window = null;
        try
        {
            ThemeService.Current.Apply(ThemeDefinition.Light);
            window = new AnnotationWindow(Frame());
            window.Show(); Pump();
            Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);
            Assert.Equal(Color.Parse("#FAFAFA"), ColorOf(window.Background));
            var canvas = (AnnotationCanvas)window.CanvasForTest;
            Assert.Equal(Color.Parse("#FAFAFA"), ColorOf(canvas.BackgroundBrush));
            window.DocumentForTest.Add(new AnnotationItem { Tool = AnnotationTool.Rect, Points = [new SKPoint(20, 20), new SKPoint(100, 90)] });
            var before = canvas.Export().Pixels;
            SavePreview(window, "light");

            ThemeService.Current.Apply(ThemeDefinition.Dark with
            {
                FontFamily = "Courier New", CornerRadius = 12,
                Colors = ThemeDefinition.Dark.Colors with { Background = "#18252F", Foreground = "#F1E9D2", Accent = "#FFD060", Border = "#8D9770", Selection = "#40558866" },
            });
            Pump();
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            Assert.Equal(Color.Parse("#18252F"), ColorOf(window.Background));
            Assert.Equal(Color.Parse("#18252F"), ColorOf(canvas.BackgroundBrush));
            Assert.Equal(Color.Parse("#FFD060"), ColorOf(canvas.AccentBrush));
            Assert.Equal("Courier New", window.FontFamily.Name);
            var done = window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "完成并复制"));
            Assert.Equal(Color.Parse("#FFD060"), ColorOf(done.Background));
            Assert.Equal(Colors.Black, ColorOf(done.Foreground));
            Assert.Equal(12, done.CornerRadius.TopLeft);
            var icon = window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().First();
            Assert.Equal(Color.Parse("#F1E9D2"), ColorOf(icon.Stroke));
            Assert.Equal(before, canvas.Export().Pixels);
            SavePreview(window, "custom");
        }
        finally { window?.Close(); ThemeService.Current.Apply(previous); }
    }

    [AvaloniaFact]
    public void Quick_toolbar_and_mask_use_the_application_theme_and_update_while_open()
    {
        var previous = ThemeService.Current.CurrentTheme;
        AnnotationWindow? window = null;
        try
        {
            ThemeService.Current.Apply(ThemeDefinition.Light);
            var layout = QuickAnnotationLayout.TryCreate(new PixelRect(400, 250, 400, 260),
                new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1)!;
            window = new AnnotationWindow(Frame(), quickLayout: layout);
            window.Show(); Pump();
            var toolbar = window.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "QuickAnnotationToolbar");
            var overlay = Assert.IsType<Canvas>(window.Content);
            Assert.Equal(Color.Parse("#FAFAFA"), ColorOf(toolbar.Background));
            Assert.Equal(Colors.Transparent, ColorOf(overlay.Background));
            ThemeService.Current.Apply(ThemeDefinition.Dark with { Mask = new ThemeMask { Style = ThemeMaskStyles.Dim, Color = "#80402010" } });
            Pump();
            Assert.Equal(Color.Parse(ThemeDefinition.Dark.Colors.Background), ColorOf(toolbar.Background));
            Assert.Equal(Color.Parse("#80402010"), ColorOf(overlay.Background));
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
        }
        finally { window?.Close(); ThemeService.Current.Apply(previous); }
    }

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
    private static ImageFrame Frame()
    {
        var pixels = new byte[400 * 260 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 180; pixels[i + 1] = 110; pixels[i + 2] = 60; pixels[i + 3] = 255; }
        return new ImageFrame(400, 260, pixels);
    }
    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
    }
    private static void SavePreview(Window window, string theme)
    {
        if (Environment.GetEnvironmentVariable("KSCAN_ANNOTATION_PREVIEW") is not { Length: > 0 } path) return;
        using var frame = window.CaptureRenderedFrame();
        frame!.Save(Path.ChangeExtension(path, null) + "-theme-" + theme + ".png");
    }
}
