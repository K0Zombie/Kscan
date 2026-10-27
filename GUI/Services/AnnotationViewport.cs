using Avalonia;
using SkiaSharp;

namespace Kscan.App.Services;

/// <summary>统一的图片像素 ↔ 画布 DIP 变换；不混入窗口坐标或设备缩放。</summary>
internal sealed class AnnotationViewport(int imageWidth, int imageHeight)
{
    public double Scale { get; private set; }
    public Point Offset { get; private set; }
    public Point ToView(SKPoint point) => new(point.X * Scale + Offset.X, point.Y * Scale + Offset.Y);
    public SKPoint ToImage(Point point, bool clamp = false)
    {
        var x = (point.X - Offset.X) / Math.Max(.001, Scale);
        var y = (point.Y - Offset.Y) / Math.Max(.001, Scale);
        return new SKPoint((float)(clamp ? Math.Clamp(x, 0, imageWidth) : x), (float)(clamp ? Math.Clamp(y, 0, imageHeight) : y));
    }
    public bool Contains(Point point)
    {
        var p = ToImage(point);
        return p.X >= 0 && p.Y >= 0 && p.X <= imageWidth && p.Y <= imageHeight;
    }
    public void Fit(Size size, bool actual = false, double deviceScale = 1)
    {
        if (size.Width < 1 || size.Height < 1) return;
        Scale = actual ? 1 / Math.Max(.01, deviceScale) : Math.Clamp(Math.Min((size.Width - 48) / imageWidth, (size.Height - 48) / imageHeight), .001, 1);
        Offset = new Point((size.Width - imageWidth * Scale) / 2, (size.Height - imageHeight * Scale) / 2);
        if (actual) Offset = new Point(Math.Round(Offset.X * deviceScale) / deviceScale, Math.Round(Offset.Y * deviceScale) / deviceScale);
    }
    public void Pan(Vector delta) => Offset += delta;
    public void Zoom(Point anchor, double factor)
    {
        var p = ToImage(anchor);
        Scale = Math.Clamp(Scale * factor, .01, 32);
        Offset = new Point(anchor.X - p.X * Scale, anchor.Y - p.Y * Scale);
    }
}
