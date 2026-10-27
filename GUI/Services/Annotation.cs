using SkiaSharp;

namespace Kscan.App.Services;

/// <summary>标注工具。</summary>
internal enum AnnotationTool
{
    /// <summary>矩形框。</summary>
    Rect,

    /// <summary>直线。</summary>
    Line,

    /// <summary>箭头。</summary>
    Arrow,

    /// <summary>文字（可定宽自动换行）。</summary>
    Text,

    /// <summary>马赛克（把一块区域像素化）。</summary>
    Mosaic,

    /// <summary>自由画笔。</summary>
    Pen,

    /// <summary>序号（①②③…自动递增）。</summary>
    Number,

    /// <summary>圆 / 椭圆。</summary>
    Circle,

    /// <summary>选择/编辑（Alt 的显式模式，不画东西）。</summary>
    Select,

    /// <summary>折线（多点连线，Enter/双击结束）。</summary>
    Polyline,

    /// <summary>尺寸标注（两点连线 + 长度数字）。</summary>
    Dimension,

    /// <summary>高亮笔（半透明粗笔刷）。</summary>
    Highlight,

    /// <summary>裁剪（导出时裁到该矩形；预览里压暗外部 + 虚线框）。</summary>
    Crop,

    /// <summary>聚光/暗角（把矩形外压暗）。</summary>
    Spotlight,

    /// <summary>局部放大（把矩形区域放大 2 倍画在旁边）。</summary>
    ZoomLens,
}

/// <summary>
/// 一条标注。<see cref="Points"/> 是<b>图片像素坐标</b>（左上角原点），
/// 单点工具（文字 / 序号）只用一个点。
/// </summary>
internal sealed class AnnotationItem
{
    public required AnnotationTool Tool { get; init; }

    public List<SKPoint> Points { get; init; } = [];

    public uint Color { get; set; } = 0xFFFF3B30;

    /// <summary>线宽（图片像素；文字用它算字号、序号用它算半径）。</summary>
    public float Thickness { get; set; } = 4;

    public string Text { get; init; } = string.Empty;

    public int Number { get; init; }

    /// <summary>马赛克的格子大小（图片像素）。</summary>
    public int Block { get; init; } = 12;

    /// <summary>
    /// 文字框宽度（图片像素）；<c>0</c> = 自动宽度（不换行）。
    /// 定宽时按宽度逐字断行，长文本不会一直往右延伸。
    /// </summary>
    public float TextWidth { get; set; }
    public float FontSize { get; init; }

    /// <summary>两个点工具的包围矩形（用于判定"太小不算一条"）。</summary>
    public SKRect Bounds()
    {
        if (Points.Count == 0)
        {
            return SKRect.Empty;
        }

        var left = Points.Min(point => point.X);
        var top = Points.Min(point => point.Y);
        var right = Points.Max(point => point.X);
        var bottom = Points.Max(point => point.Y);

        return new SKRect(left, top, right, bottom);
    }
}

/// <summary>
/// 把标注画到图上（预览与最终导出<b>同一条代码路径</b>，所见即所得）。
/// <para>
/// 纯 Skia 绘制，不依赖 Avalonia —— 这样能直接单测（"画了之后像素确实变了"）。
/// </para>
/// </summary>
internal static class AnnotationRenderer
{
    /// <summary>
    /// 标注用字体：优先<b>能显示中文</b>的字形。
    /// <para>
    /// <see cref="SKTypeface.Default"/> 在 Windows 上通常映射到不含 CJK 的字体 → 中文变方框/乱码（用户报的）。
    /// 这里用 <c>MatchCharacter('中')</c> 让系统挑一个含中文的字体；再退回雅黑/黑体/默认。
    /// </para>
    /// </summary>
    private static Lazy<SKTypeface> CjkTypeface = CreateTypeface();

    /// <summary>用户指定的标注字体（空 = 自动）。改它之后要 <see cref="ResetTypeface"/>。</summary>
    public static string FontFamily { get; set; } = "";

    /// <summary>当前标注字体（测试与设置页用）。</summary>
    public static SKTypeface Typeface => CjkTypeface.Value;

    /// <summary>字体设置变化后调用，让缓存失效重新解析。</summary>
    public static void ResetTypeface() => CjkTypeface = CreateTypeface();

