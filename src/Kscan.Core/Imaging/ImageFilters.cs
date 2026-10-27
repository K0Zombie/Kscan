using System.Runtime.CompilerServices;

namespace Kscan.Core.Imaging;

/// <summary>
/// 识别前的图像处理。
/// <para>
/// 原图失败后才使用：彩色叠加修复、中值去噪、对比度拉伸与局部阈值。
/// 全部保持原始尺寸、坐标与输入缓冲区，不改变识别框的坐标系。
/// </para>
/// </summary>
public static class ImageFilters
{
    /// <summary>
    /// 中值滤波（半径 <paramref name="radius"/>，窗口为 2r+1 的正方形），每个颜色通道独立。
    /// 不改原图；图太小或无需改变时可原样返回，其余情况返回新帧。
    /// </summary>
    public static ImageFrame MedianBlur(ImageFrame source, int radius)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (radius <= 0 || source.Width <= (long)radius * 2 || source.Height <= (long)radius * 2)
        {
            return source;
        }

        var width = source.Width;
        var height = source.Height;
        var pixels = source.Pixels;
        if (radius == 1) return Median3(source);
        var output = new byte[pixels.Length];

        // 边界保留原像素，避免补零引入黑边或改变静区。
        Buffer.BlockCopy(pixels, 0, output, 0, pixels.Length);

        var side = radius * 2 + 1;
        var windowSize = side * side;
        var half = windowSize / 2;
        var window = new byte[windowSize];
        var histogram = new int[256];

        for (var y = radius; y < height - radius; y++)
        {
            for (var x = radius; x < width - radius; x++)
            {
                var offset = (y * width + x) * 4;

                for (var channel = 0; channel < 3; channel++)   // BGRA 里的 BGR；alpha 不动
                {
                    var count = 0;
                    for (var dy = -radius; dy <= radius; dy++)
                    {
                        var row = (y + dy) * width;
                        for (var dx = -radius; dx <= radius; dx++)
                        {
                            window[count++] = pixels[(row + x + dx) * 4 + channel];
                        }
                    }

                    // byte 只有 256 种取值：用直方图数到一半就是中值，比排序快
                    Array.Clear(histogram);

                    for (var i = 0; i < windowSize; i++)
                    {
                        histogram[window[i]]++;
                    }

                    var sum = 0;
                    for (var value = 0; value < 256; value++)
                    {
                        sum += histogram[value];
                        if (sum > half)
                        {
                            output[offset + channel] = (byte)value;
                            break;
                        }
                    }
                }
            }
        }

