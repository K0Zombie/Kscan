using Kscan.Core.Imaging;

namespace Kscan.Core.Tests;

public class ImageFrameTests
{
    [Fact]
    public void Crop_extracts_expected_region()
    {
        // 4x3 图，每个像素的 B 通道记录序号，便于校验
        var frame = SequentialFrame(4, 3);
        var cropped = frame.Crop(1, 1, 2, 2);

        Assert.Equal(2, cropped.Width);
        Assert.Equal(2, cropped.Height);
        Assert.Equal(5, cropped.Pixels[0]);                                       // src(1,1) => 1*4+1 = 5
        Assert.Equal(6, cropped.Pixels[ImageFrame.BytesPerPixel]);                // src(2,1) => 6
        Assert.Equal(9, cropped.Pixels[2 * ImageFrame.BytesPerPixel]);            // src(1,2) => 9
        Assert.Equal(10, cropped.Pixels[3 * ImageFrame.BytesPerPixel]);           // src(2,2) => 10
    }

    [Fact]
    public void Crop_clamps_to_image_bounds()
    {
        var frame = SequentialFrame(4, 4);

        var cropped = frame.Crop(3, 3, 100, 100);

        Assert.Equal(1, cropped.Width);
        Assert.Equal(1, cropped.Height);
    }

    [Fact]
    public void Crop_of_whole_image_returns_same_instance()
    {
        var frame = SequentialFrame(4, 4);

        Assert.Same(frame, frame.Crop(0, 0, 4, 4));
    }

    [Fact]
    public void To_gray8_uses_bt601_weights()
    {
        var frame = FrameWith(1, 3,
            (0, 0, 255),   // 纯红 -> 0.299*255 = 76.2 -> 76
            (0, 255, 0),   // 纯绿 -> 0.587*255 = 149.7 -> 149
            (255, 0, 0));  // 纯蓝 -> 0.114*255 = 29.1 -> 29

        var gray = frame.ToGray8();

        Assert.Equal(76, gray[0]);
        Assert.Equal(149, gray[1]);
        Assert.Equal(29, gray[2]);
    }

    [Fact]
    public void Constructor_rejects_buffer_that_is_too_small()
    {
        Assert.Throws<ArgumentException>(() => new ImageFrame(10, 10, new byte[100]));
    }

    [Fact]
    public void Constructor_rejects_non_positive_dimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageFrame(0, 10, new byte[40]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageFrame(10, 0, new byte[40]));
    }

    private static ImageFrame SequentialFrame(int width, int height)
    {
        var frame = new ImageFrame(width, height, new byte[width * height * ImageFrame.BytesPerPixel]);
        for (var i = 0; i < width * height; i++)
        {
            frame.Pixels[(i * ImageFrame.BytesPerPixel) + 0] = (byte)i;
        }

        return frame;
    }

    private static ImageFrame FrameWith(int width, int height, params (byte B, byte G, byte R)[] pixels)
    {
        var frame = new ImageFrame(width, height, new byte[width * height * ImageFrame.BytesPerPixel]);
        for (var i = 0; i < pixels.Length; i++)
        {
            var offset = i * ImageFrame.BytesPerPixel;
            frame.Pixels[offset + 0] = pixels[i].B;
            frame.Pixels[offset + 1] = pixels[i].G;
            frame.Pixels[offset + 2] = pixels[i].R;
            frame.Pixels[offset + 3] = 255;
        }

        return frame;
    }
}
