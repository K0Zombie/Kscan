namespace Kscan.Core.Imaging;

/// <summary>图像里的一个像素矩形（左上原点）。</summary>
public readonly record struct ImageBounds(int X, int Y, int Width, int Height)
{
    public static ImageBounds Empty => new(0, 0, 0, 0);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public int Right => X + Width - 1;

    public int Bottom => Y + Height - 1;
}

/// <summary>
/// 定格光标标记的几何：算出光标位图里**真正看得见**的那块。
/// <para>
/// 光标位图通常比图形本身大一圈（32×32 的箭头，图形只有 12×19，其余是透明边）。
/// 虚线框如果按位图尺寸描，就会得到一个"比光标大很多"的框 —— 看起来就是"框和光标大小对不上"。
/// 所以这里按 alpha 求可见范围，框贴着图形描。
/// </para>
/// </summary>
public static class CursorMarkerGeometry
{
    /// <summary>alpha 超过它才算可见；太低的只是抗锯齿毛边，算进去会让框忽大忽小。</summary>
    public const byte VisibleAlphaThreshold = 8;

    /// <summary>光标图像里可见像素的外接矩形；整张全透明时返回 <see cref="ImageBounds.Empty"/>。</summary>
    public static ImageBounds VisibleBounds(ImageFrame frame) =>
        VisibleBounds(frame, VisibleAlphaThreshold);

    /// <summary>同 <see cref="VisibleBounds(ImageFrame)"/>，可指定 alpha 阈值（测试用）。</summary>
    public static ImageBounds VisibleBounds(ImageFrame frame, byte alphaThreshold)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < frame.Height; y++)
        {
            var row = y * frame.Width * ImageFrame.BytesPerPixel;

            for (var x = 0; x < frame.Width; x++)
            {
                if (frame.Pixels[row + (x * ImageFrame.BytesPerPixel) + 3] <= alphaThreshold)
                {
                    continue;
                }

                if (x < minX)
                {
                    minX = x;
                }

                if (y < minY)
                {
                    minY = y;
                }

                if (x > maxX)
                {
                    maxX = x;
                }

                if (y > maxY)
                {
                    maxY = y;
                }
            }
        }

        return maxX < 0
            ? ImageBounds.Empty
            : new ImageBounds(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