        return new ImageFrame(width, height, output);
    }

    private static ImageFrame Median3(ImageFrame source)
    {
        var pixels = source.Pixels;
        var output = (byte[])pixels.Clone();
        var width = source.Width;
        var stride = source.Stride;
        var grayscale = true;
        for (var p = 0; p < width * source.Height * 4; p += 4)
        {
            if (pixels[p] != pixels[p + 1] || pixels[p] != pixels[p + 2])
            {
                grayscale = false;
                break;
            }
        }
        var changed = false;
        Span<byte> window = stackalloc byte[9];
        for (var y = 1; y < source.Height - 1; y++)
        for (var x = 1; x < width - 1; x++)
        {
            var p = y * stride + x * 4;
            for (var channel = 0; channel < (grayscale ? 1 : 3); channel++)
            {
                var n = 0;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                    window[n++] = pixels[p + dy * stride + dx * 4 + channel];
                var value = Median9(window);
                output[p + channel] = value;
                changed |= value != pixels[p + channel];
                if (grayscale) output[p + 1] = output[p + 2] = value;
            }
        }
        return changed ? new ImageFrame(width, source.Height, output) : source;
    }

    // 固定 19 次比较交换，只选择中间值，不必把整个窗口排序。
    private static byte Median9(ReadOnlySpan<byte> values)
    {
        int a = values[0], b = values[1], c = values[2];
        int d = values[3], e = values[4], f = values[5];
        int g = values[6], h = values[7], i = values[8];
        Order(ref b, ref c); Order(ref e, ref f); Order(ref h, ref i);
        Order(ref a, ref b); Order(ref d, ref e); Order(ref g, ref h);
        Order(ref b, ref c); Order(ref e, ref f); Order(ref h, ref i);
        Order(ref a, ref d); Order(ref f, ref i); Order(ref e, ref h);
        Order(ref d, ref g); Order(ref b, ref e); Order(ref c, ref f);
        Order(ref e, ref h); Order(ref e, ref c); Order(ref g, ref e);
        Order(ref e, ref c);
        return (byte)e;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Order(ref int left, ref int right)
    {
        var min = Math.Min(left, right);
        right = Math.Max(left, right);
        left = min;
    }

    /// <summary>
    /// 按灰度直方图的两端分位拉伸低对比度图；平坦图与高对比度图不处理。
    /// </summary>
    /// <param name="tailPermille">
    /// 两端各裁掉的比例（千分比）。默认 <b>5</b>（= 0.5%，与历史行为一致）；
    /// 调大更激进（能救极低对比度，但也更容易把噪点拉成前景）。
    /// </param>
    public static ImageFrame StretchContrast(ImageFrame source, int tailPermille = 5)
    {
        ArgumentNullException.ThrowIfNull(source);
        var gray = GrayOnWhite(source);
        Span<int> histogram = stackalloc int[256];
        histogram.Clear();
        foreach (var value in gray) histogram[value]++;
        var tail = (int)((long)gray.Length * Math.Clamp(tailPermille, 0, 500) / 1000);
        var low = 0;
        var high = 255;
        var count = 0;
        while (low < 255 && count + histogram[low] <= tail) count += histogram[low++];
        count = 0;
        while (high > low && count + histogram[high] <= tail) count += histogram[high--];
        var range = high - low;
        if (range <= 0 || range >= 192) return source;
        var output = new byte[gray.Length * 4];
        for (var i = 0; i < gray.Length; i++)
        {
            var value = (byte)Math.Clamp((gray[i] - low) * 255 / range, 0, 255);
            var p = i * 4;
            output[p] = output[p + 1] = output[p + 2] = value;
            output[p + 3] = 255;
        }
        return new ImageFrame(source.Width, source.Height, output);
    }

    /// <summary>
    /// 用局部均值消除缓慢变化的阴影。滚动列和实现 O(像素数)，仅需 O(宽度) 的额外工作内存。
    /// 窗口大小随图片尺寸变化，不做缩放，因此定位结果仍直接对应原图。
    /// </summary>
    /// <param name="radiusDivisor">
    /// 窗口半径 = <c>min(宽, 高) / radiusDivisor</c>，再夹在 8…64。默认 <b>12</b>（历史行为）；
    /// 调大 = 窗口更小 = 更贴合细节但更容易被噪点干扰。
    /// </param>
    /// <param name="bias">
    /// 判定为前景需要比局部均值暗多少个"每像素单位"。默认 <b>2</b>（历史行为）；
    /// 调大更保守（前景更少），调小更激进。
    /// </param>
    public static ImageFrame AdaptiveThreshold(ImageFrame source, int radiusDivisor = 12, int bias = 2)
    {
        ArgumentNullException.ThrowIfNull(source);
        var gray = GrayOnWhite(source);
        var min = gray.Min();
        var max = gray.Max();
        if (max - min < 2) return source;
        var width = source.Width;
        var height = source.Height;
        var radius = Math.Clamp(Math.Min(width, height) / Math.Max(1, radiusDivisor), 8, 64);
        var thresholdBias = Math.Max(0, bias);
        var columns = new int[width];
        var output = new byte[gray.Length * 4];
        for (var y = 0; y < Math.Min(height, radius); y++)
        for (var x = 0; x < width; x++) columns[x] += gray[y * width + x];
        for (var y = 0; y < height; y++)
        {
            var add = y + radius;
            var remove = y - radius - 1;
            for (var x = 0; x < width; x++)
            {
                if (add < height) columns[x] += gray[add * width + x];
                if (remove >= 0) columns[x] -= gray[remove * width + x];
            }
            var rows = Math.Min(height - 1, y + radius) - Math.Max(0, y - radius) + 1;
            var sum = 0;
            for (var x = 0; x < Math.Min(width, radius); x++) sum += columns[x];
            for (var x = 0; x < width; x++)
            {
                if (x + radius < width) sum += columns[x + radius];
                if (x - radius - 1 >= 0) sum -= columns[x - radius - 1];
                var area = rows * (Math.Min(width - 1, x + radius) - Math.Max(0, x - radius) + 1);
                var i = y * width + x;
                var value = (byte)(gray[i] * area < sum - (long)thresholdBias * area ? 0 : 255);
                var p = i * 4;
                output[p] = output[p + 1] = output[p + 2] = value;
                output[p + 3] = 255;
            }
        }
        return new ImageFrame(width, height, output);
    }

    private static byte[] GrayOnWhite(ImageFrame source)
    {
        var gray = source.ToGray8();
        for (var i = 0; i < gray.Length; i++)
        {
            var alpha = source.Pixels[i * 4 + 3];
            gray[i] = (byte)((gray[i] * alpha + 255 * (255 - alpha) + 127) / 255);
        }
        return gray;
    }

    /// <summary>
    /// 把"彩色"的像素用周围的<b>非彩色</b>像素填掉.
    /// <para>
    /// 截图上的高亮框、批注、水印基本都是彩色的，而二维码本身是黑白的 ——
    /// 按"颜色"把它们挑出来、再拿邻域里的灰阶内容补上，那条线就从图里消失了，
    /// 定位图形也就不会被打断。（**原图读不出来时才用**，所以不怕误伤彩色二维码。）
    /// </para>
    /// <para>
    /// 中值滤波对付不了这个：识别页的框线有 3px 宽，要抹掉得用 7x7 的窗口，
    /// 那会把二维码的模块一起糊掉。按颜色挑则是一条线一个准。
    /// </para>
    /// </summary>
    /// <param name="saturationThreshold">
    /// 判定"彩色"的阈值：<c>max(R,G,B) - min(R,G,B)</c> 超过它就算彩色。
    /// 黑白灰是 0，抗锯齿的边通常也就十几，实测识别框的 <c>#4C8BF5</c> 是 169。
    /// </param>
    public static ImageFrame RemoveColoredOverlay(ImageFrame source, int saturationThreshold = 48)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(saturationThreshold);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(saturationThreshold, 255);

        var width = source.Width;
        var height = source.Height;
        var pixels = source.Pixels;

        bool[]? colored = null;

        for (var i = 0; i < width * height; i++)
        {
            var offset = i * 4;
            var b = pixels[offset];
            var g = pixels[offset + 1];
            var r = pixels[offset + 2];

            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));

            if (max - min > saturationThreshold)
            {
                colored ??= new bool[width * height];
                colored[i] = true;
            }
        }

        if (colored is null)
        {
            return source;   // 没有彩色叠加，原样返回（也就不会白跑一遍）
        }

        byte[]? output = null;

        const int Radius = 2;   // 5x5 邻域
        Span<byte> blue = stackalloc byte[25];
        Span<byte> green = stackalloc byte[25];
        Span<byte> red = stackalloc byte[25];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * width) + x;
                if (!colored[index])
                {
                    continue;
                }

                var offset = index * 4;

                var count = 0;
                for (var py = Math.Max(0, y - Radius); py <= Math.Min(height - 1, y + Radius); py++)
                for (var px = Math.Max(0, x - Radius); px <= Math.Min(width - 1, x + Radius); px++)
                {
                    var neighbour = py * width + px;
                    if (colored[neighbour]) continue;
                    var p = neighbour * 4;
                    blue[count] = pixels[p];
                    green[count] = pixels[p + 1];
                    red[count++] = pixels[p + 2];
                }
                // 缺少干净邻居时没有依据重建，保留原像素，避免把大片彩色区域直接涂白。
                if (count == 0) continue;
                blue[..count].Sort();
                green[..count].Sort();
                red[..count].Sort();
                output ??= (byte[])pixels.Clone();
                output[offset] = blue[count / 2];
                output[offset + 1] = green[count / 2];
                output[offset + 2] = red[count / 2];
            }
        }

        return output is null ? source : new ImageFrame(width, height, output);
    }
}
