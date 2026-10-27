using Avalonia;

namespace Kscan.App.Services;

internal static class PinDropPlacement
{
    public static Point GrabFraction(Point point, Size thumbnail, PixelSize image)
    {
        var scale = Math.Min(thumbnail.Width / Math.Max(1, image.Width), thumbnail.Height / Math.Max(1, image.Height));
        if (scale <= 0) return new(.5, .5);
        var width = image.Width * scale;
        var height = image.Height * scale;
        return new(Math.Clamp((point.X - (thumbnail.Width - width) / 2) / width, 0, 1),
            Math.Clamp((point.Y - (thumbnail.Height - height) / 2) / height, 0, 1));
    }

    public static PixelRect At(PixelPoint drop, PixelSize size, Point grab) => new(
        drop.X - (int)Math.Round(size.Width * grab.X), drop.Y - (int)Math.Round(size.Height * grab.Y), size.Width, size.Height);
}
