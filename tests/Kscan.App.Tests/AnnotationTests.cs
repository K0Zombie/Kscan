using Kscan.App.Services;
using Kscan.App.Views;
using Kscan.Core.Configuration;
using SkiaSharp;

namespace Kscan.App.Tests;

/// <summary>
/// 标注渲染（纯 Skia，不依赖 UI）：预览和导出走同一条路径，所以这里测的就是"最终图长什么样"。
/// </summary>
public sealed class AnnotationTests
{
    private static readonly uint Red = 0xFFFF3B30;

    [Fact]
    public void Empty_annotation_list_keeps_the_original_pixels()
    {
        using var source = Fill(40, 30, new SKColor(0xFF, 0x20, 0x60, 0xA0));
        using var rendered = AnnotationRenderer.Render(source, []);

        Assert.Equal(source.Width, rendered.Width);
        Assert.Equal(source.Height, rendered.Height);
        Assert.Equal(source.GetPixel(10, 10), rendered.GetPixel(10, 10));
        Assert.Equal(source.GetPixel(39, 29), rendered.GetPixel(39, 29));
    }

    [Fact]
    public void Rectangle_item_draws_its_border_only()
    {
        using var source = Fill(100, 100, SKColors.White);

        var item = new AnnotationItem
        {
            Tool = AnnotationTool.Rect,
            Points = [new SKPoint(10, 10), new SKPoint(90, 90)],
            Color = Red,
            Thickness = 4,
        };

        using var rendered = AnnotationRenderer.Render(source, [item]);

        // 边框上变色（取上边中间）
        var border = rendered.GetPixel(50, 10);
        Assert.True(border.Red > 200 && border.Green < 120, $"边框应被画成红色，实际 {border}");

        // 内部保持原样（矩形只有边框、不填充）
        Assert.Equal(SKColors.White, rendered.GetPixel(50, 50));
    }

    [Fact]
    public void Mosaic_turns_a_region_into_uniform_blocks()
    {
        // 横向渐变：每列颜色不同，马赛克后同一格内应完全相同
        using var source = new SKBitmap(48, 24, SKColorType.Bgra8888, SKAlphaType.Unpremul);

        for (var x = 0; x < source.Width; x++)
        {
            var color = new SKColor((byte)(x * 4), 0x40, 0x80);

            for (var y = 0; y < source.Height; y++)
            {
                source.SetPixel(x, y, color);
            }
        }

        var item = new AnnotationItem
        {
            Tool = AnnotationTool.Mosaic,
            Points = [new SKPoint(0, 0), new SKPoint(24, 24)],
            Block = 12,
        };

        using var rendered = AnnotationRenderer.Render(source, [item]);

        // 第一个 12px 格子内颜色必须完全一致
        var first = rendered.GetPixel(1, 1);

        for (var x = 0; x < 12; x++)
        {
            for (var y = 0; y < 12; y++)
            {
                Assert.Equal(first, rendered.GetPixel(x, y));
            }
        }

        // 第二个格子应取到不同的原图颜色（说明不是整块涂成一个色）
        Assert.NotEqual(first, rendered.GetPixel(13, 1));

        // 区域外保持原样
        Assert.Equal(source.GetPixel(40, 10), rendered.GetPixel(40, 10));
    }

    [Fact]
    public void Arrow_and_pen_change_pixels_along_their_path()
    {
        using var source = Fill(80, 80, SKColors.White);

        var arrow = new AnnotationItem
        {
            Tool = AnnotationTool.Arrow,
            Points = [new SKPoint(10, 10), new SKPoint(70, 70)],
            Color = Red,
            Thickness = 4,
        };

        var pen = new AnnotationItem
        {
            Tool = AnnotationTool.Pen,
            Points = [new SKPoint(5, 70), new SKPoint(20, 55), new SKPoint(35, 70)],
            Color = Red,
            Thickness = 4,
        };

        using var rendered = AnnotationRenderer.Render(source, [arrow, pen]);

        // 箭头线中点附近 + 画笔折线上
        var highlighted = 0;

        for (var y = 0; y < 80; y++)
        {
            for (var x = 0; x < 80; x++)
            {
                if (rendered.GetPixel(x, y) != SKColors.White)
                {
                    highlighted++;
                }
            }
        }

        Assert.True(highlighted > 30, $"箭头/画笔应该画出可见像素，实际只有 {highlighted} 个");
    }

    [Fact]
    public void Number_item_fills_a_disc_with_its_color()
    {
        using var source = Fill(60, 60, SKColors.White);

        var item = new AnnotationItem
        {
            Tool = AnnotationTool.Number,
            Points = [new SKPoint(30, 30)],
            Color = Red,
            Thickness = 4,
            Number = 3,
        };

        using var rendered = AnnotationRenderer.Render(source, [item]);

        // 圆心附近是红色（数字是白字，取偏离中心一点的位置）
        var probe = rendered.GetPixel(30, 22);
        Assert.True(probe.Red > 180, $"序号圆应被填充成红色，实际 {probe}");
    }

