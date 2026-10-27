using Avalonia;

namespace Kscan.App.Services;

internal static class RecordingSelectionGeometry
{
    internal static PixelRect Drag(PixelPoint start, PixelPoint end, PixelRect desktop)
    {
        var left = Math.Min(start.X, end.X); var top = Math.Min(start.Y, end.Y);
        return new PixelRect(left, top, Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y)).Intersect(desktop);
    }

    internal static bool IsDrag(PixelPoint start, PixelPoint end) => Math.Abs(start.X - end.X) >= 4 || Math.Abs(start.Y - end.Y) >= 4;
}
