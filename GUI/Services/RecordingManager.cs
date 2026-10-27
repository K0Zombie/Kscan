using Avalonia;
using Avalonia.Platform.Storage;
using Kscan.App.Views;
using Kscan.Core.Configuration;
using Kscan.Platform;

namespace Kscan.App.Services;

internal static class RecordingManager
{
    private static RecordingWindow? _window;
    private static bool _selecting;
    private static readonly List<RecordingControlWindow> Sessions = [];
    public static bool IsRecording => Sessions.Count > 0;
    public static void ShowSettings()
    {
        var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        var main = desktop?.Windows.OfType<MainWindow>().FirstOrDefault();
        if (main?.DataContext is ViewModels.MainViewModel model)
        {
            model.Settings.ActiveSection = "recording";
            model.ActiveTabIndex = 2;
            main.Show(); main.Activate();
        }
    }
    public static void Show()
    {
        if (_window is null) { _window = new RecordingWindow(); _window.Closed += (_, _) => _window = null; }
        _window.Show();
        if (IsRecording) RecordingDisplay.Exclude(_window);
        _window.Activate();
    }

    public static async Task ToggleAsync(RecordingKind kind)
    {
        var running = Sessions.LastOrDefault(s => s.Session.Kind == kind);
        if (running is not null) { await running.StopAsync(); return; }
        try { await StartAsync(kind); }
        catch (Exception ex) { await PinDialog.ChooseAsync(TopLevelAccessor.AnyWindow, "录制", ex.Message, "确定"); }
    }

    public static async Task StartAsync(RecordingKind kind)
    {
        if (_selecting) return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("当前录制功能仅支持 Windows。");
        _selecting = true;
        try
        {
            var settings = AppSettingsService.Current.Settings.Recording ?? new();
            var screens = TopLevelAccessor.GetScreens() ?? throw new InvalidOperationException("无法读取显示器信息。");
            string? directory = kind switch
            { RecordingKind.Video => settings.VideoDirectory, RecordingKind.Gif => settings.GifDirectory, _ => settings.AudioDirectory };
            if (settings.UseApplicationDirectory)
                directory = Path.Combine(AppContext.BaseDirectory, "recordings", kind.ToString().ToLowerInvariant());
            if (string.IsNullOrWhiteSpace(directory))
            {
                var picker = TopLevelAccessor.AnyWindow?.StorageProvider ?? throw new InvalidOperationException("无法打开目录选择器。");
                var folders = await picker.OpenFolderPickerAsync(new() { Title = $"选择{kind}录制保存目录", AllowMultiple = false });
                if (folders.Count == 0) return;
                directory = folders[0].TryGetLocalPath();
                if (directory is null) return;
                var selectedDirectory = directory;
                await AppSettingsService.Current.UpdateAsync(s => s with { Recording = kind switch
                {
                    RecordingKind.Video => settings with { VideoDirectory = selectedDirectory },
                    RecordingKind.Gif => settings with { GifDirectory = selectedDirectory },
                    _ => settings with { AudioDirectory = selectedDirectory },
                }});
            }
            PixelRect? region = null;
            if (kind != RecordingKind.Audio)
            {
                _window?.Hide();
                await Task.Delay(150); // Let the settings window disappear before live window detection starts.
                var all = screens.All.Select(s => new ScreenInfo(s.Bounds, s.Scaling)).ToArray();
                region = await RecordingRegionSelector.SelectAsync(all);
                if (region is null) { _window?.Show(); return; }
                if (region.Value.Width < 16 || region.Value.Height < 16) throw new ArgumentException("录制区域至少为16×16像素。");
            }
            var fps = kind == RecordingKind.Gif ? settings.GifFps : settings.VideoFps;
            if (fps == 0)
            {
                var screen = region is { } r
                    ? screens.All.OrderByDescending(s => (long)s.Bounds.Intersect(r).Width * s.Bounds.Intersect(r).Height).First()
                    : screens.Primary;
                fps = RecordingDisplay.RefreshRate(screen?.DisplayName);
            }
            var session = await RecordingSession.StartAsync(kind, region, fps, settings, directory);
            var control = new RecordingControlWindow(session);
            Sessions.Add(control);
            control.Closed += (_, _) => Sessions.Remove(control);
            try { control.Show(); control.Initialize(); }
            catch { await control.StopAsync(); Sessions.Remove(control); throw; }
        }
        finally { _selecting = false; }
    }

    public static async Task StopAllAsync()
    {
        await Task.WhenAll(Sessions.ToArray().Select(w => w.StopAsync()));
    }
    public static void FinalizeOnExit()
    {
        try { Task.WhenAll(Sessions.ToArray().Select(w => w.Session.StopAsync())).GetAwaiter().GetResult(); }
        catch (Exception ex) { GuiLog.Write($"recording: exit finalization failed {ex}"); }
    }
}