    // ---------------- 热键迁移 ----------------

    [Fact]
    public void Version_10_migration_adds_the_annotation_hotkey()
    {
        var migrated = new AppSettings { Version = 10 }.Migrate();

        Assert.Equal(AppSettings.CurrentVersion, migrated.Version);
        Assert.Contains(
            migrated.Hotkeys,
            binding => binding.ActionId == HotkeyActionIds.CaptureRegionWithAnnotation
                       && binding.Keys == AppSettings.DefaultCaptureRegionWithAnnotationHotkey);
    }

    [Fact]
    public void Version_11_migration_adds_the_annotate_clipboard_hotkey()
    {
        var migrated = new AppSettings { Version = 11 }.Migrate();

        Assert.Equal(AppSettings.CurrentVersion, migrated.Version);
        Assert.Contains(
            migrated.Hotkeys,
            binding => binding.ActionId == HotkeyActionIds.AnnotateClipboard
                       && binding.Keys == AppSettings.DefaultAnnotateClipboardHotkey);
    }

    /// <summary>
    /// 视口渲染的坐标语义：屏幕像素 = 图片像素 × scale + offset（都以设备像素为单位）。
    /// 这条钉住"125%/140% 屏上越往右下偏得越多"那类 bug —— 之前把 DIP 当设备像素，缩放就错位。
    /// </summary>
    [Fact]
    public void RenderView_maps_image_pixels_with_scale_and_offset()
    {
        using var source = Fill(100, 100, SKColors.White);

        // 在图片 (10, 20) 放一个红点，便于定位
        source.SetPixel(10, 20, new SKColor(0xFF, 0, 0));

        const int viewWidth = 200;
        const int viewHeight = 200;
        const float scale = 2f;
        var offset = new SKPoint(30, 40);

        using var view = AnnotationRenderer.RenderView(source, [], viewWidth, viewHeight, scale, offset);

        Assert.Equal(viewWidth, view.Width);
        Assert.Equal(viewHeight, view.Height);

        var expectedX = (int)Math.Round(offset.X + (10 * scale));
        var expectedY = (int)Math.Round(offset.Y + (20 * scale));

        var hit = view.GetPixel(expectedX, expectedY);
        Assert.True(hit.Red > 180 && hit.Green < 80, $"红点应该落在 ({expectedX},{expectedY})，实际取到 {hit}");
    }

    /// <summary>长期踩过的坑：默认字体不含 CJK，中文标注变方框/乱码。</summary>
    [Fact]
    public void Text_typeface_can_render_chinese()
    {
        var typeface = AnnotationRenderer.Typeface;

        Assert.NotNull(typeface);
        Assert.True(
            typeface.GetGlyph('中') != 0,
            $"标注字体（{typeface.FamilyName}）没有中文字形，中文会变乱码");
    }

    [Fact]
    public void Long_text_wraps_inside_a_fixed_width_box()
    {
        using var source = Fill(400, 200, SKColors.White);

        var text = "这是一段比较长的中文标注文字，用来验证定宽自动换行不会一直往右延伸";

        using var auto = new SKFont(AnnotationRenderer.Typeface, 18);
        using var paint = new SKPaint { Color = SKColors.Red, IsAntialias = true };

        var lines = AnnotationRenderer.Wrap(text, 120, auto, paint);

        Assert.True(lines.Count > 1, "定宽时应该断成多行");
        Assert.All(lines, line => Assert.True(
            auto.MeasureText(line, paint) <= 125,
            $"每行宽度不能超过框宽（实际 {auto.MeasureText(line, paint):0.#}）：{line}"));

        // 显式换行也不能丢
        var explicitLines = AnnotationRenderer.Wrap("第一行\n第二行", 0, auto, paint);
        Assert.Equal(2, explicitLines.Count);
    }

    [Fact]
    public void Viewport_zoom_keeps_the_image_point_under_the_cursor()
    {
        var view = new AnnotationViewport(1920, 1080);
        view.Fit(new Avalonia.Size(800, 500));
        var anchor = new Avalonia.Point(330, 210);
        var before = view.ToImage(anchor);
        view.Zoom(anchor, 2);
        var after = view.ToImage(anchor);
        Assert.Equal(before.X, after.X, 3);
        Assert.Equal(before.Y, after.Y, 3);
        view.Pan(new Avalonia.Vector(30, -10));
        var mapped = view.ToView(before);
        Assert.Equal(anchor.X + 30, mapped.X, 3);
        Assert.Equal(anchor.Y - 10, mapped.Y, 3);
    }

    [Fact]
    public void Version_12_migration_adds_the_screen_annotate_hotkey()
    {
        var migrated = new AppSettings { Version = 12 }.Migrate();

        Assert.Equal(AppSettings.CurrentVersion, migrated.Version);
        Assert.Contains(
            migrated.Hotkeys,
            binding => binding.ActionId == HotkeyActionIds.AnnotateScreen
                       && binding.Keys == AppSettings.DefaultAnnotateScreenHotkey);
    }

