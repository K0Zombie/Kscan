using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace Kscan.App.Services;

/// <summary>
/// 一张动图（GIF / 动画 WebP）解码后的帧序列。
/// <para>
/// 用 Skia 的 <see cref="SKCodec"/> 逐帧解码：帧按顺序解、并把上一帧作为 <c>PriorFrame</c> 传进去，
/// 这样 GIF 那种"只写变化区域"的帧才会被正确合成（否则会看到半张图）。
/// </para>
/// <para>
/// 上限（防止一张大 GIF 把内存吃光）：单边 ≤ <see cref="MaxDimension"/>、
/// 解码后总字节 ≤ <see cref="MaxBytes"/>、帧数 ≤ <see cref="MaxFrames"/>；
/// 超限返回 <c>null</c> 并给出原因，界面按"首帧静态图"显示。
/// </para>
/// <para>
/// 实测（2026/10/20，内置 SkiaSharp 3.119）：GIF 4 帧正常；APNG 的 <c>FrameCount</c> 是 0，
/// 也就是**不支持 APNG 动画**（只能显示首帧）；动画 WebP 能解，但 Skia 上报的帧数可能与源文件不同。
/// </para>
/// </summary>
internal sealed class AnimatedImage : IDisposable
{
    /// <summary>单边像素上限。</summary>
    public const int MaxDimension = 2048;

    /// <summary>解码后总内存上限（200MB）。</summary>
    public const long MaxBytes = 200L * 1024 * 1024;

    /// <summary>帧数上限。</summary>
    public const int MaxFrames = 600;

    private readonly Bitmap[] _frames;

    private readonly int[] _delays;

    private AnimatedImage(Bitmap[] frames, int[] delays)
    {
        _frames = frames;
        _delays = delays;
    }

    public IReadOnlyList<Bitmap> Frames => _frames;

    /// <summary>每帧停留时长（毫秒），与 <see cref="Frames"/> 一一对应。</summary>
    public IReadOnlyList<int> Delays => _delays;

    private bool _disposed;

    /// <summary>
    /// 把帧的所有权交出去（给播放器）。之后本对象的 <see cref="Dispose"/> 不会再释放它们，
    /// 避免"播放器在用、这边已经 Dispose"的悬空位图。
    /// </summary>
    internal (Bitmap[] Frames, int[] Delays) TakeFrames()
    {
        if (_disposed)
        {
            throw new InvalidOperationException("帧已经被取走了");
        }

        _disposed = true;
        return (_frames, _delays);
    }

    /// <summary>尺寸/帧数/内存是否在上限内（测试直接用它验边界）。</summary>
    public static bool IsWithinLimits(int width, int height, int frameCount) =>
        width is > 0 and <= MaxDimension
        && height is > 0 and <= MaxDimension
        && frameCount is > 1 and <= MaxFrames
        && (long)width * height * 4 * frameCount <= MaxBytes;

    /// <summary>
    /// 尝试把文件当成动图加载。不是动图、格式不支持、超出上限时返回 <c>null</c>，
    /// 并通过 <paramref name="reason"/> 说明原因（界面上用胶囊提示一次）。
    /// </summary>
    public static AnimatedImage? TryLoad(string path, out string? reason)
    {
        reason = null;

        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);

            if (codec is null)
            {
                reason = "无法解码";
                return null;
            }

            var count = codec.FrameCount;

            if (count <= 1)
            {
                reason = IsApng(path) ? "APNG（当前解码器不支持动画）" : null;
                return null;
            }

            var info = new SKImageInfo(
                codec.Info.Width,
                codec.Info.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Unpremul);

            if (!IsWithinLimits(info.Width, info.Height, count))
            {
                reason = $"超过上限（{info.Width}x{info.Height}，{count} 帧）";
                GuiLog.Write($"animated: {Path.GetFileName(path)} {reason}，按首帧显示");
                return null;
            }

            var frames = new Bitmap[count];
            var delays = new int[count];
            var buffer = Marshal.AllocHGlobal(info.BytesSize);
            var row = new byte[info.Width * 4];

            try
            {
                for (var i = 0; i < count; i++)
                {
                    // PriorFrame = i-1 是关键：让解码器在上一帧的基础上合成这一帧
                    var options = new SKCodecOptions(i, i - 1);
                    var result = codec.GetPixels(info, buffer, info.RowBytes, options);

                    if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                    {
                        reason = $"第 {i} 帧解码失败（{result}）";
                        GuiLog.Write($"animated: {Path.GetFileName(path)} {reason}，按首帧显示");

                        foreach (var frame in frames.Where(frame => frame is not null))
                        {
                            frame.Dispose();
                        }

                        return null;
                    }

                    var bitmap = new WriteableBitmap(
                        new PixelSize(info.Width, info.Height),
                        new Vector(96, 96),
                        PixelFormat.Bgra8888,
                        AlphaFormat.Unpremul);

                    using (var locked = bitmap.Lock())
                    {
                        for (var y = 0; y < info.Height; y++)
                        {
                            Marshal.Copy(buffer + (y * info.RowBytes), row, 0, row.Length);
                            Marshal.Copy(row, 0, locked.Address + (y * locked.RowBytes), row.Length);
                        }
                    }

                    frames[i] = bitmap;
                    delays[i] = Math.Clamp(codec.FrameInfo[i].Duration, 20, 2000);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            GuiLog.Write(
                $"animated: {Path.GetFileName(path)} {info.Width}x{info.Height}，{count} 帧，" +
                $"{frames.Length * info.BytesSize / 1024}KB");

            return new AnimatedImage(frames, delays);
        }
        catch (Exception ex)
        {
            GuiLog.Write($"animated: 读取 {Path.GetFileName(path)} 失败（{ex.GetType().Name}: {ex.Message}）");
            reason = "读取失败";
            return null;
        }
    }

    /// <summary>这个文件是不是多帧动图（用于库里打"动图"角标；不做解码，只看帧数）。</summary>
    public static bool IsAnimatedFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            return codec is not null && codec.FrameCount > 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>PNG 里有没有 <c>acTL</c> 块：有就是 APNG（当前解码器不播动画，只能首帧）。</summary>
    public static bool IsApng(string path)
    {
        try
        {
            if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using var stream = File.OpenRead(path);
            var buffer = new byte[Math.Min(8192, stream.Length)];
            var read = stream.Read(buffer, 0, buffer.Length);

            return read > 0 && buffer.AsSpan(0, read).IndexOf("acTL"u8) >= 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var frame in _frames)
        {
            frame.Dispose();
        }
    }
}
