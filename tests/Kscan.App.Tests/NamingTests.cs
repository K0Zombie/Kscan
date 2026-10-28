using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Kscan.App.Services;
using Kscan.App.Views;
using Kscan.Core.Configuration;
using Kscan.Core.Imaging;

namespace Kscan.App.Tests;

public sealed class NamingTests
{
    [AvaloniaFact]
    public void Settings_navigation_separates_file_names_stamps_and_general_help()
    {
        var model = new Kscan.App.ViewModels.SettingsViewModel();
        var page = new SettingsPage { DataContext = model };
        var order = page.GetLogicalDescendants().OfType<RadioButton>().Select(x => x.CommandParameter as string).ToArray();
        Assert.Equal(Array.IndexOf(order, "appearance") + 1, Array.IndexOf(order, "naming"));
        Assert.Equal(Array.IndexOf(order, "recording") + 1, Array.IndexOf(order, "recognition"));
        model.SelectSectionCommand.Execute("naming");
        var names = Assert.IsType<NamingSettingsPanel>(page.FindControl<ContentControl>("NamingSectionHost")!.Content);
        Assert.DoesNotContain(names.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, "打开使用手册"));
        Assert.Empty(names.GetLogicalDescendants().OfType<CheckBox>());
        model.SelectSectionCommand.Execute("annotation");
        Assert.IsType<StampSettingsPanel>(page.FindControl<ContentControl>("StampSectionHost")!.Content);
        var help = page.FindControl<Button>("OpenManualButton")!;
        var section = help.GetLogicalAncestors().OfType<Border>().First();
        Assert.False(section.IsVisible);
        model.SelectSectionCommand.Execute("general"); Assert.True(section.IsVisible);
        model.SelectSectionCommand.Execute("recording");
        var recording = Assert.IsType<RecordingSettingsPanel>(page.FindControl<ContentControl>("RecordingSectionHost")!.Content);
        Assert.Single(recording.GetLogicalDescendants().OfType<CheckBox>().Where(c => c.Name == "RecordingStampToggle"));
        Assert.Contains(recording.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, "配置画面标注…"));
    }
    private static readonly NamingContext Sample = new("gif", new DateTime(2026, 10, 28, 11, 30, 25), 640, 480, 7, "Example: window");
    [Theory]
    [InlineData("{type}_{date:yyyyMMdd}_{time:HHmmss}_{counter:000}", "gif_20261028_113025_007")]
    [InlineData("{width}x{height}_{title|slug|lower}", "640x480_example-window")]
    [InlineData("{if:type=gif?animation:recording}", "animation")]
    [InlineData("{title|truncate:7|upper}", "EXAMPLE")]
    [InlineData("{{date}}", "{date}")]
    public void Templates_expand_formats_conditions_and_transforms(string template, string expected) =>
        Assert.Equal(expected, NameTemplate.Render(template, Sample));
    [Fact]
    public void Empty_values_can_have_defaults_and_custom_tokens_are_explicit()
    {
        Assert.Equal("untitled", NameTemplate.Render("{title|default:untitled}", Sample with { Title = "" }));
        Assert.Equal("ABC", NameTemplate.Render("{custom_project|upper}", Sample, resolveToken: (_, _) => "abc"));
        Assert.Equal("2026-10-28 11:30:25", NameTemplate.Render("{date:yyyy-MM-dd} {time:HH:mm:ss}", Sample, false));
    }
    [Theory]
    [InlineData("{unknown}")]
    [InlineData("{date")]
    [InlineData("bad}")]
    [InlineData("{title|execute:cmd}")]
    [InlineData("{if:bad}")]
    public void Invalid_rules_are_rejected(string template) => Assert.Throws<FormatException>(() => NameTemplate.Render(template, Sample));
    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("../bad:name", ".._bad_name")]
    [InlineData("name. ", "name")]
    [InlineData("", "untitled")]
    public void File_names_cannot_escape_the_directory_or_use_reserved_names(string value, string expected) =>
        Assert.Equal(expected, NameTemplate.Sanitize(value));
    [Fact]
    public void Concurrent_same_template_outputs_do_not_reuse_reserved_or_existing_files()
    {
        var directory = Path.Combine(Path.GetTempPath(), "naming-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var leases = new System.Collections.Concurrent.ConcurrentBag<FileNaming.PathLease>();
        try
        {
            Parallel.For(0, 30, _ => leases.Add(FileNaming.ReservePath(directory, ".gif", Sample)));
            Assert.Equal(30, leases.Select(l => l.Path).Distinct().Count());
            var existing = leases.First().Path; File.WriteAllText(existing, "keep");
            foreach (var lease in leases) lease.Dispose();
            using var next = FileNaming.ReservePath(directory, ".gif", Sample);
            Assert.NotEqual(existing, next.Path); Assert.Equal("keep", File.ReadAllText(existing));
        }
        finally { foreach (var lease in leases) lease.Dispose(); Directory.Delete(directory, true); }
    }
    [Fact]
    public void Json_rules_round_trip_and_invalid_configuration_is_rejected()
    {
        var settings = new NamingSettings { Templates = new() { ["gif"] = "{date}_{counter:000}" }, RecordStamp = true };
        var restored = System.Text.Json.JsonSerializer.Deserialize<NamingSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        NamingSettingsPanel.Validate(restored);
        Assert.Equal(settings.Templates["gif"], restored.Templates["gif"]);
        Assert.Throws<FormatException>(() => StampSettingsPanel.Validate(settings with { StampSize = 0 }));
    }
    [AvaloniaFact]
    public void Recording_stamp_uses_fixed_or_live_time_and_renders_without_taking_focus()
    {
        var settings = new NamingSettings { StampTemplate = "{date:yyyy-MM-dd} {time:HH:mm:ss}", LiveStamp = false };
        var later = Sample.Time.AddSeconds(10);
        Assert.EndsWith("11:30:25", RecordingStamp.TextAt(Sample, settings, later));
        Assert.EndsWith("11:30:35", RecordingStamp.TextAt(Sample, settings with { LiveStamp = true }, later));
        var overlay = RecordingStamp.CreateOverlay(new ScreenInfo(new PixelRect(0, 0, 800, 600), 1),
            new PixelRect(10, 10, 640, 480), settings, "2026-10-28 11:30:25");
        try
        {
            Assert.False(overlay.ShowActivated); Assert.False(overlay.Focusable);
            overlay.Show(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var text = Assert.Single(overlay.GetLogicalDescendants().OfType<TextBlock>());
            Assert.Equal("2026-10-28 11:30:25", text.Text);
            using var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(800, 600)); image.Render(overlay);
        }
        finally { overlay.Close(); }
    }
    [AvaloniaFact]
    public void Naming_page_is_lazy_loaded_and_timestamp_is_an_editable_annotation()
    {
        var model = new Kscan.App.ViewModels.SettingsViewModel();
        var page = new SettingsPage { DataContext = model };
        var host = page.FindControl<ContentControl>("NamingSectionHost")!;
        Assert.Null(host.Content); model.SelectSectionCommand.Execute("naming");
        Assert.IsType<NamingSettingsPanel>(host.Content);
        Assert.True(model.SectionNaming);
        var shell = new Window { Content = page, Width = 1100, Height = 800 };
        shell.Show(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        using (var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(1100, 800))) image.Render(shell);
        shell.Close();
        var editor = new AnnotationWindow(new ImageFrame(640, 480, new byte[640 * 480 * 4]));
        try
        {
            editor.InsertTimeStamp();
            var item = Assert.Single(editor.ItemsForTest);
            Assert.Equal(AnnotationTool.Text, item.Tool);
            Assert.Contains(DateTime.Now.Year.ToString(), item.Text);
            editor.DocumentForTest.Undo(); Assert.Empty(editor.ItemsForTest);
        }
        finally { editor.Close(); }
    }
}
