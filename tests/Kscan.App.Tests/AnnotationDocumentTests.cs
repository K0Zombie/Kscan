using Kscan.App.Services;
using SkiaSharp;

namespace Kscan.App.Tests;

public sealed class AnnotationDocumentTests
{
    private static AnnotationItem Item(AnnotationTool tool = AnnotationTool.Rect) => new()
    {
        Tool = tool, Points = [new SKPoint(10, 20), new SKPoint(80, 60)], Text = "原始文字", Number = 1,
    };

    [Fact]
    public void A_whole_drag_is_one_undo_step_and_cancel_preserves_redo()
    {
        var doc = new AnnotationDocument();
        doc.Add(Item());
        doc.Select(0);
        doc.Move(10, 0); doc.Move(10, 0); doc.Commit();
        Assert.Equal(30, doc.Items[0].Points[0].X);
        doc.Undo();
        Assert.Equal(10, doc.Items[0].Points[0].X);
        doc.Select(0); doc.Move(90, 0); doc.Cancel();
        Assert.Equal(10, doc.Items[0].Points[0].X);
        Assert.True(doc.CanRedo);
        doc.Redo();
        Assert.Equal(30, doc.Items[0].Points[0].X);
    }

    [Fact]
    public void Multi_selection_style_and_deletion_are_atomic()
    {
        var doc = new AnnotationDocument();
        doc.Add(Item()); doc.Add(Item()); doc.SelectAll();
        doc.ApplyStyle(0xFF0A84FF, 8, 24);
        Assert.All(doc.Items, item => { Assert.Equal(8, item.Thickness); Assert.Equal(24, item.Block); });
        doc.Undo();
        Assert.Empty(doc.Selection);
        Assert.All(doc.Items, item => Assert.Equal(4, item.Thickness));
        doc.SelectAll(); doc.Delete(); Assert.Empty(doc.Items);
        doc.Undo(); Assert.Equal(2, doc.Items.Count);
    }

    [Fact]
    public void New_crop_replaces_old_crop_and_undo_restores_it()
    {
        var doc = new AnnotationDocument();
        doc.Add(Item(AnnotationTool.Crop));
        var replacement = new AnnotationItem { Tool = AnnotationTool.Crop, Points = [new SKPoint(30, 30), new SKPoint(50, 50)] };
        doc.Add(replacement);
        Assert.Equal(30, Assert.Single(doc.Items).Points[0].X);
        doc.Undo();
        Assert.Equal(10, Assert.Single(doc.Items).Points[0].X);
    }

    [Fact]
    public void Text_edit_is_undoable_and_snapshots_do_not_share_points()
    {
        var doc = new AnnotationDocument();
        var input = Item(AnnotationTool.Text);
        doc.Add(input);
        input.Points.Clear();
        doc.Select(0);
        doc.ApplyStyle(0xFF000000, 5, 12, "中文修改\n第二行");
        Assert.Equal("中文修改\n第二行", doc.Items[0].Text);
        doc.Undo();
        Assert.Equal("原始文字", doc.Items[0].Text);
        Assert.Equal(2, doc.Items[0].Points.Count);
    }

    [Fact]
    public void Freehand_hit_testing_does_not_select_empty_space_inside_its_bounds()
    {
        var doc = new AnnotationDocument();
        doc.Add(new AnnotationItem { Tool = AnnotationTool.Pen, Points = [new SKPoint(0, 0), new SKPoint(100, 100)] });
        Assert.Equal(-1, doc.HitTest(new SKPoint(0, 100), 3));
        Assert.Equal(0, doc.HitTest(new SKPoint(50, 51), 3));
    }

    [Fact]
    public void Numbering_follows_history_and_new_edits_clear_redo()
    {
        var doc = new AnnotationDocument();
        doc.Add(Item(AnnotationTool.Number)); Assert.Equal(2, doc.NextNumber);
        doc.Undo(); Assert.Equal(1, doc.NextNumber);
        doc.Add(Item()); Assert.False(doc.CanRedo);
    }

    [Fact]
    public void Crop_export_does_not_burn_preview_border_into_the_result()
    {
        using var source = new SKBitmap(100, 100);
        source.Erase(SKColors.White);
        using var result = AnnotationRenderer.Render(source,
            [new AnnotationItem { Tool = AnnotationTool.Crop, Points = [new SKPoint(10, 10), new SKPoint(80, 80)] }]);
        Assert.Equal(70, result.Width);
        Assert.Equal(SKColors.White, result.GetPixel(0, 0));
        Assert.Equal(SKColors.White, result.GetPixel(69, 69));
    }
}
