using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Kscan.App.Services;
using Kscan.App.Views;

namespace Kscan.App.Tests;

/// <summary>
/// 动图（GIF / 动画 WebP）的回归。
/// <para>
/// 实测结论（内置 SkiaSharp 3.119）：GIF 多帧正常；动画 WebP 能解但上报帧数可能少于源文件；
/// <b>APNG 解不出多帧</b>（<c>FrameCount</c> 为 0），只能按首帧显示 —— 这几条断言就是钉住这个现状，
/// 哪天 Skia 升级支持了 APNG，这里会红，提醒去把降级逻辑改掉。
/// </para>
/// </summary>
public sealed class AnimatedImageTests
{
    [AvaloniaFact]
    public void Gif_loads_all_frames_with_delays()
    {
        var animation = AnimatedImage.TryLoad(Asset("anim.gif"), out var reason);

        Assert.Null(reason);
        Assert.NotNull(animation);

        using var _ = animation;
        Assert.Equal(4, animation!.Frames.Count);
        Assert.All(animation.Delays, delay => Assert.Equal(120, delay));
        Assert.Equal(48, animation.Frames[0].PixelSize.Width);
        Assert.Equal(24, animation.Frames[0].PixelSize.Height);
        Assert.True(AnimatedImage.IsAnimatedFile(Asset("anim.gif")));
    }

    [AvaloniaFact]
    public void Animated_webp_loads_at_least_two_frames()
    {
        var animation = AnimatedImage.TryLoad(Asset("anim.webp"), out var reason);

        using var _ = animation;
        Assert.NotNull(animation);
        Assert.Null(reason);
        Assert.True(animation!.Frames.Count >= 2, $"WebP 动画帧数 = {animation.Frames.Count}");
    }

    [AvaloniaFact]
    public void Apng_is_not_supported_and_falls_back_to_first_frame()
    {
        var animation = AnimatedImage.TryLoad(Asset("anim.apng.png"), out var reason);

        Assert.Null(animation);
        Assert.NotNull(reason);
        Assert.Contains("APNG", reason);
        Assert.True(AnimatedImage.IsApng(Asset("anim.apng.png")));
        Assert.False(AnimatedImage.IsAnimatedFile(Asset("anim.apng.png")));
    }

    [AvaloniaFact]
    public void Static_image_is_not_animated()
    {
        var path = Path.Combine(Path.GetTempPath(), "kscan-static-" + Guid.NewGuid().ToString("N") + ".png");

        try
        {
            using (var skia = new SkiaSharp.SKBitmap(20, 10))
            {
                skia.Erase(SkiaSharp.SKColors.Orange);
                using var encoded = skia.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(path, encoded.ToArray());
            }

            Assert.Null(AnimatedImage.TryLoad(path, out var reason));
            Assert.Null(reason);
            Assert.False(AnimatedImage.IsAnimatedFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Limits_reject_oversized_animations()
    {
        // 正常尺寸/帧数
        Assert.True(AnimatedImage.IsWithinLimits(480, 270, 60));

        // 单边超过 2048
        Assert.False(AnimatedImage.IsWithinLimits(AnimatedImage.MaxDimension + 1, 100, 2));
        Assert.False(AnimatedImage.IsWithinLimits(100, AnimatedImage.MaxDimension + 1, 2));

        // 帧数超过 600
        Assert.False(AnimatedImage.IsWithinLimits(100, 100, AnimatedImage.MaxFrames + 1));

        // 解码后总内存超过 200MB：2048×2048×4×13 ≈ 218MB
        Assert.False(AnimatedImage.IsWithinLimits(2048, 2048, 13));
        Assert.True(AnimatedImage.IsWithinLimits(2048, 2048, 12));

        // 1 帧不算动图
        Assert.False(AnimatedImage.IsWithinLimits(100, 100, 1));
    }

    [AvaloniaFact]
    public void Player_advances_and_wraps_around()
    {
        var animation = AnimatedImage.TryLoad(Asset("anim.gif"), out _)!;
        var (frames, delays) = animation.TakeFrames();

        var shown = new List<int>();
        var player = new AnimatedPinPlayer(frames, delays);
        player.FrameShown += (_, _) => shown.Add(player.Index);

        player.Start();          // 播第一帧
        player.Stop();           // 测试里不等真实时间

        Assert.Equal(0, player.Index);
        Assert.Single(shown);

        player.Advance();
        Assert.Equal(1, player.Index);
        Assert.Equal(1, shown[^1]);

        player.Advance();
        player.Advance();
        Assert.Equal(3, player.Index);

        player.Advance();   // 循环回第一帧
        Assert.Equal(0, player.Index);

        player.Stop();
        player.Dispose();
    }

    [AvaloniaFact]
    public void Animated_pin_refuses_to_rotate()
    {
        var animation = AnimatedImage.TryLoad(Asset("anim.gif"), out _)!;
        var (frames, delays) = animation.TakeFrames();

        var pin = PinManager.Current.Pin(
            frames[0], "anim.gif", ownsImage: false,
            targetRegion: new PixelRect(200, 200, frames[0].PixelSize.Width, frames[0].PixelSize.Height));

        try
        {
            pin.ShowAnimation(new AnimatedPinPlayer(frames, delays));
            pin.Focus();
            Dispatcher.UIThread.RunJobs();

            Assert.True(pin.IsAnimated);

            pin.KeyPress(Key.E, RawInputModifiers.None, PhysicalKey.E, "E");
            pin.KeyRelease(Key.E, RawInputModifiers.None, PhysicalKey.E, "E");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, pin.RotationDegrees);
            Assert.False(pin.IsMirroredHorizontally);
            Assert.False(pin.IsMirroredVertically);
        }
        finally
        {
            pin.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static string Asset(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", name);
}