    private static Lazy<SKTypeface> CreateTypeface() => new(() =>
    {
        // 用户指定的字体优先，但必须真的能显示中文；
        // 否则（字体不存在时 Skia 会返回默认字体，而不是 null）继续往下挑 CJK 字体。
        if (!string.IsNullOrWhiteSpace(FontFamily))
        {
            var custom = SKTypeface.FromFamilyName(FontFamily);

            if (custom is not null && custom.GetGlyph('中') != 0)
            {
                return custom;
            }
        }

        foreach (var candidate in new[] { "Microsoft YaHei", "微软雅黑", "SimHei", "Noto Sans CJK SC", "Source Han Sans SC", "PingFang SC" })
        {
            var face = SKTypeface.FromFamilyName(candidate);

            if (face is not null && face.FamilyName.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return face;
            }
        }

        return SKFontManager.Default.MatchCharacter('中')
               ?? SKTypeface.FromFamilyName("sans-serif")
               ?? SKTypeface.Default;
    });
    /// <summary>
    /// 渲染"当前视口"：把原图和标注画进一张视口大小的位图。
    /// <para>
    /// 参数都是<b>设备像素</b>：<paramref name="scale"/> = 一个图片像素占多少屏幕像素，
    /// <paramref name="offset"/> = 图片原点在视口里的位置。想清楚这一点很重要 ——
    /// 用 DIP 算进设备位图里，在 125%/140% 屏上就会"越往右下偏得越多"。
    /// </para>
    /// <para>
    /// 为什么不"渲染 1:1 再让 Avalonia 缩放"：放大时预览会糊，看不见自己画的边到底落在哪个像素上。
    /// 按视口重绘的代价只与视口像素数有关，与放大倍数无关。
    /// </para>
    /// </summary>
    public static SKBitmap RenderView(
        SKBitmap source,
        IReadOnlyList<AnnotationItem> items,
        int viewWidth,
        int viewHeight,
        float scale,
        SKPoint offset)
    {
        var target = new SKBitmap(
            Math.Max(1, viewWidth),
            Math.Max(1, viewHeight),
            SKColorType.Bgra8888,
            SKAlphaType.Unpremul);

        PaintView(source, items, target, scale, offset);
        return target;
    }

    /// <summary>绘制到可复用的视口缓冲，不在每次指针移动时分配整屏位图。</summary>
    public static void PaintView(SKBitmap source, IReadOnlyList<AnnotationItem> items, SKBitmap target, float scale, SKPoint offset, uint backgroundColor = 0xFF202020)
    {

        using var canvas = new SKCanvas(target);
        canvas.Clear(new SKColor(backgroundColor));
        canvas.Translate(offset.X, offset.Y);
        canvas.Scale(Math.Max(0.000001f, scale));
        canvas.ClipRect(SKRect.Create(source.Width, source.Height));

        canvas.DrawBitmap(source, 0, 0);

        foreach (var item in items)
        {
            DrawItem(canvas, source, item);
        }

    }

    /// <summary>渲染结果：先铺原图，再按顺序画标注；存在「裁剪」标注时，最终裁到它的矩形。</summary>
    public static SKBitmap Render(SKBitmap source, IReadOnlyList<AnnotationItem> items)
    {
        ArgumentNullException.ThrowIfNull(source);

        var target = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);

        using (var canvas = new SKCanvas(target))
        {
            canvas.DrawBitmap(source, 0, 0);

            foreach (var item in items)
            {
                // 裁剪框、暗幕仅用于预览；导出不把辅助边框烘焙进图片。
                if (item.Tool != AnnotationTool.Crop) DrawItem(canvas, source, item);
            }
        }

        // 文档只保留一个裁剪；直接调用渲染器传入多个时，以最后一次为准。
        var crop = items.LastOrDefault(item => item.Tool == AnnotationTool.Crop && item.Points.Count >= 2);

