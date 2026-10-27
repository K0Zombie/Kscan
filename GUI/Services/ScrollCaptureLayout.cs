using Avalonia;

namespace Kscan.App.Services;

internal static class ScrollCaptureLayout
{
    public const int ToolbarWidth = 520;
    public const int ToolbarHeight = 112;

    /// <summary>工具窗必须完全在采集区外；同屏上下优先，再尝试两侧和其他屏。</summary>
    public static PixelRect? Place(PixelRect region, IEnumerable<(PixelRect WorkArea, double Scaling)> screens)
    {
        foreach (var (work, scale) in screens.OrderByDescending(x => Contains(x.WorkArea, region)))
        {
            if (scale <= 0 || !double.IsFinite(scale)) continue;
            var w = (int)Math.Ceiling(ToolbarWidth * scale);
            var h = (int)Math.Ceiling(ToolbarHeight * scale);
            var gap = (int)Math.Ceiling(10 * scale);
            if (work.Width < w + gap * 2 || work.Height < h + gap * 2) continue;
            var x = Math.Clamp(region.Right - w, work.X + gap, work.Right - w - gap);
            var y = Math.Clamp(region.Y, work.Y + gap, work.Bottom - h - gap);
            var choices = new[]
            {
                new PixelRect(x, region.Bottom + gap, w, h),
                new PixelRect(x, region.Y - h - gap, w, h),
                new PixelRect(region.Right + gap, y, w, h),
                new PixelRect(region.X - w - gap, y, w, h),
                new PixelRect(work.X + gap, work.Y + gap, w, h),
            };
            foreach (var candidate in choices)
                if (Contains(work, candidate) && !Intersects(region, candidate)) return candidate;
        }
        return null;
    }

    public static bool Contains(PixelRect outer, PixelRect inner) => inner.X >= outer.X && inner.Y >= outer.Y && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    /// <summary>实时预览优先放在选区左右的空白区，空间不足再找上下或其他屏。</summary>
    public static PixelRect? PlacePreview(PixelRect region, PixelRect toolbar, IEnumerable<(PixelRect WorkArea, double Scaling)> screens)
    {
        foreach (var (work, scale) in screens.OrderByDescending(x => Contains(x.WorkArea, region)))
        {
            if (scale <= 0 || !double.IsFinite(scale)) continue;
            var gap = (int)Math.Ceiling(10 * scale);
            if (work.Width <= gap * 2 || work.Height <= gap * 2) continue;
            var safe = new PixelRect(work.X + gap, work.Y + gap, work.Width - gap * 2, work.Height - gap * 2);
            var candidates = FreeAround(safe, Expand(region, gap))
                .SelectMany(piece => FreeAround(piece, Expand(toolbar, gap)))
                .Where(piece => piece.Width >= 220 * scale && piece.Height >= 240 * scale)
                .Select(piece =>
                {
                    var w = Math.Min(piece.Width, (int)Math.Ceiling(480 * scale));
                    var h = Math.Min(piece.Height, (int)Math.Ceiling(900 * scale));
                    var x = piece.Right <= region.X ? piece.Right - w : piece.X;
                    var y = Math.Clamp(region.Y, piece.Y, piece.Bottom - h);
                    return new PixelRect(x, y, w, h);
                })
                .OrderByDescending(piece => piece.Right <= region.X || piece.X >= region.Right)
                .ThenByDescending(piece => (long)piece.Width * piece.Height).ToArray();
            if (candidates.Length > 0) return candidates[0];
        }
        return null;
    }

    private static PixelRect Expand(PixelRect rect, int gap) => new(rect.X - gap, rect.Y - gap, rect.Width + gap * 2, rect.Height + gap * 2);

    private static IEnumerable<PixelRect> FreeAround(PixelRect space, PixelRect blocked)
    {
        var hole = space.Intersect(blocked);
        if (hole.Width <= 0 || hole.Height <= 0) { yield return space; yield break; }
        var pieces = new[]
        {
            new PixelRect(space.X, space.Y, hole.X - space.X, space.Height),
            new PixelRect(hole.Right, space.Y, space.Right - hole.Right, space.Height),
            new PixelRect(space.X, space.Y, space.Width, hole.Y - space.Y),
            new PixelRect(space.X, hole.Bottom, space.Width, space.Bottom - hole.Bottom),
        };
        foreach (var piece in pieces) if (piece.Width > 0 && piece.Height > 0) yield return piece;
    }
    private static bool Intersects(PixelRect a, PixelRect b) => a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;
}
