using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Kscan.App.Services;
using Kscan.Core.Configuration;

namespace Kscan.App.Tests;

/// <summary>
/// <see cref="ThemeService"/>：主题最终要落到 Avalonia 资源上（界面全用 DynamicResource 取）。
/// 这些用例盯"改了主题设置界面到底变没变"—— 之前 遮罩色 / 圆角 / 字体 三项正是只写进了
/// <see cref="ThemeDefinition"/>、没有真正生效。
/// </summary>
public sealed class ThemeServiceTests
{
    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "kscan-theme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [AvaloniaFact]
    public void Apply_publishes_every_appearance_field_as_a_resource()
    {
        using var service = new ThemeService(NewDirectory(), watchFiles: false);

        service.Apply(ThemeDefinition.Light with
        {
            CornerRadius = 12,
            PreviewCornerRadius = 2,
            FontFamily = "Courier New",
            Mask = new ThemeMask { Color = "#40FF0000", Style = ThemeMaskStyles.Dim },
        });

        var resources = Application.Current!.Resources;

        var background = Assert.IsType<SolidColorBrush>(resources["KscanBackgroundBrush"]);
        Assert.Equal(Color.Parse("#FAFAFA"), background.Color);

        Assert.Equal(12d, Assert.IsType<CornerRadius>(resources["KscanCornerRadius"]).TopLeft);
        Assert.Equal(2d, Assert.IsType<CornerRadius>(resources["KscanPreviewCornerRadius"]).TopLeft);

        Assert.Equal(Color.Parse("#40FF0000"), Assert.IsType<SolidColorBrush>(resources["KscanMaskBrush"]).Color);
        Assert.Equal(true, resources["KscanMaskEnabled"]);

        Assert.Equal("Courier New", Assert.IsType<FontFamily>(resources["KscanFontFamily"]).Name);
    }

    /// <summary>
    /// 没有自定义字体时**不能**显式设成 <c>FontFamily.Default</c>（<c>$Default</c>）：
    /// 那会覆盖整棵可视树的字体，而它在 Avalonia 12 下建立不了 glyphTypeface，界面直接崩
    /// （实测：<c>Could not create glyphTypeface. Font family: $Default</c>）。
    /// </summary>
    [AvaloniaFact]
    public void Empty_font_family_leaves_the_font_resource_unset()
    {
        using var service = new ThemeService(NewDirectory(), watchFiles: false);
        var resources = Application.Current!.Resources;

        service.Apply(ThemeDefinition.Dark with { FontFamily = "Courier New" });
        Assert.True(resources.ContainsKey("KscanFontFamily"));

        service.Apply(ThemeDefinition.Dark with { FontFamily = null });
        Assert.False(resources.ContainsKey("KscanFontFamily"));

        service.Apply(ThemeDefinition.Dark with { FontFamily = "   " });
        Assert.False(resources.ContainsKey("KscanFontFamily"));
    }

    [AvaloniaFact]
    public void Mask_style_none_turns_the_capture_mask_off()
    {
        using var service = new ThemeService(NewDirectory(), watchFiles: false);

        service.Apply(ThemeDefinition.Dark with
        {
            Mask = new ThemeMask { Color = "#8000FF00", Style = ThemeMaskStyles.Dim },
        });

        Assert.True(service.MaskVisible);
        Assert.Equal(Color.Parse("#8000FF00"), Assert.IsType<SolidColorBrush>(service.MaskBrush).Color);
        Assert.Equal(true, Application.Current!.Resources["KscanMaskEnabled"]);

        service.Apply(ThemeDefinition.Dark with { Mask = new ThemeMask { Style = ThemeMaskStyles.None } });

        Assert.False(service.MaskVisible);
        Assert.Equal(false, Application.Current!.Resources["KscanMaskEnabled"]);
    }

    /// <summary>内置主题必须一直在列表里（用户删光主题文件也不能没有主题可选）。</summary>
    [AvaloniaFact]
    public void Reload_keeps_built_in_themes()
    {
        using var service = new ThemeService(NewDirectory(), watchFiles: false);

        service.Reload();

        Assert.Equal(["dark", "light"], service.Available.Select(theme => theme.Id));
    }

