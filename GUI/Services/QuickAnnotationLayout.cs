using Avalonia;

namespace Kscan.App.Services;

/// <summary>全部用物理像素判断原位编辑是否放得下，跨屏和大图自然回退独立编辑。</summary>
internal sealed record QuickAnnotationLayout(PixelRect Monitor, PixelRect WorkArea, PixelRect Image, PixelRect Toolbar, double Scaling)
{
    public const double MaxImageFraction = .80;
    public const int ToolbarWidthDip = 720;
    public const int ToolbarHeightDip = 112;

    public static QuickAnnotationLayout? TryCreate(PixelRect image, PixelRect monitor, PixelRect workArea, double scaling)
    {
        if (scaling <= 0 || !double.IsFinite(scaling) || image.Width <= 0 || image.Height <= 0) return null;
        if (!Contains(workArea, image) || !Contains(monitor, image)) return null;
        if (image.Width > workArea.Width * MaxImageFraction || image.Height > workArea.Height * MaxImageFraction) return null;
        var gap = (int)Math.Ceiling(10 * scaling);
        if (workArea.Width < 640 * scaling + gap * 2) return null;
        var width = Math.Min((int)Math.Ceiling(ToolbarWidthDip * scaling), workArea.Width - gap * 2);
        var height = (int)Math.Ceiling(ToolbarHeightDip * scaling);
        var x = Math.Clamp(image.Right - width, workArea.X + gap, workArea.Right - width - gap);
        int y;
        if (image.Bottom + gap + height <= workArea.Bottom - gap) y = image.Bottom + gap;
        else if (image.Y - gap - height >= workArea.Y + gap) y = image.Y - gap - height;
        else return null;
        return new QuickAnnotationLayout(monitor, workArea, image, new PixelRect(x, y, width, height), scaling);
    }

    private static bool Contains(PixelRect outer, PixelRect inner) => inner.X >= outer.X && inner.Y >= outer.Y && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
}
