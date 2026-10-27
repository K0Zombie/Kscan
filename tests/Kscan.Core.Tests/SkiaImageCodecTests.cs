using Kscan.Core.Imaging;

namespace Kscan.Core.Tests;

public class SkiaImageCodecTests
{
    [Fact]
    public void Fast_png_preserves_every_pixel_including_transparency_and_does_not_mutate_input()
    {
        var frame = MakeFrame(97, 61);
        for (var i = 0; i < frame.Pixels.Length; i += 4)
        {
            frame.Pixels[i + 3] = (byte)((i / 4) % 256);
            if (frame.Pixels[i + 3] == 0) frame.Pixels[i] = frame.Pixels[i + 1] = frame.Pixels[i + 2] = 0;
        }
        var before = frame.Pixels.ToArray();
        var encoded = SkiaImageCodec.EncodePngFast(frame);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, encoded.Take(8));
        var decoded = SkiaImageCodec.FromEncoded(encoded);
        Assert.Equal(frame.Width, decoded.Width); Assert.Equal(frame.Height, decoded.Height);
        Assert.Equal(before, decoded.Pixels);
        Assert.Equal(before, frame.Pixels);
    }

    [Fact]
    public void Clipboard_png_benchmark_records_time_and_size_for_same_full_hd_pixels()
    {
        var frame = MakeFrame(1920, 1080);
        for (var y = 0; y < frame.Height; y++)
        for (var x = 0; x < frame.Width; x++)
        {
            var i = (y * frame.Width + x) * 4;
            var background = (byte)(x < 320 || y < 70 ? 40 : 250);
            var line = x > 350 && x < 1600 && y > 100 && y % 28 < 3;
            frame.Pixels[i] = line ? (byte)(x % 160) : background;
            frame.Pixels[i + 1] = line ? (byte)60 : background;
            frame.Pixels[i + 2] = line ? (byte)40 : background;
        }
        _ = SkiaImageCodec.EncodePngFast(MakeFrame(10, 10));
        _ = SkiaImageCodec.EncodePng(MakeFrame(10, 10));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var standard = SkiaImageCodec.EncodePng(frame);
        var standardMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var fast = SkiaImageCodec.EncodePngFast(frame);
        var fastMs = timer.Elapsed.TotalMilliseconds;
        Console.WriteLine($"clipboard PNG 1920x1080: existing={standardMs:0.0}ms/{standard.Length}B, fast={fastMs:0.0}ms/{fast.Length}B");
        Assert.Equal(frame.Pixels, SkiaImageCodec.FromEncoded(fast).Pixels);
        Assert.Equal(frame.Pixels, SkiaImageCodec.FromEncoded(standard).Pixels);
    }

    [Fact]
    public void Png_round_trip_preserves_pixels()
    {
        var frame = MakeFrame(7, 5);

        var decoded = SkiaImageCodec.FromEncoded(SkiaImageCodec.EncodePng(frame));

        Assert.Equal(frame.Width, decoded.Width);
        Assert.Equal(frame.Height, decoded.Height);
        Assert.Equal(frame.Pixels, decoded.Pixels);
    }

    [Fact]
    public void Load_reads_a_png_from_disk()
    {
        var frame = MakeFrame(4, 4);
        var path = Path.Combine(Path.GetTempPath(), $"qrscan-{Guid.NewGuid():N}.png");

        try
        {
            SkiaImageCodec.SavePng(frame, path);

            var loaded = SkiaImageCodec.Load(path);

            Assert.Equal(4, loaded.Width);
            Assert.Equal(4, loaded.Height);
            Assert.Equal(frame.Pixels, loaded.Pixels);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Invalid_data_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => SkiaImageCodec.FromEncoded([1, 2, 3, 4, 5, 6, 7, 8]));
    }

    [Fact]
    public void Missing_file_throws_file_not_found()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qrscan-missing-{Guid.NewGuid():N}.png");

        Assert.Throws<FileNotFoundException>(() => SkiaImageCodec.Load(path));
    }

    private static ImageFrame MakeFrame(int width, int height)
    {
        var frame = new ImageFrame(width, height, new byte[width * height * ImageFrame.BytesPerPixel]);
        for (var i = 0; i < width * height; i++)
        {
            var offset = i * ImageFrame.BytesPerPixel;
            frame.Pixels[offset + 0] = (byte)(i * 3 % 256);
            frame.Pixels[offset + 1] = (byte)(i * 7 % 256);
            frame.Pixels[offset + 2] = (byte)(i * 11 % 256);
            frame.Pixels[offset + 3] = 255;
        }

        return frame;
    }
}
