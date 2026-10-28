using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Kscan.App.Services;
using Kscan.Core.Configuration;
using NAudio.CoreAudioApi;

namespace Kscan.App.Views;

public sealed class RecordingSettingsPanel : UserControl
{
    private sealed record Device(string? Id, string Name) { public override string ToString() => Name; }
    public RecordingSettingsPanel()
    {
        var settings = AppSettingsService.Current.Settings.Recording ?? new();
        var panel = new StackPanel { Spacing = 12, MinWidth = 380, MaxWidth = 700 };
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var advancedPanel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "录制", FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        var kind = new ComboBox { ItemsSource = new[] { "视频（MP4）", "GIF 动画", "仅音频（M4A）" }, SelectedIndex = 0, MinWidth = 180 };
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(kind);
        panel.Children.Add(actions);
        TextBox Field(StackPanel parent, string name, string? value)
        {
            parent.Children.Add(new TextBlock { Text = name });
            var input = new TextBox { Text = value ?? "", MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
            parent.Children.Add(input); return input;
        }
        panel.Children.Add(new TextBlock { Text = "帧率" });
        var fpsRow = new WrapPanel();
        var videoFps = new NumericUpDown { Minimum = 0, Maximum = 360, Value = settings.VideoFps, Increment = 1, FormatString = "0", Width = 130 };
        var gifFps = new NumericUpDown { Minimum = 1, Maximum = 60, Value = settings.GifFps, Increment = 1, FormatString = "0", Width = 130, IsVisible = false };
        fpsRow.Children.Add(videoFps); fpsRow.Children.Add(gifFps);
        var fpsHint = new TextBlock { Text = "0 = 跟随显示器刷新率", Margin = new Thickness(10, 6, 0, 0) };
        fpsRow.Children.Add(fpsHint); panel.Children.Add(fpsRow);
        var sourceKeys = new[] { "none", "system", "mic", "mix" };
        var audioLabel = new TextBlock { Text = "音源" }; panel.Children.Add(audioLabel);
        var source = new ComboBox { ItemsSource = new[] { "无声", "系统声音", "麦克风", "系统声音 + 麦克风" }, SelectedIndex = Math.Max(0, Array.IndexOf(sourceKeys, settings.AudioSource)), HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(source);
        var cursor = new CheckBox { Content = "显示鼠标指针", IsChecked = settings.DrawMouse }; panel.Children.Add(cursor);
        var stampToggle = new CheckBox { Name = "RecordingStampToggle", Content = "显示画面标注", IsChecked = AppSettingsService.Current.Settings.Naming?.RecordStamp == true };
        var stampSettings = new Button { Content = "配置画面标注…" };
        stampSettings.Click += (_, _) => RecordingManager.ShowSettingsSection("annotation");
        var refreshingStamp = false;
        stampToggle.IsCheckedChanged += async (_, _) =>
        {
            if (refreshingStamp) return;
            try { await AppSettingsService.Current.UpdateAsync(s => s with { Naming = (s.Naming ?? new()) with { RecordStamp = stampToggle.IsChecked == true } }); }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        EventHandler refreshStamp = (_, _) =>
        {
            refreshingStamp = true;
            try { stampToggle.IsChecked = AppSettingsService.Current.Settings.Naming?.RecordStamp == true; }
            finally { refreshingStamp = false; }
        };
        AttachedToVisualTree += (_, _) => { AppSettingsService.Current.Changed += refreshStamp; refreshStamp(this, EventArgs.Empty); };
        DetachedFromVisualTree += (_, _) => AppSettingsService.Current.Changed -= refreshStamp;
        panel.Children.Add(stampToggle); panel.Children.Add(stampSettings);
        kind.SelectionChanged += (_, _) =>
        {
            videoFps.IsVisible = kind.SelectedIndex == 0;
            gifFps.IsVisible = kind.SelectedIndex == 1;
            fpsRow.IsVisible = kind.SelectedIndex != 2;
            fpsHint.Text = kind.SelectedIndex == 1 ? "建议 10–15 FPS" : "0 = 跟随显示器刷新率";
            source.IsVisible = audioLabel.IsVisible = kind.SelectedIndex != 1;
            cursor.IsVisible = kind.SelectedIndex != 2;
            stampToggle.IsVisible = stampSettings.IsVisible = kind.SelectedIndex != 2;
            if (kind.SelectedIndex == 2 && source.SelectedIndex == 0) source.SelectedIndex = 1;
        };
        panel.Children.Add(new TextBlock { Text = "保存位置" });
        var appDirectory = new CheckBox { Content = "使用应用目录下的 recordings 文件夹", IsChecked = settings.UseApplicationDirectory };
        panel.Children.Add(appDirectory);
        var folderSummary = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = .7 };
        var folderButton = new Button { Content = "选择保存文件夹…" }; panel.Children.Add(folderButton); panel.Children.Add(folderSummary);
        var video = Field(advancedPanel, "视频目录", settings.VideoDirectory);
        var gif = Field(advancedPanel, "GIF 目录", settings.GifDirectory);
        var audio = Field(advancedPanel, "音频目录", settings.AudioDirectory);
        void UpdateFolderSummary()
        {
            folderSummary.Text = appDirectory.IsChecked == true ? "按 video / gif / audio 自动分类保存" :
                string.IsNullOrWhiteSpace(video.Text) && string.IsNullOrWhiteSpace(gif.Text) && string.IsNullOrWhiteSpace(audio.Text) ? "首次录制时选择目录" : "已指定保存目录，可在高级设置中分别调整";
            folderButton.IsEnabled = appDirectory.IsChecked != true;
        }
        appDirectory.IsCheckedChanged += (_, _) => UpdateFolderSummary();
        UpdateFolderSummary();
        folderButton.Click += async (_, _) =>
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null) return;
            var result = await storage.OpenFolderPickerAsync(new() { Title = "选择录制保存文件夹", AllowMultiple = false });
            if (result.FirstOrDefault()?.TryGetLocalPath() is { } root)
            {
                video.Text = Path.Combine(root, "video"); gif.Text = Path.Combine(root, "gif"); audio.Text = Path.Combine(root, "audio");
                folderSummary.Text = root + "（按类型自动分类）";
            }
        };
        var ffmpeg = Field(advancedPanel, "FFmpeg 路径（默认从 PATH 查找）", settings.FFmpegPath);
        ComboBox DeviceBox(string label, DataFlow flow, string? id)
        {
            advancedPanel.Children.Add(new TextBlock { Text = label });
            var devices = new List<Device> { new(null, "跟随默认设备") };
            try { devices.AddRange(RecordingAudio.Devices(flow).Select(d => new Device(d.Id, d.Name))); }
            catch (Exception ex) { status.Text = "读取音源失败：" + ex.Message; }
            var box = new ComboBox { ItemsSource = devices, SelectedItem = devices.FirstOrDefault(d => d.Id == id) ?? devices[0], HorizontalAlignment = HorizontalAlignment.Stretch };
            advancedPanel.Children.Add(box); return box;
        }
        var microphone = DeviceBox("麦克风设备", DataFlow.Capture, settings.MicrophoneId);
        var output = DeviceBox("系统声音的输出设备", DataFlow.Render, settings.OutputDeviceId);
        advancedPanel.Children.Add(new TextBlock { Text = "视频编码器" });
        var encoders = new[] { "libx264", "h264_nvenc", "h264_qsv", "h264_amf" };
        var encoder = new ComboBox { ItemsSource = encoders, SelectedItem = encoders.Contains(settings.VideoEncoder) ? settings.VideoEncoder : encoders[0] }; advancedPanel.Children.Add(encoder);
        var extra = Field(advancedPanel, "编码参数（全类型共用；仅填写当前编码器支持项）", settings.ExtraOutputArguments);
        panel.Children.Add(new Expander { Header = "高级设置", Content = advancedPanel, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch });
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
                OutputDeviceId = (output.SelectedItem as Device)?.Id, VideoEncoder = encoder.SelectedItem as string ?? "libx264", ExtraOutputArguments = extra.Text ?? "",
            };
            await AppSettingsService.Current.UpdateAsync(s => s with { Recording = next });
            if (AppSettingsService.Current.LastError is { } error) throw new IOException(error);
            status.Text = "设置已保存。";
        }
        Button AddButton(Panel parent, string label, Func<Task> action)
        {
            var button = new Button { Content = label, Margin = new Thickness(8, 0, 0, 0) };
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try { await action(); }
                catch (Exception ex) { status.Text = ex.Message; }
                finally { button.IsEnabled = true; }
            };
            parent.Children.Add(button); return button;
        }
        AddButton(actions, "开始录制", async () => { await SaveAsync(); await RecordingManager.StartAsync((RecordingKind)kind.SelectedIndex); });
        AddButton(actions, "停止全部", RecordingManager.StopAllAsync);
        var save = new StackPanel { Orientation = Orientation.Horizontal };
        AddButton(save, "保存设置", SaveAsync); panel.Children.Add(save); panel.Children.Add(status);
        Content = panel;
    }
}
