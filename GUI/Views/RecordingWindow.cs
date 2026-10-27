using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Platform.Storage;
using Kscan.App.Services;
using Kscan.Core.Configuration;
using NAudio.CoreAudioApi;

namespace Kscan.App.Views;

internal sealed class RecordingWindow : Window
{
    private sealed record Device(string? Id, string Name) { public override string ToString() => Name; }
    public RecordingWindow()
    {
        Title = "框选录制 · GIF / 视频 / 音频"; Width = 650; Height = 740;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Bind(BackgroundProperty, new DynamicResourceExtension("KscanBackgroundBrush"));
        Bind(ForegroundProperty, new DynamicResourceExtension("KscanForegroundBrush"));
        var settings = AppSettingsService.Current.Settings.Recording ?? new();
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 8 };
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        TextBox Field(string name, string? value, string? hint = null)
        {
            panel.Children.Add(new TextBlock { Text = name });
            var input = new TextBox { Text = value ?? "", PlaceholderText = hint };
            panel.Children.Add(input); return input;
        }
        var ffmpeg = Field("FFmpeg 路径（默认读取 PATH，不随软件分发）", settings.FFmpegPath);
        var video = Field("视频保存目录", settings.VideoDirectory, "首次录制时选择");
        var gif = Field("GIF 保存目录", settings.GifDirectory, "首次录制时选择");
        var audio = Field("音频保存目录", settings.AudioDirectory, "首次录制时选择");
        foreach (var (input, name) in new[] { (video, "视频"), (gif, "GIF"), (audio, "音频") })
        {
            var browse = new Button { Content = $"选择{name}目录" };
            browse.Click += async (_, _) =>
            {
                var result = await StorageProvider.OpenFolderPickerAsync(new() { Title = "选择录制目录", AllowMultiple = false });
                if (result.Count > 0) input.Text = result[0].TryGetLocalPath();
            };
            panel.Children.Add(browse);
        }
        var appDirectory = new CheckBox { Content = "使用应用目录 recordings 下的 video / gif / audio 子目录", IsChecked = settings.UseApplicationDirectory };
        var cursor = new CheckBox { Content = "录制鼠标指针", IsChecked = settings.DrawMouse };
        panel.Children.Add(appDirectory); panel.Children.Add(cursor);
        panel.Children.Add(new TextBlock { Text = "视频目标帧率：0 = 跟随选区主要显示器刷新率（实际速度取决于机器）" });
        var videoFps = new NumericUpDown { Minimum = 0, Maximum = 360, Value = settings.VideoFps, Increment = 1, FormatString = "0" };
        panel.Children.Add(videoFps);
        panel.Children.Add(new TextBlock { Text = "GIF 帧率" });
        var gifFps = new NumericUpDown { Minimum = 1, Maximum = 60, Value = settings.GifFps, Increment = 1, FormatString = "0" };
        panel.Children.Add(gifFps);
        var sourceKeys = new[] { "none", "system", "mic", "mix" };
        panel.Children.Add(new TextBlock { Text = "音源（GIF 无音频；单独录音请选择非无声）" });
        var source = new ComboBox { ItemsSource = new[] { "无声", "系统声音", "麦克风", "系统声音 + 麦克风" },
            SelectedIndex = Math.Max(0, Array.IndexOf(sourceKeys, settings.AudioSource)), HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(source);
        ComboBox DeviceBox(string label, DataFlow flow, string? id)
        {
            panel.Children.Add(new TextBlock { Text = label });
            var devices = new List<Device> { new(null, "跟随默认设备") };
            try { devices.AddRange(RecordingAudio.Devices(flow).Select(d => new Device(d.Id, d.Name))); }
            catch (Exception ex) { status.Text = "读取音源失败：" + ex.Message; }
            var box = new ComboBox { ItemsSource = devices, SelectedItem = devices.FirstOrDefault(d => d.Id == id) ?? devices[0], HorizontalAlignment = HorizontalAlignment.Stretch };
            panel.Children.Add(box); return box;
        }
        var microphone = DeviceBox("麦克风设备", DataFlow.Capture, settings.MicrophoneId);
        var output = DeviceBox("系统声音所在的输出设备", DataFlow.Render, settings.OutputDeviceId);
        var encoders = new[] { "libx264", "h264_nvenc", "h264_qsv", "h264_amf" };
        panel.Children.Add(new TextBlock { Text = "视频编码器（硬件编码需显卡支持）" });
        var encoder = new ComboBox { ItemsSource = encoders, SelectedItem = encoders.Contains(settings.VideoEncoder) ? settings.VideoEncoder : encoders[0] };
        panel.Children.Add(encoder);
        var extra = Field("高级输出参数，如 -crf 20 -preset fast", settings.ExtraOutputArguments);
        panel.Children.Add(new TextBlock { Text = "每次开始可框选新区域，支持跨屏和多个独立录制。快捷键可在设置中绑定。\n视频为 MP4，音频为 M4A；边框与控制窗默认排除出录制。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        async Task SaveAsync()
        {
            if (string.IsNullOrWhiteSpace(ffmpeg.Text)) throw new ArgumentException("请指定 FFmpeg 路径或命令名。");
            RecordingSession.ParseArguments(extra.Text ?? "");
            var next = new RecordingSettings
            {
                FFmpegPath = ffmpeg.Text.Trim(), VideoDirectory = video.Text, GifDirectory = gif.Text, AudioDirectory = audio.Text,
                UseApplicationDirectory = appDirectory.IsChecked == true, DrawMouse = cursor.IsChecked == true,
                VideoFps = (int)(videoFps.Value ?? 0), GifFps = (int)(gifFps.Value ?? 15),
                AudioSource = sourceKeys[Math.Max(0, source.SelectedIndex)], MicrophoneId = (microphone.SelectedItem as Device)?.Id,
                OutputDeviceId = (output.SelectedItem as Device)?.Id, VideoEncoder = encoder.SelectedItem as string ?? "libx264",
                ExtraOutputArguments = extra.Text ?? "",
            };
            await AppSettingsService.Current.UpdateAsync(s => s with { Recording = next });
            if (AppSettingsService.Current.LastError is { } error) throw new IOException(error);
            status.Text = "设置已保存。";
        }
        void Button(string text, Func<Task> action)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8) };
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try { await action(); }
                catch (Exception ex) { status.Text = ex.Message; if (!IsVisible) Show(); }
                finally { button.IsEnabled = true; }
            };
            buttons.Children.Add(button);
        }
        Button("保存设置", SaveAsync);
        foreach (var kind in Enum.GetValues<RecordingKind>())
        {
            var name = kind == RecordingKind.Video ? "框选录视频" : kind == RecordingKind.Gif ? "框选录 GIF" : "单独录音";
            Button(name, async () => { await SaveAsync(); await RecordingManager.StartAsync(kind); });
        }
        Button("停止全部", RecordingManager.StopAllAsync);
        panel.Children.Add(buttons); panel.Children.Add(status);
        Content = new ScrollViewer { Content = panel };
    }
}
