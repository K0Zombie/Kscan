using Kscan.Core.Configuration;

namespace Kscan.Core.Tests;

/// <summary>
/// 主题文件（<c>user/themes/*.json</c>）的解析与收敛规则。
/// 主题是用户会手改的文件，"写错一个字整个界面起不来"是不可接受的 —— 这些用例盯的就是这条线。
/// </summary>
public sealed class ThemeDefinitionTests
{
    [Fact]
    public void Built_in_themes_keep_their_ids_and_base()
    {
        Assert.Equal(["dark", "light"], ThemeDefinition.BuiltIn.Select(theme => theme.Id));
        Assert.Equal("dark", ThemeDefinition.Dark.Base);
        Assert.Equal("light", ThemeDefinition.Light.Base);

        Assert.True(ThemeDefinition.IsBuiltInId("dark"));
        Assert.True(ThemeDefinition.IsBuiltInId("LIGHT"));
        Assert.False(ThemeDefinition.IsBuiltInId("my-dark"));

        Assert.True(ThemeDefinition.IsReservedId(ThemeDefinition.SystemThemeId));
        Assert.True(ThemeDefinition.IsReservedId("dark"));
        Assert.False(ThemeDefinition.IsReservedId("my-dark"));
    }

    /// <summary>只写 <c>{}</c> 也要是一套完整可用的深色主题。</summary>
    [Fact]
    public void Missing_fields_fall_back_to_dark_defaults()
    {
        var theme = ThemeDefinition.TryParse("{}");

        Assert.NotNull(theme);
        Assert.Equal(ThemeDefinition.Dark.Base, theme!.Base);
        Assert.Equal(ThemeDefinition.Dark.Colors.Background, theme.Colors.Background);
        Assert.Equal(ThemeMaskStyles.None, theme.Mask.Style);   // 不遮罩是默认
        Assert.Equal(ThemeDefinition.Dark.CornerRadius, theme.CornerRadius);
        Assert.Null(theme.FontFamily);
        Assert.False(string.IsNullOrWhiteSpace(theme.Id));
    }

    /// <summary>任务书 §2 要求的"所有外观设置"：颜色、圆角、蒙版样式、字体都要能存进主题文件。</summary>
    [Fact]
    public void Parses_mask_style_corner_radii_and_font()
    {
        const string json = """
            {
              "id": "my-dark",
              "name": "我的深色",
              "base": "light",
              "colors": { "background": "#101010", "accent": "#FF8800" },
              "mask": { "color": "#40000000", "style": "none" },
              "cornerRadius": 14,
              "previewCornerRadius": 2,
              "fontFamily": "Microsoft YaHei"
            }
            """;

        var theme = ThemeDefinition.TryParse(json);

        Assert.NotNull(theme);
        Assert.Equal("my-dark", theme!.Id);
        Assert.Equal("我的深色", theme.Name);
        Assert.Equal("light", theme.Base);
        Assert.Equal("#101010", theme.Colors.Background);
        Assert.Equal("#FF8800", theme.Colors.Accent);
        Assert.Equal("#40000000", theme.Mask.Color);
        Assert.Equal(ThemeMaskStyles.None, theme.Mask.Style);
        Assert.Equal(14, theme.CornerRadius);
        Assert.Equal(2, theme.PreviewCornerRadius);
        Assert.Equal("Microsoft YaHei", theme.FontFamily);
    }

