using Kscan.Core.Imaging;

namespace Kscan.Core.Tests;

public sealed class ScrollStitcherTests
{
    [Theory]
    [InlineData(320, 100000)]
    [InlineData(1280, 100000)]
    [InlineData(1920, 66666)]
    [InlineData(3840, 33333)]
    public void Increased_default_limits_account_for_capture_width(int width, int expectedHeight)
    {
        var stitcher = new ScrollStitcher(new ImageFrame(width, 8, new byte[width * 8 * 4]));
        Assert.Equal(128_000_000, ScrollStitcher.DefaultMaxPixels);
        Assert.Equal(100_000, ScrollStitcher.DefaultMaxHeight);
        Assert.Equal(expectedHeight, stitcher.HeightLimit);
    }

    [Fact]
    public void Long_capture_exceeds_old_height_limit_and_compacts_to_a_valid_lossless_png()
    {
        var page = Page(96, 36000);
        var stitcher = new ScrollStitcher(page.Crop(0, 0, page.Width, 400));
        for (var top = 368; top <= 35328; top += 368)
            Assert.Equal(ScrollAppendStatus.Added, stitcher.TryAppend(page.Crop(0, top, page.Width, 400)).Status);
        Assert.True(stitcher.Height > 30000);
        var result = stitcher.BuildAndCompact();
        Assert.Equal(page.Crop(0, 0, page.Width, 35728).Pixels, result.Pixels);
        Assert.Equal(1, stitcher.PartCount);
        Assert.False(stitcher.CanUndo);
        Assert.Same(result, stitcher.BuildAndCompact());
        var decoded = SkiaImageCodec.FromEncoded(SkiaImageCodec.EncodePngFast(result));
        Assert.Equal(35728, decoded.Height);
        Assert.Equal(result.Pixels, decoded.Pixels);
    }

