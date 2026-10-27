using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Kscan.App.Views;

File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "portable"), "");
AppBuilder.Configure<NativeCheckApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);

sealed class NativeCheckApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var bitmap = new WriteableBitmap(new PixelSize(400,300), new Vector(96,96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        var pin = new PinWindow();
        pin.SetImage(bitmap, "native-sizing-check.png");
        pin.SetPlacement(new PixelRect(-20000,-20000,400,300));
        pin.Show();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                foreach (var factor in new[] { 8.0, .25, 4.0 })
                {
                    typeof(PinWindow).GetMethod("Zoom", BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(pin, [new Point(0,0), factor]);
                    await Task.Delay(180);
                    var picture = pin.FindControl<Image>("Picture")!;
                    if (Math.Abs(pin.ClientSize.Width-picture.Width)>1 || Math.Abs(pin.ClientSize.Height-picture.Height)>1)
                        throw new Exception($"CLIPPED: requested {picture.Width}x{picture.Height}; client {pin.ClientSize}");
                    Console.WriteLine($"PASS: requested {picture.Width}x{picture.Height}; native client {pin.ClientSize}; DPI={pin.RenderScaling}");
                }
                pin.Close(); bitmap.Dispose(); desktop.Shutdown(0);
            }
            catch(Exception ex) { Console.WriteLine(ex); pin.Close(); bitmap.Dispose(); desktop.Shutdown(1); }
        });
    }
}