    [Theory]
    [InlineData("blur")]
    [InlineData("darken")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_mask_style_falls_back_to_none(string? style)
    {
        var json = $$"""
            { "mask": { "style": {{(style is null ? "null" : $"\"{style}\"")}} } }
            """;

        var theme = ThemeDefinition.TryParse(json);

        Assert.NotNull(theme);
        Assert.Equal(ThemeMaskStyles.None, theme!.Mask.Style);
    }

    /// <summary>手写文件里出现负数 / 超大圆角时不能崩，也不能画出"负圆角"。</summary>
    [Fact]
    public void Corner_radii_are_clamped_to_a_usable_range()
    {
        var negative = ThemeDefinition.TryParse("""{ "cornerRadius": -8, "previewCornerRadius": -1 }""");
        Assert.Equal(0, negative!.CornerRadius);
        Assert.Equal(0, negative.PreviewCornerRadius);

        var huge = ThemeDefinition.TryParse("""{ "cornerRadius": 999, "previewCornerRadius": 500 }""");
        Assert.Equal(ThemeDefinition.MaxCornerRadius, huge!.CornerRadius);
        Assert.Equal(ThemeDefinition.MaxCornerRadius, huge.PreviewCornerRadius);

        var notANumber = (ThemeDefinition.Dark with { CornerRadius = double.NaN }).Normalized();
        Assert.Equal(ThemeDefinition.Dark.CornerRadius, notANumber.CornerRadius);
    }

    /// <summary>"文件名去掉后缀就是主题 id"：文件里没写 id 时用文件名兜底。</summary>
    [Fact]
    public void Try_parse_falls_back_to_the_file_name_when_id_is_missing()
    {
        var theme = ThemeDefinition.TryParse("""{ "name": "文件里的名字" }""", "my-dark");

        Assert.NotNull(theme);
        Assert.Equal("my-dark", theme!.Id);
        Assert.Equal("文件里的名字", theme.Name);

        // 文件里写了 id 就以它为准（文件名只是兜底）
        var explicitId = ThemeDefinition.TryParse("""{ "id": "inside" }""", "file-name");
        Assert.Equal("inside", explicitId!.Id);
    }

    [Fact]
    public void Name_defaults_to_the_id_when_missing()
    {
        var theme = ThemeDefinition.TryParse("{}", "from-file");

        Assert.NotNull(theme);
        Assert.Equal("from-file", theme!.Id);
        Assert.Equal("from-file", theme.Name);
    }

    [Fact]
    public void Empty_id_and_name_are_filled_with_fallbacks()
    {        var theme = ThemeDefinition.TryParse("""{ "id": "   ", "name": "", "base": "LIGHT" }""");

        Assert.NotNull(theme);
        Assert.Equal(ThemeDefinition.CustomThemeId, theme!.Id);
        Assert.Equal(ThemeDefinition.CustomThemeId, theme.Name);
        Assert.Equal("light", theme.Base);   // base 大小写不敏感，统一收成小写
    }

    [Fact]
    public void Broken_json_returns_null_instead_of_throwing()
    {
        Assert.Null(ThemeDefinition.TryParse("{ \"cornerRadius\": "));
        Assert.Null(ThemeDefinition.TryParse("not json at all"));
    }

    /// <summary>导出再读回来必须完全一样 —— 用户靠"导出 → 改 → 分享"这套流程。</summary>
    [Fact]
    public void Json_round_trip_keeps_every_appearance_field()
    {
        var theme = new ThemeDefinition
        {
            Id = "share-me",
            Name = "分享用",
            Base = "light",
            Colors = ThemeDefinition.Light.Colors with { Accent = "#FF00AA", Panel = "#22000000" },
            Mask = new ThemeMask { Color = "#33000000", Style = ThemeMaskStyles.None },
            CornerRadius = 12,
            PreviewCornerRadius = 6,
            FontFamily = "Noto Sans CJK SC",
        };

        var json = theme.ToJson();
        var parsed = ThemeDefinition.TryParse(json);

        Assert.NotNull(parsed);
        Assert.Equal(theme, parsed);
        Assert.Equal(ThemeMaskStyles.None, parsed!.Mask.Style);
    }

    [Theory]
    [InlineData("My Dark Theme", "my-dark-theme")]
    [InlineData("  spaced  name  ", "spaced-name")]
    [InlineData("UPPER", "upper")]
    [InlineData("我的深色", "theme")]          // 中文被吃掉后不能变成空 id
    [InlineData("dark", "theme")]              // 内置 id 是保留字
    [InlineData("system", "theme")]            // 跟随系统是保留字
    [InlineData("", "theme")]
    [InlineData(null, "theme")]
    public void Suggest_id_produces_a_file_safe_ascii_id(string? name, string expected)
    {
        Assert.Equal(expected, ThemeDefinition.SuggestId(name));
    }

    [Fact]
    public void Suggest_id_dedupes_against_existing_ids()
    {
        Assert.Equal("my-dark", ThemeDefinition.SuggestId("My Dark"));
        Assert.Equal("my-dark-2", ThemeDefinition.SuggestId("My Dark", ["my-dark"]));
        Assert.Equal("my-dark-3", ThemeDefinition.SuggestId("My Dark", ["my-dark", "my-dark-2"]));
        Assert.Equal("theme-2", ThemeDefinition.SuggestId("深色", ["theme"]));
    }

    [Fact]
    public void Suggest_id_caps_the_length()
    {
        var id = ThemeDefinition.SuggestId(new string('a', 80));

        Assert.True(id.Length <= 32, $"id 太长：{id}");
        Assert.Equal(new string('a', 32), id);
    }

    [Fact]
    public void Copy_as_keeps_the_look_and_changes_id_and_name()
    {
        var copy = ThemeDefinition.Light.CopyAs("my-light", "我的浅色");

        Assert.Equal("my-light", copy.Id);
        Assert.Equal("我的浅色", copy.Name);
        Assert.Equal(ThemeDefinition.Light.Base, copy.Base);
        Assert.Equal(ThemeDefinition.Light.Colors, copy.Colors);
        Assert.Equal(ThemeDefinition.Light.Mask, copy.Mask);
    }

    [Theory]
    [InlineData("dim", "dim")]
    [InlineData("DIM", "dim")]
    [InlineData(" Dim ", "dim")]
    [InlineData("none", "none")]
    [InlineData("", "none")]
    [InlineData(null, "none")]
    public void Mask_style_normalization_is_forgiving(string? input, string expected)
    {
        Assert.Equal(expected, ThemeMaskStyles.Normalize(input));
        Assert.Equal(expected == ThemeMaskStyles.None, ThemeMaskStyles.IsNone(input));
    }

    /// <summary>默认必须是「不遮罩」：框选时先看原图，想要变暗再自己选。</summary>
    [Fact]
    public void Default_mask_style_is_none()
    {
        Assert.Equal(ThemeMaskStyles.None, new ThemeMask().Style);
        Assert.Equal(ThemeMaskStyles.None, ThemeDefinition.Dark.Mask.Style);
        Assert.Equal(ThemeMaskStyles.None, ThemeDefinition.Light.Mask.Style);
        Assert.Equal(ThemeMaskStyles.None, ThemeMaskStyles.All[0]);
    }

    /// <summary>
    /// id 会当文件名用（<c>user/themes/&lt;id&gt;.json</c>）：手写的 <c>"id": "../../evil"</c> 不能把文件写出目录。
    /// </summary>
    [Theory]
    [InlineData("../../evil", "evil")]
    [InlineData("..\\..\\evil", "evil")]
    [InlineData("a/b", "a-b")]
    [InlineData("a:b*c?d\"e<f>g|h", "a-b-c-d-e-f-g-h")]
    [InlineData("  ..  ", "custom")]
    [InlineData("", "custom")]
    [InlineData(null, "custom")]
    [InlineData("我的主题", "我的主题")]
    public void Theme_id_is_sanitized_into_a_safe_file_name(string? id, string expected) =>
        Assert.Equal(expected, ThemeDefinition.SanitizeId(id));

    [Fact]
    public void Sanitized_id_never_escapes_the_themes_folder()
    {
        var theme = ThemeDefinition.TryParse("""
            { "id": "../../evil", "name": "坏主题" }
            """);

        Assert.NotNull(theme);
        Assert.DoesNotContain('/', theme!.Id);
        Assert.DoesNotContain('\\', theme.Id);
        Assert.DoesNotContain("..", theme.Id);
    }

    /// <summary>控制字符 / 超长 id 也不能变成奇怪的文件名。</summary>
    [Fact]
    public void Control_characters_and_long_ids_are_handled()
    {
        Assert.Equal("a-b", ThemeDefinition.SanitizeId("a\u0001b"));
        Assert.Equal(64, ThemeDefinition.SanitizeId(new string('x', 200)).Length);
    }
}