    [Fact]
    public void Fast_nearly_full_screen_steps_preserve_all_pixels_and_undo_anchor()
    {
        var page = Page(320, 6000);
        var stitcher = new ScrollStitcher(page.Crop(0, 0, page.Width, 600));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var top = 560; top <= 4480; top += 560)
        {
            var result = stitcher.TryAppend(page.Crop(0, top, page.Width, 600));
            Assert.Equal(ScrollAppendStatus.Added, result.Status);
            Assert.Equal(560, result.AddedRows);
        }
        watch.Stop();
        Console.WriteLine($"scroll benchmark: eight 320x600 frames / 40px overlap: {watch.Elapsed.TotalMilliseconds:0.0}ms");
        Assert.Equal(page.Crop(0, 0, page.Width, 5080).Pixels, stitcher.Build().Pixels);
        Assert.True(stitcher.Undo());
        Assert.Equal(ScrollAppendStatus.Added, stitcher.TryAppend(page.Crop(0, 4480, page.Width, 600)).Status);
        Assert.Equal(page.Crop(0, 0, page.Width, 5080).Pixels, stitcher.Build().Pixels);
    }

    private static ImageFrame Page(int width = 240, int height = 1600)
    {
        var pixels = new byte[width * height * 4];
        var random = new Random(7182);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var value = (byte)random.Next(15, 240);
                pixels[i] = value; pixels[i + 1] = (byte)(255 - value); pixels[i + 2] = (byte)((value * 3) % 255); pixels[i + 3] = 255;
            }
        return new ImageFrame(width, height, pixels);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(90)]
    [InlineData(230)]
    [InlineData(260)]
    [InlineData(268)]
    public void Appended_rows_are_pixel_exact_without_repeated_overlap(int shift)
    {
        var page = Page();
        var stitcher = new ScrollStitcher(page.Crop(0, 0, page.Width, 300));
        var result = stitcher.TryAppend(page.Crop(0, shift, page.Width, 300));
        Assert.Equal(ScrollAppendStatus.Added, result.Status);
        Assert.Equal(shift, result.AddedRows);
        Assert.Equal(page.Crop(0, 0, page.Width, 300 + shift).Pixels, stitcher.Build().Pixels);
    }

    [Fact]
    public void Undo_restores_anchor_and_a_new_capture_does_not_repeat_or_lose_rows()
    {
        var page = Page();
        var stitcher = new ScrollStitcher(page.Crop(0, 0, page.Width, 300));
        Assert.Equal(ScrollAppendStatus.Added, stitcher.TryAppend(page.Crop(0, 100, page.Width, 300)).Status);
        Assert.Equal(ScrollAppendStatus.Added, stitcher.TryAppend(page.Crop(0, 180, page.Width, 300)).Status);
        Assert.True(stitcher.Undo()); Assert.Equal(400, stitcher.Height);
        Assert.Equal(ScrollAppendStatus.Added, stitcher.TryAppend(page.Crop(0, 150, page.Width, 300)).Status);
        Assert.Equal(page.Crop(0, 0, page.Width, 450).Pixels, stitcher.Build().Pixels);
    }

    [Fact]
    public void Unchanged_and_unrelated_frames_do_not_modify_output()
    {
        var page = Page();
        var stitcher = new ScrollStitcher(page.Crop(0, 100, page.Width, 300));
        var before = stitcher.Build().Pixels;
        Assert.Equal(ScrollAppendStatus.Unchanged, stitcher.TryAppend(page.Crop(0, 100, page.Width, 300)).Status);
        Assert.Equal(ScrollAppendStatus.NoOverlap, stitcher.TryAppend(page.Crop(0, 900, page.Width, 300)).Status);
        Assert.Equal(before, stitcher.Build().Pixels);
        Assert.False(stitcher.Undo());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(268)]
    public void Upward_capture_prepends_exact_rows_and_undo_restores_initial_frame(int shift)
    {
        var page = Page();
        var first = page.Crop(0, 500, page.Width, 300);
        var stitcher = new ScrollStitcher(first);
        var result = stitcher.TryAppend(page.Crop(0, 500 - shift, page.Width, 300));
        Assert.Equal(ScrollAppendStatus.Added, result.Status);
        Assert.True(result.AddedAtTop);
        Assert.Equal(shift, result.AddedRows);
        Assert.Equal(page.Crop(0, 500 - shift, page.Width, 300 + shift).Pixels, stitcher.Build().Pixels);
        Assert.True(stitcher.Undo());
        Assert.Equal(first.Pixels, stitcher.Build().Pixels);
        Assert.False(stitcher.CanUndo);
    }

    [Fact]
    public void Bidirectional_traversal_does_not_repeat_rows_and_undo_follows_addition_order()
    {
        var page = Page();
        var stitcher = new ScrollStitcher(page.Crop(0, 600, page.Width, 300));
        Assert.True(stitcher.TryAppend(page.Crop(0, 500, page.Width, 300)).AddedAtTop);
        Assert.Equal(ScrollAppendStatus.Unchanged, stitcher.TryAppend(page.Crop(0, 600, page.Width, 300)).Status);
        var down = stitcher.TryAppend(page.Crop(0, 850, page.Width, 300));
        Assert.Equal(250, down.AddedRows); Assert.False(down.AddedAtTop);
        Assert.Equal(ScrollAppendStatus.Unchanged, stitcher.TryAppend(page.Crop(0, 700, page.Width, 300)).Status);
        var up = stitcher.TryAppend(page.Crop(0, 450, page.Width, 300));
        Assert.Equal(50, up.AddedRows); Assert.True(up.AddedAtTop);
        Assert.Equal(page.Crop(0, 450, page.Width, 700).Pixels, stitcher.Build().Pixels);
        Assert.True(stitcher.Undo());
        Assert.Equal(page.Crop(0, 500, page.Width, 650).Pixels, stitcher.Build().Pixels);
        Assert.True(stitcher.Undo());
        Assert.Equal(page.Crop(0, 500, page.Width, 400).Pixels, stitcher.Build().Pixels);
        Assert.True(stitcher.Undo());
        Assert.Equal(page.Crop(0, 600, page.Width, 300).Pixels, stitcher.Build().Pixels);
        Assert.Equal(ScrollAppendStatus.Added, stitcher.TryAppend(page.Crop(0, 500, page.Width, 300)).Status);
        Assert.Equal(page.Crop(0, 500, page.Width, 400).Pixels, stitcher.Build().Pixels);
    }

    [Fact]
    public void Upward_limit_failure_does_not_move_anchor()
    {
        var page = Page();
        var first = page.Crop(0, 500, page.Width, 300);
        var stitcher = new ScrollStitcher(first, maxHeight: 350);
        Assert.Equal(ScrollAppendStatus.LimitReached, stitcher.TryAppend(page.Crop(0, 400, page.Width, 300)).Status);
        Assert.Equal(first.Pixels, stitcher.Build().Pixels);
        Assert.Equal(50, stitcher.TryAppend(page.Crop(0, 450, page.Width, 300)).AddedRows);
        Assert.Equal(page.Crop(0, 450, page.Width, 350).Pixels, stitcher.Build().Pixels);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(281)]
    public void Repeated_direction_changes_and_all_undos_keep_exact_document_order(int seed)
    {
        var page = Page(120, 1600);
        var top = 650;
        var firstRow = top;
        var bottomRow = top + 300;
        var stitcher = new ScrollStitcher(page.Crop(0, top, page.Width, 300));
        var history = new Stack<(int Top, int Bottom)>();
        var random = new Random(seed);
        for (var i = 0; i < 80; i++)
        {
            top = Math.Clamp(top + random.Next(-240, 241), 0, 1300);
            var previous = (firstRow, bottomRow);
            firstRow = Math.Min(firstRow, top);
            bottomRow = Math.Max(bottomRow, top + 300);
            var result = stitcher.TryAppend(page.Crop(0, top, page.Width, 300));
            var expectedRows = bottomRow - firstRow - (previous.bottomRow - previous.firstRow);
            Assert.Equal(expectedRows > 0 ? ScrollAppendStatus.Added : ScrollAppendStatus.Unchanged, result.Status);
            Assert.Equal(expectedRows, result.AddedRows);
            if (expectedRows > 0) history.Push(previous);
            Assert.Equal(page.Crop(0, firstRow, page.Width, bottomRow - firstRow).Pixels, stitcher.Build().Pixels);
        }
        while (history.TryPop(out var previous))
        {
            Assert.True(stitcher.Undo());
            Assert.Equal(page.Crop(0, previous.Top, page.Width, previous.Bottom - previous.Top).Pixels, stitcher.Build().Pixels);
        }
        Assert.False(stitcher.CanUndo);
    }

    [Fact]
    public void Blank_or_periodic_content_cannot_invent_an_alignment()
    {
        var pixels = new byte[240 * 300 * 4];
        for (var y = 0; y < 300; y++)
            for (var x = 0; x < 240; x++)
            {
                var i = (y * 240 + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)(y % 32 < 8 ? 0 : 255); pixels[i + 3] = 255;
            }
        var frame = new ImageFrame(240, 300, pixels);
        var shifted = new byte[pixels.Length];
        for (var y = 0; y < 300; y++) Buffer.BlockCopy(pixels, ((y + 7) % 288) * frame.Stride, shifted, y * frame.Stride, frame.Stride);
        var stitcher = new ScrollStitcher(frame);
        Assert.Equal(ScrollAppendStatus.Ambiguous, stitcher.TryAppend(new ImageFrame(240, 300, shifted)).Status);
        Assert.Equal(300, stitcher.Height);
    }

    [Fact]
    public void Limits_and_size_changes_preserve_the_last_good_result()
    {
        var page = Page();
        var stitcher = new ScrollStitcher(page.Crop(0, 0, page.Width, 300), maxPixels: 240 * 350);
        Assert.Equal(ScrollAppendStatus.LimitReached, stitcher.TryAppend(page.Crop(0, 100, page.Width, 300)).Status);
        Assert.Equal(ScrollAppendStatus.SizeChanged, stitcher.TryAppend(page.Crop(0, 0, 200, 300)).Status);
        Assert.Equal(300, stitcher.Build().Height);
        var preview = stitcher.Preview(80, 100);
        Assert.True(preview.Width <= 80 && preview.Height <= 100);
    }

    [Fact]
    public void Sparse_document_lines_match_without_random_noise_texture()
    {
        var page = Page();
        var white = new byte[page.Pixels.Length];
        Array.Fill(white, (byte)255);
        var random = new Random(321);
        for (var top = 0; top < page.Height - 10; top += 24)
            for (var block = 0; block < 8; block++)
            {
                var left = random.Next(10, page.Width - 25);
                for (var y = top; y < top + 7; y++)
                    for (var x = left; x < left + 15; x++)
                    {
                        var i = (y * page.Width + x) * 4;
                        white[i] = white[i + 1] = white[i + 2] = 20;
                    }
            }
        var document = new ImageFrame(page.Width, page.Height, white);
        var stitcher = new ScrollStitcher(document.Crop(0, 0, page.Width, 400));
        var result = stitcher.TryAppend(document.Crop(0, 120, page.Width, 400));
        Assert.Equal(ScrollAppendStatus.Added, result.Status);
        Assert.Equal(document.Crop(0, 0, page.Width, 520).Pixels, stitcher.Build().Pixels);
    }
}
