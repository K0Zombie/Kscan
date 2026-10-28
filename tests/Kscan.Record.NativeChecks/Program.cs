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
using Avalonia.LogicalTree;

if (!OperatingSystem.IsWindows()) return;
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "portable"), "");
AppBuilder.Configure<NativeCheckApp>().UsePlatformDetect()
    .With(new FontManagerOptions { DefaultFamilyName = "sans-serif" }).WithInterFont()
    .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);

sealed class NativeCheckApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        if (Environment.GetCommandLineArgs().Contains("--naming-smoke"))
        {
            var shell = new Window { Content = new ScrollViewer { Content = new NamingSettingsPanel() }, Width = 1050, Height = 800,
                Title = "Kscan naming smoke", ShowActivated = false };
            ThemeService.Current.Apply(ThemeDefinition.Light); shell.Show();
            var smokeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            smokeTimer.Tick += (_, _) =>
            {
                smokeTimer.Stop();
                try
                {
                    foreach (var theme in new[] { ThemeDefinition.Light, ThemeDefinition.Dark })
                    {
                        ThemeService.Current.Apply(theme);
                        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(1050, 800));
                        bitmap.Render(shell); bitmap.Save(Path.Combine(AppContext.BaseDirectory, "naming-smoke-" + theme.Id + ".png"));
                        Console.WriteLine("PASS: naming page rendered " + theme.Id);
                    }
                    shell.Close(); desktop.Shutdown(0);
                }
                catch (Exception ex) { Console.WriteLine(ex); desktop.Shutdown(1); }
            };
            smokeTimer.Start(); return;
        }
        if (Environment.GetCommandLineArgs().Contains("--render-smoke"))
        {
            var screen = new ScreenInfo(new PixelRect(0, 0, 800, 600), 1);
            var overlays = new[] { RecordingKind.Gif, RecordingKind.Video }
                .Select(kind => RecordingRegionSelector.CreateOverlay(screen, kind)).ToArray();
            foreach (var overlay in overlays)
            {
                overlay.Show();
                overlay.SetSelection(new PixelRect(10, 10, 300, 200), new PixelPoint(780, 580));
            }
            var smoke = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            smoke.Tick += (_, _) =>
            {
                smoke.Stop();
                try
                {
                    foreach (var overlay in overlays)
                    {
                        var label = overlay.GetLogicalDescendants().OfType<TextBlock>().Single();
                        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(800, 600));
                        bitmap.Render(overlay);
                        Console.WriteLine($"PASS: rendered {label.Text}; font={label.FontFamily}; width={label.Bounds.Width}");
                    }
                    foreach (var overlay in overlays) overlay.Close();
                    desktop.Shutdown(0);
                }
                catch (Exception ex) { Console.WriteLine(ex); desktop.Shutdown(1); }
            };
            smoke.Start(); return;
        }
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
