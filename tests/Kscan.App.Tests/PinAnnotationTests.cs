using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Kscan.App.Services;
using Kscan.App.Views;
using Kscan.Core.Imaging;

namespace Kscan.App.Tests;

public class PinAnnotationTests
{
    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Background_stays_transparent_after_opacity_focus_zoom_and_rotation()
    {
        using var bitmap = ResultActions.ToBitmap(new ImageFrame(200, 100, new byte[200 * 100 * 4]));
        var pin = new PinWindow();
        pin.SetImage(bitmap);
        pin.SetPlacement(new PixelRect(100, 100, 200, 100), 1);
        pin.Show(); Pump();
        var other = new Window();
        try
        {
            Assert.Equal(Avalonia.Media.Colors.Transparent, ((ISolidColorBrush)pin.Background!).Color);
            pin.MouseWheel(new Point(80, 50), new Vector(0, -2), RawInputModifiers.Control); Pump();
            other.Show(); other.Activate(); Pump();
            pin.MouseWheel(new Point(80, 50), new Vector(0, 1), RawInputModifiers.None); Pump();
            pin.KeyPress(Key.E, RawInputModifiers.None, PhysicalKey.E, "e"); Pump();
            Assert.True(pin.Opacity < 1);
            Assert.Equal(Avalonia.Media.Colors.Transparent, ((ISolidColorBrush)pin.Background!).Color);
            Assert.Contains(pin.ContextMenu!.Items.OfType<MenuItem>(), x => Equals(x.Header, I18n.T("Str.Pin.Annotate")));
        }
        finally { other.Close(); pin.Close(); Pump(); }
    }

    [AvaloniaFact]
    public void External_resize_updates_zoom_without_stretching_image()
    {
        using var bitmap = ResultActions.ToBitmap(new ImageFrame(200, 100, new byte[200 * 100 * 4]));
        var pin = new PinWindow();
        pin.SetImage(bitmap);
        pin.SetPlacement(new PixelRect(100, 100, 200, 100), 1);
        pin.Show(); Pump();
        try
        {
            Assert.True(pin.CanResize);
            pin.AdoptExternalSize(new Size(600, 200)); Pump();
            Assert.Equal(400, pin.Width, 1);
            Assert.Equal(200, pin.Height, 1);
            Assert.Equal(Avalonia.Media.Colors.Transparent, ((ISolidColorBrush)pin.Background!).Color);
        }
        finally { pin.Close(); Pump(); }
    }

    [Fact]
    public void Quick_annotation_of_scaled_pin_keeps_original_pixel_coordinates()
    {
        var viewport = new AnnotationViewport(1200, 800);
        viewport.FitImageBounds(new Size(600, 400));
        Assert.Equal(.5, viewport.Scale);
        Assert.Equal(new Point(600, 400), viewport.ToView(new SkiaSharp.SKPoint(1200, 800)));
        Assert.Equal(new SkiaSharp.SKPoint(200, 100), viewport.ToImage(new Point(100, 50)));
    }
}
