using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Kscan.App.Services;
using Kscan.Core.Configuration;

namespace Kscan.App.Views;

public sealed class NamingSettingsPanel : UserControl
{
    internal static readonly string[] Kinds = ["screenshot", "scroll", "annotation", "pin", "barcode", "clipboard", "video", "gif", "audio"];
    public NamingSettingsPanel()
    {
        var settings = AppSettingsService.Current.Settings.Naming ?? new();
        var templates = new Dictionary<string, string>(settings.Templates ?? new());
        var panel = new StackPanel { Spacing = 10, MinWidth = 380, MaxWidth = 700 };
        panel.Children.Add(new TextBlock { Text = "文件命名与时间标注", FontSize = 20 });
        panel.Children.Add(new TextBlock { Text = "仅填写文件名，不含目录和扩展名。文件名会自动处理非法字符及重名。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var kind = new ComboBox { ItemsSource = new[] { "截图", "滚动截图", "标注结果", "贴图另存", "生成二维码 / 条码", "剪贴板文本", "视频", "GIF 动画", "音频" }, SelectedIndex = 0 };
        var template = new TextBox { Name = "NamingTemplate", Text = templates.GetValueOrDefault(Kinds[0]) ?? NamingSettings.DefaultTemplate, MaxLength = 2048 };
        var preview = new TextBlock { Name = "NamingPreview", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        panel.Children.Add(kind); panel.Children.Add(template); panel.Children.Add(preview);
        panel.Children.Add(new TextBlock { Text = "type 类型 · date 日期 · time 时间 · counter 序号 · width / height 像素尺寸 · title 来源名称", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = .7 });
        var variables = new WrapPanel();
        foreach (var token in new[] { "{type}", "{date:yyyyMMdd}", "{time:HHmmss}", "{counter:000}", "{width}", "{height}", "{title|default:untitled|slug}" })
        {
            var button = new Button { Content = token, Margin = new Avalonia.Thickness(0, 0, 6, 6) };
            button.Click += (_, _) => { var at = Math.Clamp(template.CaretIndex, 0, (template.Text ?? "").Length); template.Text = (template.Text ?? "").Insert(at, token); template.CaretIndex = at + token.Length; template.Focus(); };
            variables.Children.Add(button);
        }
        panel.Children.Add(variables);
        var oldKind = Kinds[0];
        void UpdatePreview()
        {
            var extension = oldKind switch { "gif" => ".gif", "audio" => ".m4a", "video" => ".mp4", "clipboard" => ".txt", _ => ".png" };
            try { preview.Text = "示例：" + NameTemplate.Render(template.Text ?? "", new NamingContext(oldKind, DateTime.Now,
                oldKind is "audio" or "clipboard" ? 0 : 1920, oldKind is "audio" or "clipboard" ? 0 : 1080, 1, "Example window", extension.TrimStart('.'))) + extension; }
            catch (Exception ex) { preview.Text = ex.Message; }
        }
        template.TextChanged += (_, _) => UpdatePreview();
        kind.SelectionChanged += (_, _) =>
        {
            templates[oldKind] = template.Text ?? NamingSettings.DefaultTemplate;
            oldKind = Kinds[Math.Max(0, kind.SelectedIndex)];
            template.Text = templates.GetValueOrDefault(oldKind) ?? NamingSettings.DefaultTemplate;
            UpdatePreview();
        };
        UpdatePreview();
        var stamp = new TextBox { Text = settings.StampTemplate, MaxLength = 2048 };
        var enabled = new CheckBox { Content = "录制 GIF / 视频时显示画面标注（会进入成片）", IsChecked = settings.RecordStamp };
        var live = new CheckBox { Content = "时间实时更新（关闭则固定为开始时间）", IsChecked = settings.LiveStamp };
        var positions = new[] { "top-left", "top-right", "bottom-left", "bottom-right" };
        var position = new ComboBox { ItemsSource = new[] { "左上", "右上", "左下", "右下" }, SelectedIndex = Math.Max(0, Array.IndexOf(positions, settings.StampPosition)) };
        var size = new NumericUpDown { Minimum = 8, Maximum = 96, Value = (decimal)(double.IsFinite(settings.StampSize) ? Math.Clamp(settings.StampSize, 8, 96) : 20), Increment = 1 };
        var color = new TextBox { Text = settings.StampColor, PlaceholderText = "#AARRGGBB" };
        var opacity = new NumericUpDown { Minimum = 0.1m, Maximum = 1, Value = (decimal)(double.IsFinite(settings.StampOpacity) ? Math.Clamp(settings.StampOpacity, .1, 1) : .9), Increment = .1m };
        var stampPanel = new StackPanel { Spacing = 8 };
        foreach (var control in new Control[] { new TextBlock { Text = "文字模板；标注编辑器的“插入时间标注”也使用此模板。" }, stamp, enabled, live,
            new TextBlock { Text = "位置（录制）" }, position, new TextBlock { Text = "字号（图片像素）" }, size,
            new TextBlock { Text = "文字颜色 #AARRGGBB" }, color, new TextBlock { Text = "透明度 0.1–1；字体跟随软件主题" }, opacity }) stampPanel.Children.Add(control);
        panel.Children.Add(new Expander { Header = "画面时间标注", Content = stampPanel });
        var json = new TextBox { AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Height = 230, MaxLength = 32768 };
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        NamingSettings Read()
        {
            templates[oldKind] = template.Text ?? "";
            var value = new NamingSettings { Templates = new(templates), StampTemplate = stamp.Text ?? "", RecordStamp = enabled.IsChecked == true,
                LiveStamp = live.IsChecked == true, StampPosition = positions[Math.Max(0, position.SelectedIndex)], StampSize = (double)(size.Value ?? 20),
                StampColor = color.Text ?? "#FFFFFFFF", StampOpacity = (double)(opacity.Value ?? .9m) };
            Validate(value); return value;
        }
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var export = new Button { Content = "从编辑器生成 JSON" };
        export.Click += (_, _) => { try { json.Text = JsonSerializer.Serialize(Read(), options); } catch (Exception ex) { status.Text = ex.Message; } };
        var import = new Button { Content = "将 JSON 应用到编辑器" };
        import.Click += (_, _) =>
        {
            try
            {
                var value = JsonSerializer.Deserialize<NamingSettings>(json.Text ?? "", options) ?? throw new FormatException("JSON 不能为空。");
                Validate(value); templates = new(value.Templates ?? new());
                template.Text = templates.GetValueOrDefault(oldKind) ?? NamingSettings.DefaultTemplate;
                stamp.Text = value.StampTemplate; enabled.IsChecked = value.RecordStamp; live.IsChecked = value.LiveStamp;
                position.SelectedIndex = Array.IndexOf(positions, value.StampPosition); size.Value = (decimal)value.StampSize; color.Text = value.StampColor; opacity.Value = (decimal)value.StampOpacity;
                status.Text = "已应用到编辑器，点击保存后生效。";
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        panel.Children.Add(new Expander { Header = "高级规则 / JSON", Content = new StackPanel { Spacing = 8, Children =
            { new TextBlock { Text = "格式：{date:yyyy-MM-dd}；处理：{title|default:untitled|slug|truncate:40}；条件：{if:type=gif?animation:recording}", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, json, export, import } } });
        var save = new Button { Content = "保存命名与标注设置" };
        save.Click += async (_, _) => { try { var value = Read(); await AppSettingsService.Current.UpdateAsync(s => s with { Naming = value }); status.Text = AppSettingsService.Current.LastError ?? "已保存。"; } catch (Exception ex) { status.Text = ex.Message; } };
        var help = new Button { Content = "打开使用手册" };
        help.Click += async (_, _) => { var result = await ResultActions.OpenAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "帮助文档", "index.html")); if (!result.Success) status.Text = result.Error; };
        panel.Children.Add(save); panel.Children.Add(help); panel.Children.Add(status); Content = panel;
    }
    internal static void Validate(NamingSettings value)
    {
        if (value.Templates is null || value.Templates.Keys.Any(k => !Kinds.Contains(k))) throw new FormatException("templates 中包含未知类型。");
        foreach (var entry in value.Templates) NameTemplate.Render(entry.Value, new NamingContext(entry.Key, DateTime.Now, 1920, 1080));
        NameTemplate.Render(value.StampTemplate, new NamingContext("video", DateTime.Now), false);
        if (!new[] { "top-left", "top-right", "bottom-left", "bottom-right" }.Contains(value.StampPosition)
            || value.StampSize is < 8 or > 96 || value.StampOpacity is < .1 or > 1) throw new FormatException("标注位置、字号或透明度无效。");
        _ = Avalonia.Media.Color.Parse(value.StampColor);
    }
}
