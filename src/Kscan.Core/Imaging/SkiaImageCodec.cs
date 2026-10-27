using System.Runtime.InteropServices;
using SkiaSharp;

namespace Kscan.Core.Imaging;

/// <summary>
/// 基于 SkiaSharp 的图片编解码（PNG/JPEG/WebP/BMP...），跨平台（Windows/Linux/macOS）。
/// 所有输出统一为 BGRA32 straight-alpha 的 <see cref="ImageFrame"/>。
/// </summary>
public static class SkiaImageCodec
{
    public static ImageFrame Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return FromEncoded(File.ReadAllBytes(path));
    }

    public static ImageFrame FromEncoded(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return FromEncoded(buffer.ToArray());
    }

    public static ImageFrame FromEncoded(byte[] encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);

        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("无法解码图片：格式不受支持或数据已损坏。");

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);

        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new InvalidDataException($"图片解码失败：{result}。");
        }

        var frame = FromPixelBuffer(bitmap.GetPixels(), info.Width, info.Height);
        return ApplyEncodedOrigin(frame, codec.EncodedOrigin);
    }

    public static SKBitmap ToSKBitmap(ImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        Marshal.Copy(frame.Pixels, 0, bitmap.GetPixels(), frame.Pixels.Length);
        return bitmap;
    }

    public static byte[] EncodePng(ImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        using var bitmap = ToSKBitmap(frame);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static void SavePng(ImageFrame frame, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllBytes(path, EncodePng(frame));
    }

    /// <summary>剪贴板临时 PNG：无损快速压缩，直接读取源像素，避免整张图的额外拷贝。</summary>
    public static byte[] EncodePngFast(ImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var pinned = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var pixels = new SKPixmap(info, pinned.AddrOfPinnedObject(), frame.Stride);
            using var encoded = pixels.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.Sub | SKPngEncoderFilterFlags.Up, 1))
                ?? throw new InvalidDataException("PNG 编码失败。");
            return encoded.ToArray();
        }
        finally { pinned.Free(); }
    }

    private static ImageFrame FromPixelBuffer(IntPtr source, int width, int height)
    {
        var bytes = new byte[width * height * ImageFrame.BytesPerPixel];
        Marshal.Copy(source, bytes, 0, bytes.Length);
        return new ImageFrame(width, height, bytes);
    }

    /// <summary>把 EXIF 方向（手机拍照常见）应用到像素上，避免旋转过的码识别不出来。</summary>
    private static ImageFrame ApplyEncodedOrigin(ImageFrame frame, SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.TopRight or
        SKEncodedOrigin.BottomRight or
        SKEncodedOrigin.BottomLeft or
        SKEncodedOrigin.LeftTop or
        SKEncodedOrigin.RightTop or
        SKEncodedOrigin.RightBottom or
        SKEncodedOrigin.LeftBottom => Remap(frame, origin),
        _ => frame,
    };

    private static ImageFrame Remap(ImageFrame src, SKEncodedOrigin origin)
    {
        var swapAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        var dstWidth = swapAxes ? src.Height : src.Width;
        var dstHeight = swapAxes ? src.Width : src.Height;
        var dst = new byte[dstWidth * dstHeight * ImageFrame.BytesPerPixel];

        for (var dy = 0; dy < dstHeight; dy++)
        {
            for (var dx = 0; dx < dstWidth; dx++)
            {
                var (sx, sy) = MapSource(origin, dx, dy, src.Width, src.Height);
                var srcOffset = ((sy * src.Width) + sx) * ImageFrame.BytesPerPixel;
                var dstOffset = ((dy * dstWidth) + dx) * ImageFrame.BytesPerPixel;
                src.Pixels.AsSpan(srcOffset, ImageFrame.BytesPerPixel)
                    .CopyTo(dst.AsSpan(dstOffset, ImageFrame.BytesPerPixel));
            }
        }

        return new ImageFrame(dstWidth, dstHeight, dst);
    }

    private static (int X, int Y) MapSource(SKEncodedOrigin origin, int dx, int dy, int width, int height) => origin switch
    {
        SKEncodedOrigin.TopRight => (width - 1 - dx, dy),
        SKEncodedOrigin.BottomRight => (width - 1 - dx, height - 1 - dy),
        SKEncodedOrigin.BottomLeft => (dx, height - 1 - dy),
        SKEncodedOrigin.LeftTop => (dy, dx),
        SKEncodedOrigin.RightTop => (dy, height - 1 - dx),
        SKEncodedOrigin.RightBottom => (width - 1 - dy, height - 1 - dx),
        SKEncodedOrigin.LeftBottom => (width - 1 - dy, dx),
        _ => (dx, dy),
    };
}
