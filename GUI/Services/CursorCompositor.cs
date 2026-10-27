using Kscan.Core.Imaging;

namespace Kscan.App.Services;

/// <summary>
/// 把光标图像按 alpha 混合到截图里（"截出来的图带光标"）。
/// </summary>
internal static class CursorCompositor
{
    /// <summary>
    /// 在 <paramref name="target"/> 的 <paramref name="left"/>, <paramref name="top"/> 处画 <paramref name="cursor"/>。
    /// 返回<b>新帧</b>：<see cref="ImageFrame.Crop"/> 在区域等于整图时会原样返回同一个实例，
    /// 直接改它的像素会污染整张桌面截图。
    /// </summary>
    public static ImageFrame Draw(ImageFrame target, ImageFrame cursor, int left, int top)
    {
        var pixels = (byte[])target.Pixels.Clone();
        var drawn = false;

        for (var y = 0; y < cursor.Height; y++)
        {
            var targetY = top + y;
            if (targetY < 0 || targetY >= target.Height)
            {
                continue;
            }

            for (var x = 0; x < cursor.Width; x++)
            {
                var targetX = left + x;
                if (targetX < 0 || targetX >= target.Width)
                {
                    continue;
                }

                var sourceIndex = (y * cursor.Width + x) * ImageFrame.BytesPerPixel;
                var alpha = cursor.Pixels[sourceIndex + 3];

                if (alpha == 0)
                {
                    continue;
                }

                var targetIndex = (targetY * target.Width + targetX) * ImageFrame.BytesPerPixel;
                var ratio = alpha / 255.0;

                pixels[targetIndex] = Blend(cursor.Pixels[sourceIndex], pixels[targetIndex], ratio);
                pixels[targetIndex + 1] = Blend(cursor.Pixels[sourceIndex + 1], pixels[targetIndex + 1], ratio);
                pixels[targetIndex + 2] = Blend(cursor.Pixels[sourceIndex + 2], pixels[targetIndex + 2], ratio);
                pixels[targetIndex + 3] = 255;
                drawn = true;
            }
        }

        return drawn ? new ImageFrame(target.Width, target.Height, pixels) : target;
    }

    private static byte Blend(byte source, byte destination, double ratio) =>
        (byte)Math.Clamp(source * ratio + destination * (1 - ratio), 0, 255);
}
