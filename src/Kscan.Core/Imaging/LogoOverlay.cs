namespace Kscan.Core.Imaging;

/// <summary>
/// 在条码图片中心叠加 Logo。纯像素运算（自写双线性缩放），不依赖 SkiaSharp 的缩放 API，
/// 因此行为在各平台一致、可单测。
/// <para>
/// 默认带一圈白色衬底：二维码的纠错机制能容忍中心遮挡（建议纠错级别 M 以上），
/// 衬底能避免 Logo 边缘与模块糊在一起而影响识别。
/// </para>
/// </summary>
public static class LogoOverlay
{
    /// <summary>默认 Logo 宽度占条码宽度的比例。</summary>
    public const double DefaultRatio = 0.22;

    /// <summary>默认衬底占 Logo 尺寸的比例（四边各留）。</summary>
    public const double DefaultPaddingRatio = 0.08;

    public static ImageFrame Apply(
        ImageFrame barcode,
        ImageFrame logo,
        double ratio = DefaultRatio,
        double paddingRatio = DefaultPaddingRatio,
        uint backgroundArgb = 0xFFFFFFFF)
    {
        ArgumentNullException.ThrowIfNull(barcode);
        ArgumentNullException.ThrowIfNull(logo);
        ArgumentOutOfRangeException.ThrowIfLessThan(ratio, 0.02);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ratio, 0.6);

        var logoWidth = Math.Max(1, (int)Math.Round(barcode.Width * ratio));
        var logoHeight = Math.Max(1, (int)Math.Round(logo.Height * (double)logoWidth / logo.Width));

        var resized = Resize(logo, logoWidth, logoHeight);

        var pad = Math.Max(0, (int)Math.Round(Math.Min(logoWidth, logoHeight) * paddingRatio));
        var boxWidth = logoWidth + (pad * 2);
        var boxHeight = logoHeight + (pad * 2);
        var boxLeft = (barcode.Width - boxWidth) / 2;
        var boxTop = (barcode.Height - boxHeight) / 2;

        var output = new ImageFrame(barcode.Width, barcode.Height, (byte[])barcode.Pixels.Clone());

        FillRect(output, boxLeft, boxTop, boxWidth, boxHeight, backgroundArgb);
        Blit(output, resized, boxLeft + pad, boxTop + pad);

        return output;
    }

    /// <summary>双线性缩放（对 BGRA 四通道分别插值）。</summary>
    private static ImageFrame Resize(ImageFrame source, int width, int height)
    {
        var destination = new byte[width * height * ImageFrame.BytesPerPixel];
        var scaleX = (double)source.Width / width;
        var scaleY = (double)source.Height / height;

        for (var y = 0; y < height; y++)
        {
            var sourceY = ((y + 0.5) * scaleY) - 0.5;
            var y0 = Math.Clamp((int)Math.Floor(sourceY), 0, source.Height - 1);
            var y1 = Math.Clamp(y0 + 1, 0, source.Height - 1);
            var wy = Math.Clamp(sourceY - y0, 0, 1);

            for (var x = 0; x < width; x++)
            {
                var sourceX = ((x + 0.5) * scaleX) - 0.5;
                var x0 = Math.Clamp((int)Math.Floor(sourceX), 0, source.Width - 1);
                var x1 = Math.Clamp(x0 + 1, 0, source.Width - 1);
                var wx = Math.Clamp(sourceX - x0, 0, 1);

                var offset00 = ((y0 * source.Width) + x0) * ImageFrame.BytesPerPixel;
                var offset10 = ((y0 * source.Width) + x1) * ImageFrame.BytesPerPixel;
                var offset01 = ((y1 * source.Width) + x0) * ImageFrame.BytesPerPixel;
                var offset11 = ((y1 * source.Width) + x1) * ImageFrame.BytesPerPixel;
                var target = ((y * width) + x) * ImageFrame.BytesPerPixel;

                for (var channel = 0; channel < ImageFrame.BytesPerPixel; channel++)
                {
                    var top = (source.Pixels[offset00 + channel] * (1 - wx)) + (source.Pixels[offset10 + channel] * wx);
                    var bottom = (source.Pixels[offset01 + channel] * (1 - wx)) + (source.Pixels[offset11 + channel] * wx);
                    destination[target + channel] = (byte)Math.Clamp((top * (1 - wy)) + (bottom * wy), 0, 255);
                }
            }
        }

        return new ImageFrame(width, height, destination);
    }

    private static void FillRect(ImageFrame frame, int left, int top, int width, int height, uint argb)
    {
        var b = (byte)(argb & 0xFF);
        var g = (byte)((argb >> 8) & 0xFF);
        var r = (byte)((argb >> 16) & 0xFF);
        var a = (byte)((argb >> 24) & 0xFF);

        var maxY = Math.Min(top + height, frame.Height);
        var maxX = Math.Min(left + width, frame.Width);

        for (var y = Math.Max(0, top); y < maxY; y++)
        {
            var row = y * frame.Width;
            for (var x = Math.Max(0, left); x < maxX; x++)
            {
                var i = (row + x) * ImageFrame.BytesPerPixel;
                frame.Pixels[i] = b;
                frame.Pixels[i + 1] = g;
                frame.Pixels[i + 2] = r;
                frame.Pixels[i + 3] = a;
            }
        }
    }

    /// <summary>把 <paramref name="patch"/> 逐像素贴到 <paramref name="frame"/>（不做混合，Logo 自带 alpha 时按 alpha 合成）。</summary>
    private static void Blit(ImageFrame frame, ImageFrame patch, int left, int top)
    {
        for (var y = 0; y < patch.Height; y++)
        {
            var targetY = top + y;
            if (targetY < 0 || targetY >= frame.Height)
            {
                continue;
            }

            for (var x = 0; x < patch.Width; x++)
            {
                var targetX = left + x;
                if (targetX < 0 || targetX >= frame.Width)
                {
                    continue;
                }

                var source = ((y * patch.Width) + x) * ImageFrame.BytesPerPixel;
                var target = ((targetY * frame.Width) + targetX) * ImageFrame.BytesPerPixel;

                var alpha = patch.Pixels[source + 3];
                if (alpha == 255)
                {
                    frame.Pixels[target] = patch.Pixels[source];
                    frame.Pixels[target + 1] = patch.Pixels[source + 1];
                    frame.Pixels[target + 2] = patch.Pixels[source + 2];
                    frame.Pixels[target + 3] = 255;
                }
                else if (alpha > 0)
                {
                    var a = alpha / 255.0;
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var blended = (patch.Pixels[source + channel] * a) + (frame.Pixels[target + channel] * (1 - a));
                        frame.Pixels[target + channel] = (byte)Math.Clamp(blended, 0, 255);
                    }

                    frame.Pixels[target + 3] = 255;
                }
            }
        }
    }
}
