using Avalonia.Controls;
using Kscan.App.Services;
using Kscan.Core.Configuration;

namespace Kscan.App.Views;

public sealed class StampSettingsPanel : UserControl
{
    public StampSettingsPanel()
    {
        var settings = AppSettingsService.Current.Settings.Naming ?? new();
        var panel = new StackPanel { Spacing = 10, MaxWidth = 700 };
        panel.Children.Add(new TextBlock { Text = "时间与自定义文字", FontSize = 18 });
        var stamp = new TextBox { Text = settings.StampTemplate, MaxLength = 2048 };
        var enabled = new CheckBox { Content = "录制 GIF / 视频时显示画面标注（会进入成片）", IsChecked = settings.RecordStamp };
        var live = new CheckBox { Content = "时间实时更新（关闭则固定为开始时间）", IsChecked = settings.LiveStamp };
        var positions = new[] { "top-left", "top-right", "bottom-left", "bottom-right" };
        var position = new ComboBox { ItemsSource = new[] { "左上", "右上", "左下", "右下" }, SelectedIndex = Math.Max(0, Array.IndexOf(positions, settings.StampPosition)) };
        var size = new NumericUpDown { Minimum = 8, Maximum = 96, Value = (decimal)(double.IsFinite(settings.StampSize) ? Math.Clamp(settings.StampSize, 8, 96) : 20), Increment = 1 };
        var color = new TextBox { Text = settings.StampColor, PlaceholderText = "#AARRGGBB" };
        var opacity = new NumericUpDown { Minimum = 0.1m, Maximum = 1, Value = (decimal)(double.IsFinite(settings.StampOpacity) ? Math.Clamp(settings.StampOpacity, .1, 1) : .9), Increment = .1m };
        var stampPanel = new StackPanel { Spacing = 8 };
        foreach (var control in new Control[] { new TextBlock { Text = "文字模板；截图编辑器的“插入时间标注”也使用此模板。" }, stamp,
            new TextBlock { Text = "字号（图片像素）" }, size, new TextBlock { Text = "文字颜色 #AARRGGBB" }, color,
            new TextBlock { Text = "透明度 0.1–1；字体跟随软件主题" }, opacity, new Separator(),
            new TextBlock { Text = "录制中的显示", FontSize = 18 }, enabled, live, new TextBlock { Text = "位置（录制）" }, position }) stampPanel.Children.Add(control);
        panel.Children.Add(stampPanel);
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var save = new Button { Content = "保存画面标注设置" };
        save.Click += async (_, _) =>
        {
            try
            {
                var value = settings with { StampTemplate = stamp.Text ?? "", RecordStamp = enabled.IsChecked == true,
                    LiveStamp = live.IsChecked == true, StampPosition = positions[Math.Max(0, position.SelectedIndex)],
                    StampSize = (double)(size.Value ?? 20), StampColor = color.Text ?? "#FFFFFFFF", StampOpacity = (double)(opacity.Value ?? .9m) };
                Validate(value);
                await AppSettingsService.Current.UpdateAsync(s => s with { Naming = (s.Naming ?? new()) with
                {
                    StampTemplate = value.StampTemplate, RecordStamp = value.RecordStamp, LiveStamp = value.LiveStamp,
                    StampPosition = value.StampPosition, StampSize = value.StampSize, StampColor = value.StampColor, StampOpacity = value.StampOpacity
                }});
                status.Text = AppSettingsService.Current.LastError ?? "画面标注设置已保存。";
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        panel.Children.Add(save); panel.Children.Add(status); Content = panel;
        var observedEnabled = settings.RecordStamp;
        EventHandler refreshEnabled = (_, _) =>
        {
            var current = AppSettingsService.Current.Settings.Naming?.RecordStamp == true;
            if (current == observedEnabled) return;
            observedEnabled = current; enabled.IsChecked = current;
        };
        AttachedToVisualTree += (_, _) => { AppSettingsService.Current.Changed += refreshEnabled; refreshEnabled(this, EventArgs.Empty); };
        DetachedFromVisualTree += (_, _) => AppSettingsService.Current.Changed -= refreshEnabled;
    }
    internal static void Validate(NamingSettings value)
    {
        NameTemplate.Render(value.StampTemplate, new NamingContext("video", DateTime.Now), false);
        if (!new[] { "top-left", "top-right", "bottom-left", "bottom-right" }.Contains(value.StampPosition)
            || !double.IsFinite(value.StampSize) || !double.IsFinite(value.StampOpacity)
            || value.StampSize is < 8 or > 96 || value.StampOpacity is < .1 or > 1) throw new FormatException("标注位置、字号或透明度无效。");
        _ = Avalonia.Media.Color.Parse(value.StampColor);
    }
}
