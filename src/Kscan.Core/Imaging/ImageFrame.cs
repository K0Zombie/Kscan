namespace Kscan.Core.Imaging;

/// <summary>
/// 与 UI 框架无关的位图帧。像素为 BGRA32、straight alpha、行优先，
/// 缓冲区长度必须 ≥ <c>Width * Height * 4</c>。
/// </summary>
public sealed class ImageFrame
{
    public const int BytesPerPixel = 4;

    public ImageFrame(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);

        var expected = (long)width * height * BytesPerPixel;
        if (pixels.LongLength < expected)
        {
            throw new ArgumentException(
                $"像素缓冲区太小：需要 {expected} 字节，实际 {pixels.LongLength} 字节。", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>BGRA32 像素数据（行优先）。</summary>
    public byte[] Pixels { get; }

    public int Stride => Width * BytesPerPixel;

    /// <summary>裁剪出子区域；若区域等于整图则返回自身。</summary>
    public ImageFrame Crop(int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        width = Math.Clamp(width, 1, Width - x);
        height = Math.Clamp(height, 1, Height - y);

        if (x == 0 && y == 0 && width == Width && height == Height)
        {
            return this;
        }

        var rowBytes = width * BytesPerPixel;
        var dst = new byte[rowBytes * height];
        for (var row = 0; row < height; row++)
        {
            var srcOffset = ((y + row) * Width + x) * BytesPerPixel;
            Buffer.BlockCopy(Pixels, srcOffset, dst, row * rowBytes, rowBytes);
        }

        return new ImageFrame(width, height, dst);
    }

    /// <summary>
    /// 转 8 位灰度，权重为 ITU-R BT.601（0.299/0.587/0.114），
    /// 与旧版 QrCodeScanner 的 <c>.3/.59/.11</c> 矩阵一致。
    /// </summary>
    public byte[] ToGray8()
    {
        var gray = new byte[Width * Height];
        for (int i = 0, p = 0; i < gray.Length; i++, p += BytesPerPixel)
        {
            var b = Pixels[p];
            var g = Pixels[p + 1];
            var r = Pixels[p + 2];
            gray[i] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
        }

        return gray;
    }
}
