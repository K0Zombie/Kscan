using Kscan.Core.Imaging;

namespace Kscan.Core.Tests;

/// <summary>固定种子的退化样本，供回归与性能对比复用。</summary>
public static class PreprocessingSamples
{
    public const string Payload = "QSCAN-PREPROCESS-2026";

    public static ImageFrame Qr() => SkiaImageCodec.FromEncoded(BarcodeImageFactory.CreateQrCodePng(Payload));

    public static IEnumerable<(string Name, ImageFrame Frame)> All()
    {
        yield return ("clean", Qr());
        foreach (var (low, high) in new[] { (92, 104), (12, 22), (226, 238) })
            yield return ($"contrast-{low}-{high}", Map(Qr(), (_, _, value) => (byte)(value < 128 ? low : high)));
        foreach (var strength in new[] { 80, 140, 200 })
        {
            var qr = Qr();
            yield return ($"shadow-{strength}", Map(qr, (x, _, value) =>
                (byte)Math.Clamp((value < 128 ? 25 : 70) + strength * x / qr.Width, 0, 255)));
        }
        foreach (var percent in new[] { 1, 3, 5 })
        {
            var qr = Qr();
            var random = new Random(1729);
            yield return ($"impulse-{percent}", Map(qr, (_, _, value) =>
                random.Next(100) < percent ? (byte)(255 - value) : value));
        }
        var transparent = Qr();
        for (var p = 0; p < transparent.Width * transparent.Height * 4; p += 4)
        {
            var white = transparent.Pixels[p] > 128;
            transparent.Pixels[p] = transparent.Pixels[p + 1] = transparent.Pixels[p + 2] = 0;
            transparent.Pixels[p + 3] = white ? (byte)0 : (byte)255;
        }
        yield return ("transparent", transparent);
    }

    public static ImageFrame Map(ImageFrame source, Func<int, int, byte, byte> map)
    {
        var pixels = (byte[])source.Pixels.Clone();
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var p = (y * source.Width + x) * 4;
            pixels[p] = pixels[p + 1] = pixels[p + 2] = map(x, y, pixels[p]);
        }
        return new ImageFrame(source.Width, source.Height, pixels);
    }
}
