using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Kscan.App.Services;
using Kscan.Core.Imaging;

namespace Kscan.App.Tests;

/// <summary>
/// <see cref="ResultActions.ToBitmap"/> 的像素直拷路径：内容要对、代价要小。
/// <para>
/// 以前它走的是"编码成 PNG 再让 Avalonia 解码"，整屏抓图（2560×1440）在 UI 线程上要花几百毫秒，
/// 而且跑在框选窗口 <c>Show()</c> 之前 —— 表现就是"触发框选时先卡一下"。
/// </para>
/// </summary>
public sealed class BitmapConversionTests
{
    private static ImageFrame MakeFrame(int width, int height, int seed = 7)
    {
        var pixels = new byte[width * height * 4];
        new Random(seed).NextBytes(pixels);

        // 与抓屏路径一致：alpha 全不透明
        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        return new ImageFrame(width, height, pixels);
    }

    /// <summary>奇数宽度 —— 一旦实现里假设了"行距 = 宽度×4 且无对齐填充"就会在这里露馅。</summary>
    [AvaloniaFact]
    public void ToBitmap_copies_pixels_verbatim()
    {
        var frame = MakeFrame(97, 43);
        using var bitmap = (WriteableBitmap)ResultActions.ToBitmap(frame);

        Assert.Equal(frame.Width, bitmap.PixelSize.Width);
        Assert.Equal(frame.Height, bitmap.PixelSize.Height);
        Assert.Equal((double)96, bitmap.Dpi.X);

        using var buffer = bitmap.Lock();
        Assert.Equal(PixelFormat.Bgra8888, buffer.Format);
        Assert.Equal(AlphaFormat.Unpremul, buffer.AlphaFormat);

        var rowBytes = frame.Stride;
        var actual = new byte[rowBytes];
        for (var row = 0; row < frame.Height; row++)
        {
            Marshal.Copy(buffer.Address + (row * buffer.RowBytes), actual, 0, rowBytes);
            Assert.True(
                frame.Pixels.AsSpan(row * rowBytes, rowBytes).SequenceEqual(actual),
                $"第 {row} 行像素与源帧不一致");
        }
    }

    /// <summary>整屏大小的对照：直拷必须比 PNG 往返快一个数量级（阈值放到 2× 以免机器慢时误报）。</summary>
    [AvaloniaFact]
    public void ToBitmap_is_far_cheaper_than_the_png_round_trip()
    {
        var frame = MakeFrame(2560, 1440);

        var roundTrip = Stopwatch.StartNew();
        using (var encoded = new MemoryStream(SkiaImageCodec.EncodePng(frame)))
        {
            using var bitmap = new Bitmap(encoded);
        }

        roundTrip.Stop();

        var direct = Stopwatch.StartNew();
        using (var bitmap = ResultActions.ToBitmap(frame))
        {
        }

        direct.Stop();

        // 数字要留证据：xunit 把 Console 输出吞掉了，写一份到系统临时目录（跑测试即刷新）
        File.WriteAllText(
            Path.Combine(Path.GetTempPath(), "kscan-tobitmap-bench.txt"),
            $"2560×1440 PNG 往返 {roundTrip.Elapsed.TotalMilliseconds:F1} ms；" +
            $"直拷像素 {direct.Elapsed.TotalMilliseconds:F1} ms");

        Assert.True(
            direct.ElapsedMilliseconds * 2 < roundTrip.ElapsedMilliseconds,
            $"直拷像素没有比 PNG 往返快：直拷 {direct.ElapsedMilliseconds} ms / PNG {roundTrip.ElapsedMilliseconds} ms");
    }
}