        if (crop is not null)
        {
            var rect = crop.Bounds();
            var x = Math.Clamp((int)Math.Floor(rect.Left), 0, source.Width);
            var y = Math.Clamp((int)Math.Floor(rect.Top), 0, source.Height);
            var w = Math.Max(0, Math.Clamp((int)Math.Ceiling(rect.Right), 0, source.Width) - x);
            var h = Math.Max(0, Math.Clamp((int)Math.Ceiling(rect.Bottom), 0, source.Height) - y);

            if (w > 0 && h > 0)
            {
                // 注意：cropped 要返回给调用方，**不能**用 using var（否则返回后立即被释放，读取直接崩）
                var cropped = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);

                using (var cropCanvas = new SKCanvas(cropped))
                {
                    cropCanvas.DrawBitmap(target, new SKRect(x, y, x + w, y + h), new SKRect(0, 0, w, h));
                }

                target.Dispose();
                return cropped;
            }
        }

        return target;
    }

    private static void DrawItem(SKCanvas canvas, SKBitmap source, AnnotationItem item)
    {
        var color = new SKColor(item.Color);

        using var paint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1, item.Thickness),
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };

        switch (item.Tool)
        {
            case AnnotationTool.Rect:
                if (item.Points.Count >= 2)
                {
                    var rect = item.Bounds();
                    canvas.DrawRect(rect, paint);
                }

                break;

            case AnnotationTool.Line:
                if (item.Points.Count >= 2)
                {
                    canvas.DrawLine(item.Points[0], item.Points[^1], paint);
                }

                break;

            case AnnotationTool.Circle:
                if (item.Points.Count >= 2)
                {
                    var box = item.Bounds();
                    canvas.DrawOval(box, paint);
                }

                break;

            case AnnotationTool.Arrow:
                if (item.Points.Count >= 2)
                {
                    DrawArrow(canvas, paint, item.Points[0], item.Points[^1], item.Thickness);
                }

                break;

            case AnnotationTool.Pen:
                if (item.Points.Count >= 2)
                {
                    using var path = new SKPath();
                    path.MoveTo(item.Points[0]);

                    for (var index = 1; index < item.Points.Count; index++)
                    {
                        path.LineTo(item.Points[index]);
                    }

                    canvas.DrawPath(path, paint);
                }

                break;

            case AnnotationTool.Mosaic:
                if (item.Points.Count >= 2)
                {
                    DrawMosaic(canvas, source, item.Bounds(), Math.Max(4, item.Block));
                }

                break;

            case AnnotationTool.Text:
                if (item.Points.Count >= 1 && item.Text.Length > 0)
                {
                    DrawText(canvas, paint, item);
                }

                break;

            case AnnotationTool.Number:
                if (item.Points.Count >= 1)
                {
                    DrawNumber(canvas, paint, item.Points[0], item.Thickness, item.Number);
                }

                break;

            case AnnotationTool.Polyline:
                if (item.Points.Count >= 2)
                {
                    using var path = new SKPath();
                    path.MoveTo(item.Points[0]);

                    for (var index = 1; index < item.Points.Count; index++)
                    {
                        path.LineTo(item.Points[index]);
                    }

                    canvas.DrawPath(path, paint);
                }

                break;

            case AnnotationTool.Highlight:
                if (item.Points.Count >= 2)
                {
                    using var hl = new SKPaint
                    {
                        Color = color.WithAlpha(0x50),
                        IsAntialias = true,
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = Math.Max(8, item.Thickness * 3),
                        StrokeCap = SKStrokeCap.Round,
                        StrokeJoin = SKStrokeJoin.Round,
                    };

                    using var path = new SKPath();
                    path.MoveTo(item.Points[0]);

                    for (var index = 1; index < item.Points.Count; index++)
                    {
                        path.LineTo(item.Points[index]);
                    }

                    canvas.DrawPath(path, hl);
                }

                break;

            case AnnotationTool.Dimension:
                if (item.Points.Count >= 2)
                {
                    DrawDimension(canvas, paint, item.Points[0], item.Points[^1]);
                }

                break;

            case AnnotationTool.Spotlight:
            case AnnotationTool.Crop:
                if (item.Points.Count >= 2)
                {
                    DimOutside(canvas, source, item.Bounds());

                    if (item.Tool is AnnotationTool.Crop)
                    {
                        using var dash = new SKPaint
                        {
                            Color = new SKColor(0xFF, 0xFF, 0xCC, 0x00),
                            IsAntialias = true,
                            Style = SKPaintStyle.Stroke,
                            StrokeWidth = 1.5f,
                            PathEffect = SKPathEffect.CreateDash([6, 4], 0),
                        };

                        canvas.DrawRect(item.Bounds(), dash);
                    }
                }

                break;

            case AnnotationTool.ZoomLens:
                if (item.Points.Count >= 2)
                {
                    DrawZoomLens(canvas, source, item.Bounds(), item.Thickness);
                }

                break;
        }
    }

    /// <summary>把矩形外压暗（聚光 / 裁剪预览共用）。</summary>
    private static void DimOutside(SKCanvas canvas, SKBitmap source, SKRect rect)
    {
        var left = Math.Max(0, rect.Left);
        var top = Math.Max(0, rect.Top);
        var right = Math.Min(source.Width, rect.Right);
        var bottom = Math.Min(source.Height, rect.Bottom);

        using var dim = new SKPaint { Color = new SKColor(0x00, 0x00, 0x00).WithAlpha(0xB0) };

        canvas.DrawRect(SKRect.Create(0, 0, source.Width, top), dim);
        canvas.DrawRect(SKRect.Create(0, bottom, source.Width, source.Height - bottom), dim);
        canvas.DrawRect(SKRect.Create(0, top, left, Math.Max(0, bottom - top)), dim);
        canvas.DrawRect(SKRect.Create(right, top, Math.Max(0, source.Width - right), Math.Max(0, bottom - top)), dim);
    }

    /// <summary>尺寸标注：两端小竖线 + 中间长度数字（图片像素）。</summary>
    private static void DrawDimension(SKCanvas canvas, SKPaint paint, SKPoint from, SKPoint to)
    {
        canvas.DrawLine(from, to, paint);

        var angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        var tick = Math.Max(8, paint.StrokeWidth * 3);

        using var tickPaint = new SKPaint { Color = paint.Color, IsAntialias = true, StrokeWidth = paint.StrokeWidth };

        foreach (var end in new[] { from, to })
        {
            var perpendicular = angle + (Math.PI / 2);
            var half = tick / 2f;

            canvas.DrawLine(
                end,
                new SKPoint((float)(end.X + (half * Math.Cos(perpendicular))), (float)(end.Y + (half * Math.Sin(perpendicular)))),
                tickPaint);

            canvas.DrawLine(
                end,
                new SKPoint((float)(end.X - (half * Math.Cos(perpendicular))), (float)(end.Y - (half * Math.Sin(perpendicular)))),
                tickPaint);
        }

        var length = (int)Math.Round(SKPoint.Distance(from, to));
        var text = $"{length}px";
        var mid = new SKPoint((from.X + to.X) / 2, (from.Y + to.Y) / 2);

        using var font = new SKFont(Typeface, Math.Max(12, paint.StrokeWidth * 3.5f));
        using var textPaint = new SKPaint { Color = paint.Color, IsAntialias = true, Style = SKPaintStyle.Fill };

        var width = font.MeasureText(text, textPaint);
        var metrics = font.Metrics;

        canvas.DrawText(text, mid.X - (width / 2), mid.Y - ((metrics.Ascent + metrics.Descent) / 2), font, textPaint);
    }

    /// <summary>局部放大：把源区域放大 2 倍画到原矩形右下侧，带边框。</summary>
    private static void DrawZoomLens(SKCanvas canvas, SKBitmap source, SKRect rect, float thickness)
    {
        const float zoom = 2f;

        var left = Math.Max(0, (int)rect.Left);
        var top = Math.Max(0, (int)rect.Top);
        var right = Math.Min(source.Width, (int)Math.Ceiling(rect.Right));
        var bottom = Math.Min(source.Height, (int)Math.Ceiling(rect.Bottom));

        if (right <= left || bottom <= top)
        {
            return;
        }

        var sw = right - left;
        var sh = bottom - top;
        var destX = rect.Right + 10;
        var destY = rect.Top;

        // 把源区域放大画到目标位置（放大镜用最近邻：逐像素放大，边缘锐利、坐标也好判）
        canvas.DrawBitmap(
            source,
            new SKRect(left, top, right, bottom),
            new SKRect(destX, destY, destX + (sw * zoom), destY + (sh * zoom)),
            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));

        using var border = new SKPaint
        {
            Color = new SKColor(0xFF, 0xFF, 0xCC, 0x00),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.5f, thickness * 0.8f),
        };

        canvas.DrawRect(new SKRect(destX, destY, destX + (sw * zoom), destY + (sh * zoom)), border);
    }

    /// <summary>
    /// 画文字：<c>Points[0]</c> 是文本框左上角；<see cref="AnnotationItem.TextWidth"/> &gt; 0 时按宽度逐字断行
    /// （长文本不会一直往右延伸），<c>\n</c> 也能显式换行。
    /// </summary>
    private static void DrawText(SKCanvas canvas, SKPaint paint, AnnotationItem item)
    {
        var size = item.FontSize > 0 ? item.FontSize : Math.Max(12, item.Thickness * 6);
        using var font = new SKFont(Typeface, size);
        paint.Style = SKPaintStyle.Fill;

        var metrics = font.Metrics;
        var lineHeight = Math.Max(1, -metrics.Ascent + metrics.Descent);
        var top = item.Points[0].Y;
        var left = item.Points[0].X;

        var lines = Wrap(item.Text, item.TextWidth, font, paint);
        var baseline = top - metrics.Ascent;

        foreach (var line in lines)
        {
            canvas.DrawText(line, left, baseline, font, paint);
            baseline += lineHeight;
        }
    }

    /// <summary>
    /// 量出文字块的实际范围（图片像素）：命中测试与"拖角点改宽"都要用。
    /// 只有一个点的文字项如果按 <see cref="AnnotationItem.Bounds"/> 算，会得到零面积矩形 ——
    /// 于是既选不中也改不了（用户报的"文本框没法拖动调整"）。
    /// </summary>
    internal static SKRect MeasureTextBlock(AnnotationItem item)
    {
        var size = item.FontSize > 0 ? item.FontSize : Math.Max(12, item.Thickness * 6);
        using var font = new SKFont(Typeface, size);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };

        var lines = Wrap(item.Text, item.TextWidth, font, paint);
        var metrics = font.Metrics;
        var lineHeight = Math.Max(1, -metrics.Ascent + metrics.Descent);

        var width = 0f;

        foreach (var line in lines)
        {
            width = Math.Max(width, font.MeasureText(line, paint));
        }

        width = Math.Max(width, item.TextWidth > 0 ? item.TextWidth : 1);

        var top = item.Points.Count > 0 ? item.Points[0].Y : 0;
        var left = item.Points.Count > 0 ? item.Points[0].X : 0;

        return new SKRect(left, top, left + width, top + (lineHeight * Math.Max(1, lines.Count)));
    }

    /// <summary>按宽度断行（逐字符，中文/英文都能断）；<paramref name="width"/> ≤ 0 时只按 <c>\n</c> 分行。</summary>
    internal static IReadOnlyList<string> Wrap(string text, float width, SKFont font, SKPaint paint)
    {
        var lines = new List<string>();

        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (width <= 0 || paragraph.Length == 0)
            {
                lines.Add(paragraph);
                continue;
            }

            var current = string.Empty;

            foreach (var character in paragraph)
            {
                var candidate = current + character;

                if (current.Length > 0 && font.MeasureText(candidate, paint) > width)
                {
                    lines.Add(current);
                    current = character.ToString();
                }
                else
                {
                    current = candidate;
                }
            }

            lines.Add(current);
        }

        return lines;
    }

    /// <summary>箭头：一条线 + 一个实心三角头（头部大小跟线宽走）。</summary>
    private static void DrawArrow(SKCanvas canvas, SKPaint paint, SKPoint from, SKPoint to, float thickness)
    {
        canvas.DrawLine(from, to, paint);

        var angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        var head = Math.Max(10, thickness * 4);
        var spread = Math.PI / 7;

        using var fill = new SKPaint { Color = paint.Color, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var path = new SKPath();
        path.MoveTo(to);
        path.LineTo(
            (float)(to.X - (head * Math.Cos(angle - spread))),
            (float)(to.Y - (head * Math.Sin(angle - spread))));
        path.LineTo(
            (float)(to.X - (head * Math.Cos(angle + spread))),
            (float)(to.Y - (head * Math.Sin(angle + spread))));
        path.Close();

        canvas.DrawPath(path, fill);
    }

    /// <summary>
    /// 马赛克：按格子大小把每格替换成"该格中心在原图上的颜色"。
    /// 采样的是<b>原图</b>，所以多条马赛克叠加也不会越叠越糊。
    /// </summary>
    private static void DrawMosaic(SKCanvas canvas, SKBitmap source, SKRect bounds, int block)
    {
        var left = Math.Max(0, (int)bounds.Left);
        var top = Math.Max(0, (int)bounds.Top);
        var right = Math.Min(source.Width, (int)Math.Ceiling(bounds.Right));
        var bottom = Math.Min(source.Height, (int)Math.Ceiling(bounds.Bottom));

        if (right <= left || bottom <= top)
        {
            return;
        }

        using var paint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };

        for (var y = top; y < bottom; y += block)
        {
            for (var x = left; x < right; x += block)
            {
                var sampleX = Math.Min(source.Width - 1, x + (block / 2));
                var sampleY = Math.Min(source.Height - 1, y + (block / 2));

                paint.Color = source.GetPixel(sampleX, sampleY);

                var width = Math.Min(block, right - x);
                var height = Math.Min(block, bottom - y);

                canvas.DrawRect(SKRect.Create(x, y, width, height), paint);
            }
        }
    }

    /// <summary>序号：实心圆 + 白字。</summary>
    private static void DrawNumber(SKCanvas canvas, SKPaint paint, SKPoint center, float thickness, int number)
    {
        var radius = Math.Max(12, thickness * 3.2f);

        using var fill = new SKPaint { Color = paint.Color, IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawCircle(center, radius, fill);

        var text = number.ToString();
        using var font = new SKFont(Typeface, radius * 1.15f);
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };

        var metrics = font.Metrics;
        var width = font.MeasureText(text, textPaint);
        var baseline = center.Y - ((metrics.Ascent + metrics.Descent) / 2);

        canvas.DrawText(text, center.X - (width / 2), baseline, font, textPaint);
    }
}
