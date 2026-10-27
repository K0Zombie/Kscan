using Kscan.Core.Imaging;
using Kscan.Core.Model;
using Kscan.Core.Pipeline;

namespace Kscan.Core.Tests;

public sealed class ImageFiltersTests
{
    public static IEnumerable<object[]> Corpus() => PreprocessingSamples.All()
        .Select(sample => new object[] { sample.Name });

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Preprocessing_recovers_degraded_codes_without_mutating_input(string name)
    {
        var frame = PreprocessingSamples.All().Single(sample => sample.Name == name).Frame;
        var original = (byte[])frame.Pixels.Clone();
        var pipeline = ScanPipeline.CreateDefault();
        var outcome = pipeline.Scan(frame);
        Assert.Equal(PreprocessingSamples.Payload, outcome.FirstText);
        Assert.Equal(original, frame.Pixels);
        Assert.All(outcome.Results.SelectMany(result => result.Points), point =>
        {
            Assert.InRange(point.X, 0, frame.Width - 1);
            Assert.InRange(point.Y, 0, frame.Height - 1);
        });
        if (name.StartsWith("contrast-", StringComparison.Ordinal) || name.StartsWith("shadow-", StringComparison.Ordinal))
            Assert.False(pipeline.Scan(frame, new ScanOptions { TryDenoise = false }).Success);
        if (name == "clean") Assert.Single(outcome.Attempts);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void Median_matches_sorted_neighbourhood_and_preserves_alpha_and_edges(int radius, bool grayscale)
    {
        const int width = 19, height = 17;
        var random = new Random(239);
        var pixels = new byte[width * height * 4];
        random.NextBytes(pixels);
        if (grayscale)
            for (var p = 0; p < pixels.Length; p += 4) pixels[p + 1] = pixels[p + 2] = pixels[p];
        var original = (byte[])pixels.Clone();
        var actual = ImageFilters.MedianBlur(new ImageFrame(width, height, pixels), radius);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        for (var c = 0; c < 4; c++)
        {
            var p = (y * width + x) * 4 + c;
            var expected = pixels[p];
            if (c < 3 && x >= radius && y >= radius && x < width - radius && y < height - radius)
            {
                var values = new List<byte>();
                for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++) values.Add(pixels[((y + dy) * width + x + dx) * 4 + c]);
                values.Sort();
                expected = values[values.Count / 2];
            }
            Assert.Equal(expected, actual.Pixels[p]);
        }
        Assert.Equal(original, pixels);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(37, 29)]
    public void Rolling_threshold_matches_naive_local_means_including_clipped_borders(int width, int height)
    {
        var random = new Random(1729);
        var pixels = new byte[width * height * 4];
        for (var p = 0; p < pixels.Length; p += 4)
        {
            pixels[p] = pixels[p + 1] = pixels[p + 2] = (byte)random.Next(256);
            pixels[p + 3] = 255;
        }
        var source = new ImageFrame(width, height, pixels);
        var actual = ImageFilters.AdaptiveThreshold(source);
        if (width * height == 1) { Assert.Same(source, actual); return; }
        var radius = Math.Clamp(Math.Min(width, height) / 12, 8, 64);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var sum = 0;
            var count = 0;
            for (var py = Math.Max(0, y - radius); py <= Math.Min(height - 1, y + radius); py++)
            for (var px = Math.Max(0, x - radius); px <= Math.Min(width - 1, x + radius); px++)
            { sum += pixels[(py * width + px) * 4]; count++; }
            var expected = (byte)(pixels[(y * width + x) * 4] < sum / (double)count - 2 ? 0 : 255);
            Assert.Equal(expected, actual.Pixels[(y * width + x) * 4]);
        }
    }

    [Fact]
    public void Median_selection_handles_every_binary_three_by_three_pattern()
    {
        for (var pattern = 0; pattern < 512; pattern++)
        {
            var pixels = new byte[3 * 3 * 4];
            for (var n = 0; n < 9; n++)
            {
                pixels[n * 4] = pixels[n * 4 + 1] = pixels[n * 4 + 2] = (byte)((pattern & (1 << n)) == 0 ? 0 : 255);
                pixels[n * 4 + 3] = 255;
            }
            var filtered = ImageFilters.MedianBlur(new ImageFrame(3, 3, pixels), 1);
            var expected = (byte)(System.Numerics.BitOperations.PopCount((uint)pattern) >= 5 ? 255 : 0);
            Assert.Equal(expected, filtered.Pixels[4 * 4]);
        }
    }

    [Fact]
    public void Filters_skip_uniform_images_and_oversized_median_windows()
    {
        var gray = new ImageFrame(9, 9, Enumerable.Repeat((byte)255, 9 * 9 * 4).ToArray());
        Assert.Same(gray, ImageFilters.MedianBlur(gray, 1));
        Assert.Same(gray, ImageFilters.MedianBlur(gray, int.MaxValue));
        Assert.Same(gray, ImageFilters.RemoveColoredOverlay(gray));
        Assert.Same(gray, ImageFilters.StretchContrast(gray));
        Assert.Same(gray, ImageFilters.AdaptiveThreshold(gray));
    }

    [Fact]
    public void Overlay_repair_does_not_invent_white_pixels_without_clean_neighbours()
    {
        var pixels = new byte[9 * 9 * 4];
        for (var p = 0; p < pixels.Length; p += 4) { pixels[p] = 200; pixels[p + 3] = 255; }
        var source = new ImageFrame(9, 9, pixels);
        Assert.Same(source, ImageFilters.RemoveColoredOverlay(source));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void Overlay_repair_validates_threshold(int threshold) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageFilters.RemoveColoredOverlay(PreprocessingSamples.Qr(), threshold));
}
