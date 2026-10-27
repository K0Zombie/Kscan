using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Kscan.App.Views;
using Kscan.App.Services;
using Kscan.Core.Configuration;
using Avalonia.Input;
using Kscan.Platform;

if (!OperatingSystem.IsWindows()) return;
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "portable"), "");
AppBuilder.Configure<NativeCheckApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);

sealed class NativeCheckApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var counter = new TextBlock { FontSize = 32, Margin = new Thickness(20), Foreground = Brushes.White };
        var start = new Button { Content = "开始实时框选检查", Margin = new Thickness(20), FontSize = 18 };
        var content = new StackPanel { Children = { counter, start } };
        var window = new Window { Title = "Kscan 录制实时框选检查", Width = 640, Height = 360, Content = content,
            Background = Brushes.DarkBlue, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        desktop.MainWindow = window;
        var frames = 0; var selectedFrames = 0; var selecting = false; var lostFocus = false; var foreground = IntPtr.Zero;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            counter.Text = $"实时帧 {++frames}\n{(selecting ? "正在框选：画面应继续变化" : "准备就绪")}";
            window.Background = new SolidColorBrush(Color.FromRgb((byte)(frames % 100), 40, (byte)(100 + frames % 100)));
            if (selecting) { selectedFrames++; if (GetForegroundWindow() != foreground) lostFocus = true; }
        };
        start.Click += async (_, _) =>
        {
            start.IsEnabled = false; selecting = true; foreground = GetForegroundWindow();
            try
            {
                var screens = window.Screens.All.Select(s => new ScreenInfo(s.Bounds, s.Scaling)).ToArray();
                var result = await RecordingRegionSelector.SelectAsync(screens, RecordingKind.Gif,
                    excludeFromCapture: !Environment.GetCommandLineArgs().Contains("--visible-hints"));
                selecting = false;
                var ok = result is { Width: >= 16, Height: >= 16 } && !lostFocus && selectedFrames >= 4;
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: selection={result}; liveFrames={selectedFrames}; focusLost={lostFocus}");
                if (ok && Environment.GetCommandLineArgs().Contains("--record"))
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var pointer = PlatformServices.CursorCapture.Capture();
                    Console.WriteLine($"CURSOR: physicalSize={pointer?.Image.Width}x{pointer?.Image.Height}; left={pointer?.Left}; top={pointer?.Top}");
                    var session = await RecordingSession.StartAsync(RecordingKind.Gif, result, 15,
                        new RecordingSettings { DrawMouse = true }, Path.Combine(AppContext.BaseDirectory, "proof-recordings"));
                    Console.WriteLine($"READY: milliseconds={watch.ElapsedMilliseconds}");
                    await Task.Delay(2500);
                    await session.StopAsync();
                    Console.WriteLine($"SAVED: {session.Output}");
                    var copied = await ClipboardWriter.CopyFileAsync(session.Output);
                    using var data = await window.Clipboard!.TryGetDataAsync();
                    var files = data is null ? null : await data.TryGetFilesAsync();
                    var containsGif = files?.Any(f => f.Name == Path.GetFileName(session.Output)) == true;
                    Console.WriteLine($"CLIPBOARD: copied={copied}; savedGifPresent={containsGif}");
                    ok &= copied && containsGif;
                }
                timer.Stop(); desktop.Shutdown(ok ? 0 : 1);
            }
            catch (Exception ex) { Console.WriteLine(ex); timer.Stop(); desktop.Shutdown(1); }
        };
        window.Closed += (_, _) => { timer.Stop(); desktop.Shutdown(); };
        window.Show(); timer.Start();
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
