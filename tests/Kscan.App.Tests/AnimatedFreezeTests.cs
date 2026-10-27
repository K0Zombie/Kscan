using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Kscan.App.Services;
using Kscan.Platform.Abstractions;

namespace Kscan.App.Tests;

/// <summary>
/// 动图定格（按住修饰键停在当前帧）的纯逻辑回归：
/// 设置值 ↔ 修饰键的映射，以及播放器的"暂停 / 从当前帧继续"。
/// </summary>
public sealed class AnimatedFreezeTests
{
    [Fact]
    public void Setting_maps_to_modifier_and_back()
    {
        Assert.Equal(HoldModifier.Shift, FreezeKey.ToModifier("Shift"));
        Assert.Equal(HoldModifier.Control, FreezeKey.ToModifier("Control"));
        Assert.Equal(HoldModifier.Alt, FreezeKey.ToModifier("Alt"));
        Assert.Null(FreezeKey.ToModifier("None"));

        // 旧值/脏值按默认 Shift 处理，别让功能悄悄失效
        Assert.Equal(HoldModifier.Shift, FreezeKey.ToModifier(null));
        Assert.Equal(HoldModifier.Shift, FreezeKey.ToModifier("Shift+"));
        Assert.Equal(HoldModifier.Control, FreezeKey.ToModifier("Ctrl"));

        Assert.Equal("Ctrl", FreezeKey.Label(HoldModifier.Control));
        Assert.Equal(FreezeKey.Control, FreezeKey.Stored(HoldModifier.Control));
        Assert.Equal(FreezeKey.Alt, FreezeKey.Stored(HoldModifier.Alt));
        Assert.Equal(FreezeKey.Shift, FreezeKey.Stored(HoldModifier.Shift));
    }

    [AvaloniaFact]
    public void Pause_keeps_the_current_frame_and_resume_continues_from_it()
    {
        var frames = new Bitmap[]
        {
            NewBitmap(), NewBitmap(), NewBitmap(), NewBitmap(),
        };

        var displayed = new List<int>();
        var player = new AnimatedPinPlayer(frames, [100, 100, 100, 100]);
        player.FrameShown += (_, _) => displayed.Add(player.Index);

        player.Start();      // 第 0 帧
        player.Stop();
        player.Advance();    // 第 1 帧
        Assert.Equal(1, player.Index);

        player.Pause();
        Assert.True(player.IsPaused);
        Assert.False(player.IsRunning, "定格时定时器必须停");
        Assert.Equal(1, player.Index);

        // 定格期间画面就是当前帧
        Assert.Same(frames[1], player.Current);

        player.Resume();
        Assert.False(player.IsPaused);
        Assert.True(player.IsRunning, "松开后应该继续播");
        Assert.Equal(1, player.Index);   // **不回到第 0 帧**
        Assert.Equal(1, displayed[^1]);

        player.Stop();
        player.Dispose();
    }

    private static Bitmap NewBitmap()
    {
        using var skia = new SkiaSharp.SKBitmap(8, 8);
        skia.Erase(SkiaSharp.SKColors.Orange);
        using var encoded = skia.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(encoded.ToArray());
        return new Bitmap(stream);
    }
}
