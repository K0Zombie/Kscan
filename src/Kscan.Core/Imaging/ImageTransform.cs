using System.Runtime.InteropServices;
using SkiaSharp;

namespace Kscan.Core.Imaging;

/// <summary>
/// 贴图的旋转与镜像（像素级）。两种路径：
/// <list type="bullet">
/// <item>90° 的整数倍 + 镜像：<b>逐像素搬运</b>。没有插值、没有透明边，质量最好，也最快。</item>
/// <item>任意角度：Skia 绘制到更大的画布上，四角补透明。用于"按住 Alt 自由旋转"。</item>
/// </list>
/// </summary>
public static class ImageTransform
{
    /// <summary>
    /// 生成"旋转 + 镜像"后的新帧。参数为累计角度（顺时针为正）与是否水平/垂直镜像；
    /// 镜像先做、旋转后做。
    /// </summary>
    public static ImageFrame Apply(ImageFrame source, double degrees, bool mirrorHorizontal, bool mirrorVertical)
    {
        ArgumentNullException.ThrowIfNull(source);

        var normalized = Normalize(degrees);

        if (IsRightAngle(normalized))
        {
            return ApplyRightAngle(source, (int)Math.Round(normalized) % 360, mirrorHorizontal, mirrorVertical);
        }

        return ApplyArbitrary(source, (float)normalized, mirrorHorizontal, mirrorVertical);
    }

    /// <summary>角度归一化到 [0, 360)。</summary>
    public static double Normalize(double degrees)
    {
        var value = degrees % 360;
        return value < 0 ? value + 360 : value;
    }

    /// <summary>是不是 90° 的整数倍（浮点误差容忍 0.01°）。</summary>
    public static bool IsRightAngle(double degrees)
    {
        var normalized = Normalize(degrees);
        var remainder = normalized % 90;
        return Math.Min(remainder, 90 - remainder) < 0.01;
    }

    /// <summary>旋转 + 镜像后的外接矩形尺寸（同一套规则，界面用它算窗口大小）。</summary>
    public static (int Width, int Height) BoundsOf(int width, int height, double degrees)
    {
        var normalized = Normalize(degrees);
        var radians = normalized * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));

        return (
            Math.Max(1, (int)Math.Round(width * cos + height * sin)),
            Math.Max(1, (int)Math.Round(width * sin + height * cos)));
    }

    private static ImageFrame ApplyRightAngle(ImageFrame source, int degrees, bool mirrorH, bool mirrorV)
    {
        var width = source.Width;
        var height = source.Height;

        // 先镜像（宽高不变）
        var pixels = source.Pixels;
        if (mirrorH || mirrorV)
        {
            var flipped = new byte[width * height * ImageFrame.BytesPerPixel];

            for (var y = 0; y < height; y++)
            {
                var sourceRow = y * width * ImageFrame.BytesPerPixel;
                var targetY = mirrorV ? height - 1 - y : y;
                var targetRow = targetY * width * ImageFrame.BytesPerPixel;

                for (var x = 0; x < width; x++)
                {
                    var sourceX = mirrorH ? width - 1 - x : x;
                    Buffer.BlockCopy(
                        pixels,
                        sourceRow + sourceX * ImageFrame.BytesPerPixel,
                        flipped,
                        targetRow + x * ImageFrame.BytesPerPixel,
                        ImageFrame.BytesPerPixel);
                }
            }

            pixels = flipped;
        }

        if (degrees == 0)
        {
            return ReferenceEquals(pixels, source.Pixels) ? source : new ImageFrame(width, height, pixels);
        }

        // 顺时针旋转：90° → 宽高互换
        var rotatedWidth = degrees is 90 or 270 ? height : width;
        var rotatedHeight = degrees is 90 or 270 ? width : height;
        var target = new byte[rotatedWidth * rotatedHeight * ImageFrame.BytesPerPixel];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (tx, ty) = degrees switch
                {
                    90 => (height - 1 - y, x),
                    180 => (width - 1 - x, height - 1 - y),
                    270 => (y, width - 1 - x),
                    _ => (x, y),
                };

                Buffer.BlockCopy(
                    pixels,
                    (y * width + x) * ImageFrame.BytesPerPixel,
                    target,
                    (ty * rotatedWidth + tx) * ImageFrame.BytesPerPixel,
                    ImageFrame.BytesPerPixel);
            }
        }

        return new ImageFrame(rotatedWidth, rotatedHeight, target);
    }

    private static ImageFrame ApplyArbitrary(ImageFrame source, float degrees, bool mirrorH, bool mirrorV)
    {
        // 先镜像出一份（Skia 里也能做，但逐像素翻转更可控、也不引入额外插值）
        var mirrored = (mirrorH || mirrorV)
            ? ApplyRightAngle(source, 0, mirrorH, mirrorV)
            : source;

        var bounds = BoundsOf(mirrored.Width, mirrored.Height, degrees);
        var sourceInfo = new SKImageInfo(mirrored.Width, mirrored.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var targetInfo = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul);

        var sourceBytes = mirrored.Width * mirrored.Height * ImageFrame.BytesPerPixel;
        var targetBytes = bounds.Width * bounds.Height * ImageFrame.BytesPerPixel;

        var sourceBuffer = Marshal.AllocHGlobal(sourceBytes);
        var targetBuffer = Marshal.AllocHGlobal(targetBytes);

        try
        {
            Marshal.Copy(mirrored.Pixels, 0, sourceBuffer, sourceBytes);
            // 目标先清零 = 全透明（旋转后四角留白）
            for (var i = 0; i < targetBytes; i++)
            {
                Marshal.WriteByte(targetBuffer, i, 0);
            }

            using (var sourceBitmap = new SKBitmap())
            using (var targetBitmap = new SKBitmap())
            {
                sourceBitmap.InstallPixels(sourceInfo, sourceBuffer, sourceInfo.RowBytes);
                targetBitmap.InstallPixels(targetInfo, targetBuffer, targetInfo.RowBytes);

                using var canvas = new SKCanvas(targetBitmap);
                canvas.Translate(bounds.Width / 2f, bounds.Height / 2f);
                canvas.RotateDegrees(degrees);
                canvas.Translate(-mirrored.Width / 2f, -mirrored.Height / 2f);

                using var paint = new SKPaint { IsAntialias = true };
                // SkiaSharp 4 起用 SKSamplingOptions 描述重采样（SKFilterQuality 已移除）
                var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
                canvas.DrawBitmap(sourceBitmap, 0, 0, sampling, paint);
                canvas.Flush();
            }

            // 预乘 → 直通 alpha（ImageFrame 的约定是 straight alpha）
            var pixels = new byte[targetBytes];
            Marshal.Copy(targetBuffer, pixels, 0, targetBytes);

            for (var i = 0; i < targetBytes; i += ImageFrame.BytesPerPixel)
            {
                var alpha = pixels[i + 3];

                if (alpha is 0 or 255)
                {
                    if (alpha == 0)
                    {
                        pixels[i] = 0;
                        pixels[i + 1] = 0;
                        pixels[i + 2] = 0;
                    }

                    continue;
                }

                pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / alpha);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / alpha);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / alpha);
            }

            return new ImageFrame(bounds.Width, bounds.Height, pixels);
        }
        finally
        {
            Marshal.FreeHGlobal(sourceBuffer);
            Marshal.FreeHGlobal(targetBuffer);
        }
    }
}
