using Avalonia;
using Kscan.App.Services;
using Kscan.Core.Configuration;

namespace Kscan.App.Tests;

public sealed class QuickAnnotationLayoutTests
{
    private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);
    private static readonly PixelRect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void Small_region_places_toolbar_below_without_changing_image_geometry()
    {
        var image = new PixelRect(500, 200, 640, 400);
        var layout = QuickAnnotationLayout.TryCreate(image, Monitor, Work, 1);
        Assert.NotNull(layout);
        Assert.Equal(image, layout.Image);
        Assert.True(layout.Toolbar.Y > image.Bottom);
        Assert.True(layout.Toolbar.Right <= Work.Right && layout.Toolbar.Bottom <= Work.Bottom);
    }

    [Fact]
    public void Bottom_edge_places_toolbar_above_and_right_edge_is_clamped()
    {
        var image = new PixelRect(1520, 700, 400, 300);
        var layout = QuickAnnotationLayout.TryCreate(image, Monitor, Work, 1);
        Assert.NotNull(layout);
        Assert.True(layout.Toolbar.Bottom < image.Y);
        Assert.True(layout.Toolbar.Right < Work.Right);
    }

    [Theory]
    [InlineData(1700, 500)]
    [InlineData(500, 950)]
    [InlineData(1920, 1040)]
    public void Large_regions_use_independent_editor(int width, int height) =>
        Assert.Null(QuickAnnotationLayout.TryCreate(new PixelRect(0, 0, width, height), Monitor, Work, 1));

    [Fact]
    public void Eighty_percent_is_inclusive_but_larger_uses_editor()
    {
        Assert.NotNull(QuickAnnotationLayout.TryCreate(new PixelRect(0, 0, 1536, 400), Monitor, Work, 1));
        Assert.Null(QuickAnnotationLayout.TryCreate(new PixelRect(0, 0, 1537, 400), Monitor, Work, 1));
        Assert.NotNull(QuickAnnotationLayout.TryCreate(new PixelRect(0, 0, 600, 832), Monitor, Work, 1));
        Assert.Null(QuickAnnotationLayout.TryCreate(new PixelRect(0, 0, 600, 833), Monitor, Work, 1));
    }

    [Fact]
    public void Cross_monitor_region_and_missing_toolbar_space_use_independent_editor()
    {
        Assert.Null(QuickAnnotationLayout.TryCreate(new PixelRect(-100, 200, 500, 300), Monitor, Work, 1));
        // 图片虽不足工作区 85%，上下却都没有足够的工具栏空间。
        Assert.Null(QuickAnnotationLayout.TryCreate(new PixelRect(100, 100, 500, 840), Monitor, Work, 1));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Negative_monitor_coordinates_and_mixed_dpi_are_physical(double dpi)
    {
        var monitor = new PixelRect(-2560, -100, 2560, 1440);
        var work = new PixelRect(-2560, -100, 2560, 1400);
        var image = new PixelRect(-1900, 200, 600, 400);
        var layout = QuickAnnotationLayout.TryCreate(image, monitor, work, dpi);
        Assert.NotNull(layout);
        Assert.Equal(image, layout.Image);
        Assert.Equal((int)Math.Ceiling(QuickAnnotationLayout.ToolbarHeightDip * dpi), layout.Toolbar.Height);
        Assert.True(layout.Toolbar.X >= work.X && layout.Toolbar.Right <= work.Right);
    }

    [Fact]
    public void Capture_hotkeys_remain_distinct_and_user_bindings_are_kept()
    {
        var settings = new AppSettings
        {
            Version = 0,
            Hotkeys = new HotkeyBindings([new(HotkeyActionIds.CaptureRegion, "Ctrl+F1"), new(HotkeyActionIds.CaptureRegionAndPin, "Ctrl+F3")]),
        }.Migrate();
        Assert.Contains(settings.Hotkeys, x => x.ActionId == HotkeyActionIds.CaptureRegion && x.Keys == "Ctrl+F1");
        Assert.Contains(settings.Hotkeys, x => x.ActionId == HotkeyActionIds.CaptureRegionAndPin && x.Keys == "Ctrl+F3");
        Assert.Contains(settings.Hotkeys, x => x.ActionId == HotkeyActionIds.CaptureRegionWithAnnotation && x.Keys == "Ctrl+Shift+F1");
    }
}
