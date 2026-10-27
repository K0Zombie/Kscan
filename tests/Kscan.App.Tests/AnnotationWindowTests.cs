using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kscan.App.Views;
using Kscan.Core.Imaging;

namespace Kscan.App.Tests;

/// <summary>
/// 标注编辑器的冒烟测试：能建出来、能按键收尾、返回的帧符合预期。
/// 几何/像素细节由 <see cref="AnnotationTests"/> 覆盖（纯 Skia）。
/// </summary>
public sealed class AnnotationWindowTests
{
    [AvaloniaFact]
    public void Very_tall_image_exports_without_an_extra_copy_when_unmodified_and_can_be_annotated()
    {
        var pixels = new byte[64 * 40000 * 4];
        Array.Fill(pixels, (byte)255);
        var source = new ImageFrame(64, 40000, pixels);
        var document = new Kscan.App.Services.AnnotationDocument();
        using var canvas = new AnnotationCanvas(source, document);
        Assert.Same(source, canvas.Export());
        document.Add(new Kscan.App.Services.AnnotationItem
        {
            Tool = Kscan.App.Services.AnnotationTool.Rect,
            Points = [new SkiaSharp.SKPoint(5, 39900), new SkiaSharp.SKPoint(50, 39950)],
            Color = 0xFFFF0000,
        });
        var annotated = canvas.Export();
        Assert.Equal(64, annotated.Width); Assert.Equal(40000, annotated.Height);
        Assert.False(source.Pixels.SequenceEqual(annotated.Pixels));
        Assert.Equal((byte)255, source.Pixels[(39900 * 64 + 5) * 4]);
    }