    [Fact]
    public void Crop_item_shrinks_the_rendered_output()
    {
        using var source = Fill(200, 120, SKColors.White);

        var crop = new AnnotationItem
        {
            Tool = AnnotationTool.Crop,
            Points = [new SKPoint(20, 30), new SKPoint(120, 90)],
        };

        using var rendered = AnnotationRenderer.Render(source, [crop]);

        Assert.Equal(100, rendered.Width);
        Assert.Equal(60, rendered.Height);
    }

    [Fact]
    public void Spotlight_dims_the_area_outside_the_rect()
    {
        using var source = Fill(100, 100, SKColors.White);

        var spot = new AnnotationItem
        {
            Tool = AnnotationTool.Spotlight,
            Points = [new SKPoint(30, 30), new SKPoint(70, 70)],
        };

        using var rendered = AnnotationRenderer.Render(source, [spot]);

        Assert.True(rendered.GetPixel(10, 10).Red < rendered.GetPixel(50, 50).Red, "框外应被压暗");
    }

    [Fact]
    public void Polyline_and_highlight_change_pixels()
    {
        using var source = Fill(120, 120, SKColors.White);

        var polyline = new AnnotationItem
        {
            Tool = AnnotationTool.Polyline,
            Points = [new SKPoint(10, 10), new SKPoint(60, 60), new SKPoint(110, 20)],
            Color = 0xFF0000FF,
            Thickness = 4,
        };

        var highlight = new AnnotationItem
        {
            Tool = AnnotationTool.Highlight,
            Points = [new SKPoint(10, 90), new SKPoint(60, 110)],
            Color = 0xFFFF0000,
            Thickness = 4,
        };

        using var rendered = AnnotationRenderer.Render(source, [polyline, highlight]);

        var changed = 0;

        for (var y = 0; y < 120; y++)
        {
            for (var x = 0; x < 120; x++)
            {
                if (rendered.GetPixel(x, y) != SKColors.White)
                {
                    changed++;
                }
            }
        }

        Assert.True(changed > 40, $"折线/高亮应画出可见像素，实际 {changed}");
    }

    [Fact]
    public void Dimension_draws_a_length_label()
    {
        using var source = Fill(200, 80, SKColors.White);

        var dim = new AnnotationItem
        {
            Tool = AnnotationTool.Dimension,
            Points = [new SKPoint(20, 40), new SKPoint(140, 40)],
            Color = 0xFF000000,
            Thickness = 2,
        };

        using var rendered = AnnotationRenderer.Render(source, [dim]);

        var hits = new[] { (80, 40), (20, 40), (140, 40) }
            .Count(point => rendered.GetPixel(point.Item1, point.Item2) != SKColors.White);

        Assert.True(hits >= 2, $"尺寸标注应在线与刻度处着墨，命中 {hits}");
    }

    [Fact]
    public void Zoom_lens_magnifies_the_region_beside_it()
    {
        using var source = Fill(100, 100, SKColors.White);
        source.SetPixel(25, 25, new SKColor(0xFF, 0, 0));

        var lens = new AnnotationItem
        {
            Tool = AnnotationTool.ZoomLens,
            Points = [new SKPoint(20, 20), new SKPoint(40, 40)],
            Thickness = 2,
        };

        using var rendered = AnnotationRenderer.Render(source, [lens]);

        var hit = rendered.GetPixel(60, 30);

        Assert.True(hit.Red > 180 && hit.Green < 80, $"放大镜应把红点放大到目标位置，实际 {hit}");
    }

    [Fact]
    public void Annotation_defaults_are_sane()
    {
        var settings = new AppSettings();

        Assert.Equal("#FFFF3B30", settings.AnnotationColor);
        Assert.Equal(4f, settings.AnnotationThickness);
    }

    [Fact]
    public void Annotation_color_parser_accepts_argb_and_rgb()
    {
        Assert.Equal(0xFFFF3B30u, AnnotationWindow.ParseColor("#FFFF3B30"));
        Assert.Equal(0xFF34C759u, AnnotationWindow.ParseColor("#34C759"));
        Assert.Equal(0xFFFF3B30u, AnnotationWindow.ParseColor("FFFF3B30"));

        Assert.Null(AnnotationWindow.ParseColor(""));
        Assert.Null(AnnotationWindow.ParseColor(null));
        Assert.Null(AnnotationWindow.ParseColor("#12"));
        Assert.Null(AnnotationWindow.ParseColor("not-a-color"));
    }

    [Fact]
    public void Font_override_falls_back_to_a_cjk_typeface()
    {
        try
        {
            AnnotationRenderer.FontFamily = "NoSuchFontFamily_KS";
            AnnotationRenderer.ResetTypeface();

            // 指定的字体不存在时也必须回退到能显示中文的字体
            Assert.NotEqual(0, AnnotationRenderer.Typeface.GetGlyph('中'));
        }
        finally
        {
            AnnotationRenderer.FontFamily = string.Empty;
            AnnotationRenderer.ResetTypeface();
        }
    }

    private static SKBitmap Fill(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        bitmap.Erase(color);
        return bitmap;
    }
}
