namespace Kscan.Core.Imaging;

/// <summary>要保留的矩形（屏幕物理像素）。Core 不引用 Avalonia，所以用它自己的矩形类型。</summary>
public readonly record struct KeepRect(int X, int Y, int Width, int Height);

/// <summary>
/// 把"不属于任何显示器"的像素清成全透明。
/// <para>
/// 跨屏截图是整块虚拟桌面的一次 BitBlt，显示器之间的空档（上下错开、围出的空洞）里
/// 系统给的是黑色 —— 那并不是屏幕上真实存在的画面。这里按显示器矩形把空档清成 alpha=0，
/// 拼出来的 PNG 才是"只有真实存在的区域"。
/// </para>
/// </summary>
public static class FrameMask
{
    /// <summary>
    /// 就地把 <paramref name="frame"/> 里不被 <paramref name="keepers"/> 覆盖的像素清成透明。
    /// </summary>
    /// <param name="frame">要处理的图（会被就地修改）。</param>
    /// <param name="originX">这张图左上角的屏幕物理 X。</param>
    /// <param name="originY">这张图左上角的屏幕物理 Y。</param>
    /// <param name="keepers">要保留的矩形（显示器），屏幕物理坐标。</param>
    /// <param name="transparent">
    /// <c>true</c> = 空档清成透明（alpha=0）；<c>false</c> = 填成不透明黑。
    /// </param>
    public static void ClearOutside(
        ImageFrame frame,
        int originX,
        int originY,
        IReadOnlyList<KeepRect> keepers,
        bool transparent = true)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(keepers);

        if (keepers.Count == 0)
        {
            return;
        }

        var pixels = frame.Pixels;
        var bytesPerPixel = ImageFrame.BytesPerPixel;

        // 每一行先算出"被显示器覆盖的列区间"，再把这些区间之外的部分清零。
        // 逐行处理而不是逐像素：空档通常只占很小一部分，整行扫一遍即可。
        var spans = new List<(int Start, int End)>();

        for (var row = 0; row < frame.Height; row++)
        {
            var screenY = originY + row;
            spans.Clear();

            foreach (var keeper in keepers)
            {
                if (screenY < keeper.Y || screenY >= keeper.Y + keeper.Height)
                {
                    continue;
                }

                var start = Math.Max(0, keeper.X - originX);
                var end = Math.Min(frame.Width, keeper.X + keeper.Width - originX);

                if (end > start)
                {
                    spans.Add((start, end));
                }
            }

            if (spans.Count == 0)
            {
                // 整行都没有显示器覆盖
                Fill(pixels, row * frame.Stride, frame.Stride, transparent);
                continue;
            }

            spans.Sort((a, b) => a.Start.CompareTo(b.Start));

            var cursor = 0;
            var rowOffset = row * frame.Stride;

            foreach (var (start, end) in spans)
            {
                if (start > cursor)
                {
                    Fill(pixels, rowOffset + (cursor * bytesPerPixel), (start - cursor) * bytesPerPixel, transparent);
                }

                cursor = Math.Max(cursor, end);
            }

            if (cursor < frame.Width)
            {
                Fill(pixels, rowOffset + (cursor * bytesPerPixel), (frame.Width - cursor) * bytesPerPixel, transparent);
            }
        }
    }

    /// <summary>透明 = 全清零；黑色 = BGR 为 0、alpha 为 255。</summary>
    private static void Fill(byte[] pixels, int offset, int length, bool transparent)
    {
        if (length <= 0)
        {
            return;
        }

        Array.Clear(pixels, offset, length);

        if (transparent)
        {
            return;
        }

        // BGRA：每 4 字节的最后一个（alpha）设成 255，其余已经是 0
        for (var i = offset + 3; i < offset + length; i += 4)
        {
            pixels[i] = 255;
        }
    }
}