    [AvaloniaFact]
    public void Quick_done_returns_full_size_annotated_image_and_cancel_returns_null()
    {
        var layout = Kscan.App.Services.QuickAnnotationLayout.TryCreate(new PixelRect(400, 250, 400, 260),
            new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1)!;
        var source = Frame(400, 260);
        var quick = new AnnotationWindow(source, quickLayout: layout);
        quick.Show(); Pump();
        quick.DocumentForTest.Add(new Kscan.App.Services.AnnotationItem
        {
            Tool = Kscan.App.Services.AnnotationTool.Rect,
            Points = [new SkiaSharp.SKPoint(30, 40), new SkiaSharp.SKPoint(120, 160)],
        });
        var done = quick.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "完成复制"));
        done.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        var result = quick.Result.GetAwaiter().GetResult();
        Assert.NotNull(result);
        Assert.Equal(400, result.Value.Frame.Width); Assert.Equal(260, result.Value.Frame.Height);
        Assert.False(source.Pixels.SequenceEqual(result.Value.Frame.Pixels));
        var canceled = new AnnotationWindow(source, quickLayout: layout);
        canceled.Show(); Pump();
        canceled.Close();
        Assert.Null(canceled.Result.GetAwaiter().GetResult());
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public async Task Quick_editor_keeps_capture_position_and_expansion_preserves_edit_history(double dpi)
    {
        var layout = Kscan.App.Services.QuickAnnotationLayout.TryCreate(new PixelRect(400, 250, 400, 260),
            new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), dpi)!;
        var quick = new AnnotationWindow(Frame(400, 260), quickLayout: layout);
        quick.SetRenderScaling(dpi);
        quick.Show(); Pump();
        Assert.True(quick.IsQuickForTest);
        var canvas = (AnnotationCanvas)quick.CanvasForTest;
        var origin = canvas.TranslatePoint(default, quick)!.Value;
        Assert.Equal(400, origin.X * dpi, 2); Assert.Equal(250, origin.Y * dpi, 2);
        Assert.Equal(400, canvas.Bounds.Width * dpi, 2); Assert.Equal(260, canvas.Bounds.Height * dpi, 2);
        Assert.False(canvas.AllowViewNavigation);
        Point At(int x, int y) => canvas.TranslatePoint(canvas.Viewport.ToView(new SkiaSharp.SKPoint(x, y)), quick)!.Value;
        quick.MouseDown(At(40, 40), MouseButton.Left);
        quick.MouseMove(At(200, 150)); quick.MouseUp(At(200, 150), MouseButton.Left); Pump();
        Assert.Single(quick.ItemsForTest);
        if (Environment.GetEnvironmentVariable("KSCAN_ANNOTATION_PREVIEW") is { Length: > 0 } path)
        {
            using var frame = quick.CaptureRenderedFrame();
            frame!.Save(System.IO.Path.ChangeExtension(path, null) + "-quick.png");
        }
        var expand = quick.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "独立编辑"));
        expand.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        var editor = quick.ExpandedEditorForTest;
        Assert.NotNull(editor); Assert.False(editor.IsQuickForTest);
        Assert.Same(quick.DocumentForTest, editor.DocumentForTest);
        editor.DocumentForTest.Undo(); Assert.Empty(editor.ItemsForTest);
        editor.DocumentForTest.Redo(); Assert.Single(editor.ItemsForTest);
        editor.Close(); Pump();
        Assert.Null(await quick.Result.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [AvaloniaFact]
    public void Toolbar_is_separate_from_canvas_and_plain_typing_does_not_execute_commands()
    {
        var window = new AnnotationWindow(Frame(600, 400));
        window.Show(); Pump();
        foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(x => x.Name?.StartsWith("Tool") == true))
            Assert.True(button.Bounds.Width > 0 && button.Bounds.Height > 0);
        var canvas = window.CanvasForTest;
        var origin = canvas.TranslatePoint(default, window)!.Value;
        Assert.True(origin.X >= 148, "工具栏必须在画布之外");
        window.MouseDown(new Point(35, 30), MouseButton.Left);
        window.MouseUp(new Point(35, 30), MouseButton.Left);
        Assert.Empty(window.ItemsForTest);
        canvas.Focus();
        window.KeyPress(Key.R, RawInputModifiers.None, PhysicalKey.R, "r");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "Enter");
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Assert.False(window.Result.IsCompleted);
        Pump();
        if (Environment.GetEnvironmentVariable("KSCAN_ANNOTATION_PREVIEW") is { Length: > 0 } path)
        {
            using var frame = window.CaptureRenderedFrame();
            frame!.Save(path);
        }
        window.Close();
    }

    [AvaloniaFact]
    public void Horizontal_line_is_retained_and_escape_cancels_an_unfinished_shape()
    {
        var window = new AnnotationWindow(Frame(300, 200));
        window.Show(); Pump();
        var canvas = (AnnotationCanvas)window.CanvasForTest;
        canvas.SetTool(Kscan.App.Services.AnnotationTool.Line);
        Point At(int x, int y) => canvas.TranslatePoint(canvas.Viewport.ToView(new SkiaSharp.SKPoint(x, y)), window)!.Value;
        window.MouseDown(At(20, 80), MouseButton.Left);
        window.MouseMove(At(200, 80));
        window.MouseUp(At(200, 80), MouseButton.Left);
        Assert.Single(window.ItemsForTest);
        canvas.SetTool(Kscan.App.Services.AnnotationTool.Rect);
        window.MouseDown(At(30, 30), MouseButton.Left);
        window.MouseMove(At(90, 90));
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "Escape");
        window.MouseUp(At(90, 90), MouseButton.Left);
        Assert.Single(window.ItemsForTest);
        Assert.False(window.Result.IsCompleted);
        window.Close();
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Drawing_after_zoom_uses_image_coordinates_at_each_display_scale(double scaling)
    {
        var window = new AnnotationWindow(Frame(400, 300));
        window.SetRenderScaling(scaling);
        window.Show(); Pump();
        var canvas = (AnnotationCanvas)window.CanvasForTest;
        canvas.Zoom(1.5); Pump();
        Point At(int x, int y) => canvas.TranslatePoint(canvas.Viewport.ToView(new SkiaSharp.SKPoint(x, y)), window)!.Value;
        window.MouseMove(At(80, 70));
        window.MouseDown(At(80, 70), MouseButton.Left);
        window.MouseMove(At(200, 180));
        window.MouseUp(At(200, 180), MouseButton.Left);
        var item = Assert.Single(window.ItemsForTest);
        Assert.Equal(80, item.Points[0].X, 1); Assert.Equal(70, item.Points[0].Y, 1);
        Assert.Equal(200, item.Points[1].X, 1); Assert.Equal(180, item.Points[1].Y, 1);
        Pump();
        // 同一块 WriteableBitmap 被复用时也必须显示最新笔画，不能只检查文档数据。
        using (var rendered = window.CaptureRenderedFrame()!)
        using (var pixels = rendered.Lock())
        {
            var p = At(140, 70);
            var sx = (int)Math.Round(p.X * window.RenderScaling);
            var sy = (int)Math.Round(p.Y * window.RenderScaling);
            var sample = new byte[4];
            System.Runtime.InteropServices.Marshal.Copy(pixels.Address + sy * pixels.RowBytes + sx * 4, sample, 0, 4);
            var rgba = pixels.Format == Avalonia.Platform.PixelFormat.Rgba8888;
            var red = sample[rgba ? 0 : 2];
            var blue = sample[rgba ? 2 : 0];
            Assert.True(red > 200 && blue < 100, $"预览应在对应坐标显示红线：{string.Join(',', sample)}");
        }
        window.Close();
    }

    [AvaloniaFact]
    public void Chinese_text_can_be_added_and_edited_without_deleting_the_selection_while_typing()
    {
        var window = new AnnotationWindow(Frame(400, 300));
        window.Show(); Pump();
        var canvas = (AnnotationCanvas)window.CanvasForTest;
        var textTool = window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "ToolText");
        textTool.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var point = canvas.TranslatePoint(canvas.Viewport.ToView(new SkiaSharp.SKPoint(50, 60)), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump();
        var input = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.AcceptsReturn);
        Assert.True(input.IsVisible);
        Assert.Same(canvas.Parent, input.Parent?.Parent);
        input.Text = "中文标注\n第二行"; input.Focus();
        Pump();
        if (Environment.GetEnvironmentVariable("KSCAN_ANNOTATION_PREVIEW") is { Length: > 0 } path)
        {
            using var rendered = window.CaptureRenderedFrame();
            rendered!.Save(System.IO.Path.ChangeExtension(path, null) + "-text.png");
        }
        window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, "Enter"); Pump();
        Assert.Equal("中文标注\n第二行", Assert.Single(window.ItemsForTest).Text);
        window.DocumentForTest.Select(0); Pump();
        var edit = window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "编辑选中文字"));
        edit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        input.Focus(); input.Text = "修改后的文字";
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, "Delete");
        Assert.Single(window.ItemsForTest);
        input.Text = "修改后的文字";
        window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, "Enter"); Pump();
        Assert.Equal("修改后的文字", Assert.Single(window.ItemsForTest).Text);
        Assert.False(window.Result.IsCompleted);
        window.DocumentForTest.Undo();
        Assert.Equal("中文标注\n第二行", Assert.Single(window.ItemsForTest).Text);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Large_image_opens_fitted_to_window_and_exports_full_size(double dpi)
    {
        var window = new AnnotationWindow(Frame(2000, 1200));
        window.SetRenderScaling(dpi);
        window.Show(); Pump();
        var canvas = (AnnotationCanvas)window.CanvasForTest;
        Assert.True(canvas.Viewport.Scale * dpi < 1);
        Assert.True(2000 * canvas.Viewport.Scale <= canvas.Bounds.Width);
        Assert.True(1200 * canvas.Viewport.Scale <= canvas.Bounds.Height);
        window.DocumentForTest.Add(new Kscan.App.Services.AnnotationItem
        {
            Tool = Kscan.App.Services.AnnotationTool.Arrow,
            Points = [new SkiaSharp.SKPoint(900, 500), new SkiaSharp.SKPoint(1100, 650)],
        });
        var exported = canvas.Export();
        Assert.Equal(2000, exported.Width); Assert.Equal(1200, exported.Height);
        canvas.Fit();
        Assert.True(canvas.Viewport.Scale * dpi < 1);
        canvas.Fit(true);
        Assert.Equal(1, canvas.Viewport.Scale * dpi, 6);
        window.Close();
    }

    [AvaloniaFact]
    public void Escape_cancels_inline_text_without_closing_or_changing_the_document()
    {
        var window = new AnnotationWindow(Frame(400, 300));
        window.Show(); Pump();
        var canvas = (AnnotationCanvas)window.CanvasForTest;
        var tool = window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "ToolText");
        tool.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var p = canvas.TranslatePoint(canvas.Viewport.ToView(new SkiaSharp.SKPoint(60, 70)), window)!.Value;
        window.MouseDown(p, MouseButton.Left); window.MouseUp(p, MouseButton.Left); Pump();
        var input = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.AcceptsReturn);
        input.Text = "不提交的文字"; input.Focus();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "Escape"); Pump();
        Assert.False(input.IsVisible);
        Assert.Empty(window.ItemsForTest);
        Assert.False(window.Result.IsCompleted);
        window.Close();
    }

    [AvaloniaFact]
    public void Selected_style_changes_can_be_undone_and_redone()
    {
        var window = new AnnotationWindow(Frame(200, 120));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var (scale, x, y) = window.DisplayTransformForTest();
        var canvas = window.CanvasForTest!;
        Avalonia.Point At(int px, int py) => canvas.TranslatePoint(new Avalonia.Point(x + px * scale, y + py * scale), window)!.Value;
        window.MouseDown(At(20, 20), MouseButton.Left);
        window.MouseMove(At(100, 80));
        window.MouseUp(At(100, 80), MouseButton.Left);
        var document = window.DocumentForTest;
        Assert.Single(window.ItemsForTest);
        document.Select(0);
        document.ApplyStyle(0xFF0A84FFu, 4, 12);
        document.ApplyStyle(0xFF0A84FFu, 8, 12);
        Assert.Equal(0xFF0A84FFu, Assert.Single(window.ItemsForTest).Color);
        Assert.Equal(8f, Assert.Single(window.ItemsForTest).Thickness);
        document.Undo();
        Assert.Equal(4f, Assert.Single(window.ItemsForTest).Thickness);
        document.Undo();
        Assert.Equal(0xFFFF3B30u, Assert.Single(window.ItemsForTest).Color);
        document.Redo();
        Assert.Equal(0xFF0A84FFu, Assert.Single(window.ItemsForTest).Color);
        window.Close();
    }

    [AvaloniaFact]
    public void Window_builds_and_can_be_closed()
    {
        var window = new AnnotationWindow(Frame(60, 40));

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.Width > 0 && window.Height > 0, $"窗口尺寸异常：{window.Width}x{window.Height}");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Enter_completes_with_the_original_frame_when_nothing_was_drawn()
    {
        var source = Frame(40, 30);
        var window = new AnnotationWindow(source);

        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, "Enter");
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.Result.IsCompleted, "Enter 之后应该有结果");
        Assert.NotNull(window.Result.Result);

        var result = window.Result.Result!.Value;

        Assert.False(result.Recognize);
        Assert.Equal(source.Width, result.Frame.Width);
        Assert.Equal(source.Height, result.Frame.Height);
        Assert.Equal(source.Pixels[0], result.Frame.Pixels[0]);

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Escape_cancels_the_whole_capture()
    {
        var window = new AnnotationWindow(Frame(20, 20));

        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "Escape");
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.Result.IsCompleted, "Esc 之后应该有结果");
        Assert.Null(window.Result.Result);

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Space_with_empty_command_does_not_finish()
    {
        var source = Frame(24, 16);
        var window = new AnnotationWindow(source);

        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Focus();
        Dispatcher.UIThread.RunJobs();

        // 空闲时按空格**不能**完成/退出标注（用户明确要求）；它只在命令行有内容时当确认键
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.Result.IsCompleted, "空闲时按空格不应完成标注");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// 回归：点击坐标必须换算成**图片像素**，不能把窗口坐标（含工具栏高度）当成画布坐标。
    /// 曾经的 bug：用 <c>e.GetPosition(this)</c> 配合画布尺寸换算，标注整体往下偏了"工具栏高度"那么多。
    /// </summary>
    [AvaloniaFact]
    public void Click_coordinates_map_to_image_pixels_without_toolbar_offset()
    {
        var window = new AnnotationWindow(Frame(200, 120));

        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Focus();
        Dispatcher.UIThread.RunJobs();

        var (scale, offsetX, offsetY) = window.DisplayTransformForTest();
        var canvas = window.CanvasForTest;

        Assert.NotNull(canvas);

        // 画布是根 Grid 的直接子元素，它的 Bounds 位置就是相对窗口的偏移（工具栏在它上面 → Y > 0）
        var canvasOrigin = canvas!.TranslatePoint(default, window)!.Value;

        // 图片像素 → 画布 DIP → 窗口坐标
        Avalonia.Point ToWindow(int imageX, int imageY) => new(
            canvasOrigin.X + offsetX + (imageX * scale),
            canvasOrigin.Y + offsetY + (imageY * scale));

        var from = ToWindow(40, 30);
        var to = ToWindow(140, 90);

        window.MouseDown(from, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(to);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(to, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var item = Assert.Single(window.ItemsForTest);

        Assert.Equal(40, item.Points[0].X, 1);
        Assert.Equal(30, item.Points[0].Y, 1);
        Assert.Equal(140, item.Points[^1].X, 1);
        Assert.Equal(90, item.Points[^1].Y, 1);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// 「标注」设置子页要真的能加载并切出来（AXAML 里的绑定/资源写错了，这里会红）。
    /// </summary>
    [AvaloniaFact]
    public void Annotation_settings_page_loads_and_switches_to_its_section()
    {
        Kscan.App.ViewModels.SettingsViewModel vm;

        try
        {
            vm = new Kscan.App.ViewModels.SettingsViewModel();
        }
        catch (Exception ex)
        {
            // 无头环境里平台服务可能不可用；这不是页面的错，跳过而不是假装通过
            Assert.Skip($"无头环境无法构造 SettingsViewModel：{ex.GetType().Name}: {ex.Message}");
            return;
        }

        var page = new SettingsPage { DataContext = vm };
        var window = new Window { Content = page, Width = 900, Height = 700 };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        vm.SelectSectionCommand.Execute("annotation");
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.SectionAnnotation, "切到标注子页后 SectionAnnotation 应为 true");
        Assert.Null(typeof(Kscan.App.ViewModels.SettingsViewModel).GetProperty("AnnotationFont"));
        Assert.False(vm.SectionMultiScreen, "切到标注子页后多屏页应关闭");

        var comboBoxes = page.GetVisualDescendants().OfType<ComboBox>().Count();

        Assert.True(comboBoxes >= 2, $"标注页应有「线宽」「颜色」两个下拉，实际 {comboBoxes}");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static ImageFrame Frame(int width, int height)
    {
        var pixels = new byte[width * height * ImageFrame.BytesPerPixel];

        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 0x80;      // B
            pixels[index + 1] = 0x40;  // G
            pixels[index + 2] = 0x20;  // R
            pixels[index + 3] = 0xFF;  // A
        }

        return new ImageFrame(width, height, pixels);
    }
}
