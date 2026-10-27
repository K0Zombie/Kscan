using Kscan.Core.Configuration;
using Kscan.Core.Imaging;

namespace Kscan.Core.Tests;

/// <summary>
/// 定格光标标记：几何（虚线框要贴着光标真实形状）+ 配置默认值
/// （任务书 §3：默认不显示光标，只显示描出的边框；颜色 / 粗细 / 透明度可自定义）。
/// </summary>
public sealed class CursorMarkerTests
{
    private static ImageFrame Frame(int width, int height, params (int X, int Y, int Width, int Height, byte Alpha)[] blocks)
    {
        var pixels = new byte[width * height * ImageFrame.BytesPerPixel];

        foreach (var (x0, y0, w, h, alpha) in blocks)
        {
            for (var y = y0; y < y0 + h; y++)
            {
                for (var x = x0; x < x0 + w; x++)
                {
                    var index = ((y * width) + x) * ImageFrame.BytesPerPixel;
                    pixels[index] = 255;
                    pixels[index + 3] = alpha;
                }
            }
        }

        return new ImageFrame(width, height, pixels);
    }

    /// <summary>光标位图通常比图形本身大一圈：框必须贴着图形，不能按位图尺寸画。</summary>
    [Fact]
    public void Visible_bounds_hug_the_painted_pixels_not_the_bitmap()
    {
        // 32x32 的位图，图形只占 (11,4)-(20,28) —— 就是 Windows 箭头光标的典型情况
        var frame = Frame(32, 32, (11, 4, 10, 25, 255));

        var bounds = CursorMarkerGeometry.VisibleBounds(frame);

        Assert.Equal(new ImageBounds(11, 4, 10, 25), bounds);
        Assert.Equal(20, bounds.Right);
        Assert.Equal(28, bounds.Bottom);
    }

    [Fact]
    public void Fully_transparent_cursor_has_no_visible_bounds()
    {
        Assert.True(CursorMarkerGeometry.VisibleBounds(Frame(32, 32)).IsEmpty);
    }

    [Fact]
    public void Fully_opaque_cursor_uses_the_whole_bitmap()
    {
        Assert.Equal(new ImageBounds(0, 0, 32, 32), CursorMarkerGeometry.VisibleBounds(Frame(32, 32, (0, 0, 32, 32, 255))));
    }

    /// <summary>抗锯齿毛边（很低的 alpha）不能算进去，否则框会一帧大、一帧小地跳。</summary>
    [Fact]
    public void Faint_antialiasing_edges_are_ignored()
    {
        var frame = Frame(32, 32, (0, 0, 32, 32, 3), (10, 10, 6, 8, 255));

        Assert.Equal(new ImageBounds(10, 10, 6, 8), CursorMarkerGeometry.VisibleBounds(frame));
    }

    [Fact]
    public void Alpha_threshold_is_configurable()
    {
        var frame = Frame(8, 8, (1, 1, 2, 2, 20));

        Assert.True(CursorMarkerGeometry.VisibleBounds(frame, 30).IsEmpty);
        Assert.Equal(new ImageBounds(1, 1, 2, 2), CursorMarkerGeometry.VisibleBounds(frame, 10));
    }

    /// <summary>默认配置：不显示光标图像、画虚线框；框的颜色 / 粗细 / 透明度都有默认值。</summary>
    [Fact]
    public void Defaults_draw_nothing_until_the_toggle_key_is_pressed()
    {
        var settings = new AppSettings();

        Assert.True(settings.CursorMarkerEnabled);
        Assert.False(settings.CursorMarkerShowCursor);
        Assert.False(settings.CursorMarkerShowFrame);
        Assert.Equal("Shift", settings.CaptureCursorKey);
        Assert.False(string.IsNullOrWhiteSpace(settings.CursorMarkerColor));
        Assert.InRange(settings.CursorMarkerThickness, 0.5, 8);
        Assert.InRange(settings.CursorMarkerOpacity, 0.1, 1);
    }

    /// <summary>旧配置里没有这些新字段：读出来必须是"什么都不画"的新默认值。</summary>
    [Fact]
    public void Older_settings_files_draw_nothing_by_default()
    {
        const string json = """
            { "version": 2, "cursorMarkerEnabled": true, "captureCursorKey": "Ctrl" }
            """;

        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            json,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

        Assert.NotNull(loaded);
        Assert.False(loaded!.CursorMarkerShowCursor);
        Assert.False(loaded.CursorMarkerShowFrame);
        Assert.Equal("Ctrl", loaded.CaptureCursorKey);
        Assert.True(loaded.CursorMarkerThickness > 0);
    }

    /// <summary>
    /// version 2 的配置里存的是老默认值 <c>true</c>；迁移成新版时按版本把光标图像关掉一次，
    /// 之后用户自己打开就不会再被覆盖。
    /// </summary>
    [Fact]
    public void Migrating_from_version_2_turns_the_cursor_image_off_once()
    {
        var migrated = (AppSettings.Default with { Version = 2, CursorMarkerShowCursor = true }).Migrate();

        Assert.Equal(AppSettings.CurrentVersion, migrated.Version);
        Assert.False(migrated.CursorMarkerShowCursor);

        var reopened = (migrated with { CursorMarkerShowCursor = true }).Migrate();

        Assert.True(reopened.CursorMarkerShowCursor);
    }

    /// <summary>version 3 的配置里虚线框还是老默认值 <c>true</c>：迁移时关掉一次，用户自己打开的不再覆盖。</summary>
    [Fact]
    public void Migrating_from_version_3_turns_the_dashed_frame_off_once()
    {
        var migrated = (AppSettings.Default with { Version = 3, CursorMarkerShowFrame = true }).Migrate();

        Assert.Equal(AppSettings.CurrentVersion, migrated.Version);
        Assert.False(migrated.CursorMarkerShowFrame);

        var reopened = (migrated with { CursorMarkerShowFrame = true }).Migrate();

        Assert.True(reopened.CursorMarkerShowFrame);
    }

    /// <summary>version 2 → 3 的迁移不能把用户删掉的「框选排除应用」热键又加回来。</summary>
    [Fact]
    public void Migrating_from_version_2_does_not_re_add_the_excluded_app_hotkey()
    {
        var migrated = (AppSettings.Default with { Version = 2 }).Migrate();

        Assert.DoesNotContain(
            migrated.Hotkeys,
            binding => binding.ActionId == HotkeyActionIds.PickExcludedApp);
    }
}
