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
        panel.Children.Add(new TextBlock { Text = "文件命名", FontSize = 20 });
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
        var json = new TextBox { AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Height = 230, MaxLength = 32768 };
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        NamingSettings Read()
        {
            templates[oldKind] = template.Text ?? "";
            var value = new NamingSettings { Templates = new(templates) };
            Validate(value); return value;
        }
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var export = new Button { Content = "从编辑器生成 JSON" };
        export.Click += (_, _) => { try { json.Text = JsonSerializer.Serialize(new { templates = Read().Templates }, options); } catch (Exception ex) { status.Text = ex.Message; } };
        var import = new Button { Content = "将 JSON 应用到编辑器" };
        import.Click += (_, _) =>
        {
            try
            {
                var value = JsonSerializer.Deserialize<NamingSettings>(json.Text ?? "", options) ?? throw new FormatException("JSON 不能为空。");
                Validate(value); templates = new(value.Templates ?? new());
                template.Text = templates.GetValueOrDefault(oldKind) ?? NamingSettings.DefaultTemplate;
                status.Text = "已应用到编辑器，点击保存后生效。";
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        panel.Children.Add(new Expander { Header = "高级规则 / JSON", Content = new StackPanel { Spacing = 8, Children =
            { new TextBlock { Text = "格式：{date:yyyy-MM-dd}；处理：{title|default:untitled|slug|truncate:40}；条件：{if:type=gif?animation:recording}", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, json, export, import } } });
        var save = new Button { Content = "保存文件命名设置" };
        save.Click += async (_, _) => { try { var value = Read(); await AppSettingsService.Current.UpdateAsync(s => s with { Naming = (s.Naming ?? new()) with { Templates = value.Templates } }); status.Text = AppSettingsService.Current.LastError ?? "已保存。"; } catch (Exception ex) { status.Text = ex.Message; } };
        panel.Children.Add(save); panel.Children.Add(status); Content = panel;
    }
    internal static void Validate(NamingSettings value)
    {
        if (value.Templates is null || value.Templates.Keys.Any(k => !Kinds.Contains(k))) throw new FormatException("templates 中包含未知类型。");
        foreach (var entry in value.Templates) NameTemplate.Render(entry.Value, new NamingContext(entry.Key, DateTime.Now, 1920, 1080));
    }
}