    /// <summary>
    /// 用户主题的三种情况：正常文件要读进来、坏文件跳过、<c>system</c> 是保留 id；
    /// 同 id 的文件覆盖内置主题（"想改深色就放一份 dark.json"）。
    /// </summary>
    [AvaloniaFact]
    public void Reload_reads_user_themes_and_lets_them_override_built_ins()
    {
        var directory = NewDirectory();
        File.WriteAllText(
            Path.Combine(directory, "dark.json"),
            """{ "name": "我的深色", "colors": { "background": "#101010" } }""");
        File.WriteAllText(Path.Combine(directory, "extra.json"), """{ "id": "extra", "name": "Extra" }""");
        File.WriteAllText(Path.Combine(directory, "broken.json"), "{ 这不是 json");
        File.WriteAllText(Path.Combine(directory, "system.json"), """{ "id": "system", "name": "nope" }""");

        using var service = new ThemeService(directory, watchFiles: false);
        var reloadEvents = 0;
        service.ThemesChanged += (_, _) => reloadEvents++;
        service.Reload();

        var ids = service.Available.Select(theme => theme.Id).ToList();

        Assert.Equal(1, ids.Count(id => id == "dark"));            // 同名只留一份
        Assert.Contains("light", ids);
        Assert.Contains("extra", ids);
        Assert.DoesNotContain("system", ids);
        Assert.DoesNotContain("broken", ids);
        Assert.Equal(1, reloadEvents);

        var dark = service.Available.Single(theme => theme.Id == "dark");
        Assert.Equal("我的深色", dark.Name);
        Assert.Equal("#101010", dark.Colors.Background);
    }

    /// <summary>坏圆角不能传成 <c>CornerRadius(-5)</c>。</summary>
    [AvaloniaFact]
    public void Invalid_radii_from_json_are_clamped_before_reaching_the_ui()
    {
        var directory = NewDirectory();
        File.WriteAllText(
            Path.Combine(directory, "odd.json"),
            """{ "id": "odd", "cornerRadius": -8, "previewCornerRadius": 999 }""");

        using var service = new ThemeService(directory, watchFiles: false);
        service.Reload();
        service.Apply(service.Available.Single(theme => theme.Id == "odd"));

        var resources = Application.Current!.Resources;
        Assert.Equal(0d, Assert.IsType<CornerRadius>(resources["KscanCornerRadius"]).TopLeft);
        Assert.Equal(
            ThemeDefinition.MaxCornerRadius,
            Assert.IsType<CornerRadius>(resources["KscanPreviewCornerRadius"]).TopLeft);
    }

    /// <summary>导出的是"收敛过"的主题：手改坏的圆角不会原样传给下一个人。</summary>
    [Fact]
    public void Export_writes_a_normalized_theme_file()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "out.json");
        using var service = new ThemeService(directory, watchFiles: false);

        service.Export(ThemeDefinition.Dark with { Id = "out", CornerRadius = -3 }, path);

        var parsed = ThemeDefinition.TryParse(File.ReadAllText(path));
        Assert.NotNull(parsed);
        Assert.Equal("out", parsed!.Id);
        Assert.Equal(0, parsed.CornerRadius);
    }

    /// <summary>主题文件夹里必须有一份说明（任务书 §2「写一个 md 放在主题文件夹，指导用户分享」）。</summary>
    [Fact]
    public void Themes_folder_gets_a_share_guide()
    {
        var directory = NewDirectory();
        using var service = new ThemeService(directory, watchFiles: false);

        service.EnsureThemesFolder();

        var readme = Path.Combine(directory, "README.md");
        Assert.True(File.Exists(readme));

        var text = File.ReadAllText(readme);
        Assert.Contains("kscan:themes-readme", text);
        Assert.Contains("mask.style", text);   // 新版说明必须讲遮罩样式
        Assert.Contains("分享", text);
    }

    /// <summary>旧版说明（本程序以前生成的）要升级成新版；用户自己写的文档不能被覆盖。</summary>
    [Fact]
    public void Old_stock_guide_is_upgraded_but_user_notes_are_kept()
    {
        var directory = NewDirectory();
        var readme = Path.Combine(directory, "README.md");
        using var service = new ThemeService(directory, watchFiles: false);

        File.WriteAllText(readme, "# Kscan 主题分享说明\n\n旧版内容，没有版本标记。\n");
        service.EnsureThemesFolder();
        Assert.Contains("kscan:themes-readme", File.ReadAllText(readme));

        File.WriteAllText(readme, "# 我自己的备忘\n\n别删这个文件。\n");
        service.EnsureThemesFolder();
        Assert.Contains("别删这个文件", File.ReadAllText(readme));
    }
}
