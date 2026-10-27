namespace Kscan.Core.Imaging;

public enum ScrollAppendStatus { Added, Unchanged, NoOverlap, Ambiguous, Reverse, SizeChanged, LimitReached }
public readonly record struct ScrollAppendResult(ScrollAppendStatus Status, int AddedRows = 0, bool AddedAtTop = false);

/// <summary>双向纵向拼接。跟踪当前画面在长图中的位置，仅保留范围外的新增像素条带。</summary>
public sealed class ScrollStitcher
{
    public const long DefaultMaxPixels = 128_000_000;
    public const int DefaultMaxHeight = 100_000;
    private const int Columns = 48;
    private readonly List<ImageFrame> _parts = [];
    private readonly long _maxPixels;
    private readonly int _maxHeight;
    private ImageFrame _last;
    private byte[] _lastSample;
    private int _anchorTop;
    private readonly Stack<(bool AtTop, int Rows, int PreviousAnchor)> _history = new();
    public int Width => _parts[0].Width;
    public int Height { get; private set; }
    public int PartCount => _parts.Count;
    public bool CanUndo => _history.Count > 0;
    public int HeightLimit => (int)Math.Min(_maxHeight, _maxPixels / Width);

    public ScrollStitcher(ImageFrame first, long maxPixels = DefaultMaxPixels, int maxHeight = DefaultMaxHeight)
    {
        ArgumentNullException.ThrowIfNull(first);
        if ((long)first.Width * first.Height > maxPixels || first.Height > maxHeight)
            throw new ArgumentException("初始选区超过长图像素上限。", nameof(first));
        _parts.Add(first);
        _last = first;
        _lastSample = Sample(first);
        Height = first.Height;
        _maxPixels = maxPixels;
        _maxHeight = maxHeight;
    }

    public ScrollAppendResult TryAppend(ImageFrame frame, CancellationToken cancellationToken = default)
    {
        if (frame.Width != Width || frame.Height != _last.Height) return new(ScrollAppendStatus.SizeChanged);
        var previous = _lastSample;
        var current = Sample(frame);
        if (Difference(previous, current) < .45) return new(ScrollAppendStatus.Unchanged);
        var match = Find(previous, current, frame.Height, cancellationToken);
        var delta = match.Offset;
        if (match.Offset == 0)
        {
            var reverse = Find(current, previous, frame.Height, cancellationToken);
            if (reverse.Offset == 0) return new(ScrollAppendStatus.NoOverlap);
            if (reverse.Ambiguous) return new(ScrollAppendStatus.Ambiguous);
            delta = -reverse.Offset;
        }
        if (match.Ambiguous) return new(ScrollAppendStatus.Ambiguous);
        var top = _anchorTop + delta;
        var atTop = top < 0;
        var addedRows = atTop ? -top : Math.Max(0, top + frame.Height - Height);
        var height = Height + addedRows;
        if (height > _maxHeight || (long)Width * height > _maxPixels) return new(ScrollAppendStatus.LimitReached);
        if (addedRows > 0)
        {
            var strip = frame.Crop(0, atTop ? 0 : frame.Height - addedRows, Width, addedRows);
            _history.Push((atTop, addedRows, _anchorTop));
            if (atTop) _parts.Insert(0, strip);
            else _parts.Add(strip);
        }
        _anchorTop = Math.Max(0, top);
        _last = frame;
        _lastSample = current;
        Height = height;
        return addedRows > 0 ? new(ScrollAppendStatus.Added, addedRows, atTop) : new(ScrollAppendStatus.Unchanged);
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        var change = _history.Pop();
        Height -= change.Rows;
        _parts.RemoveAt(change.AtTop ? 0 : _parts.Count - 1);
        _anchorTop = change.PreviousAnchor;
        _last = CopyRows(_anchorTop, _last.Height);
        _lastSample = Sample(_last);
        return true;
    }

    public ImageFrame Build() => CopyRows(0, Height);

    /// <summary>完成采集时合并并替换条带，让结果与内部存储共享像素，不继续保留一整份旧条带。</summary>
    public ImageFrame BuildAndCompact()
    {
        var frame = _parts.Count == 1 ? _parts[0] : Build();
        _parts.Clear();
        _parts.Add(frame);
        _history.Clear();
        return frame;
    }

    private ImageFrame CopyRows(int start, int count)
    {
        var pixels = new byte[checked(Width * count * 4)];
        var top = 0;
        foreach (var part in _parts)
        {
            var from = Math.Max(start, top);
            var to = Math.Min(start + count, top + part.Height);
            if (to > from) Buffer.BlockCopy(part.Pixels, (from - top) * part.Stride, pixels, (from - start) * part.Stride, (to - from) * part.Stride);
            top += part.Height;
            if (top >= start + count) break;
        }
        return new ImageFrame(Width, count, pixels);
    }

    /// <summary>直接从条带采样缩略图，不为每次预览分配完整长图。</summary>
    public ImageFrame Preview(int maxWidth = 120, int maxHeight = 180)
    {
        var scale = Math.Min(1, Math.Min((double)maxWidth / Width, (double)maxHeight / Height));
        var width = Math.Max(1, (int)(Width * scale));
        var height = Math.Max(1, (int)(Height * scale));
        var pixels = new byte[width * height * 4];
        var partIndex = 0;
        var partTop = 0;
        for (var y = 0; y < height; y++)
        {
            var row = (int)((long)y * Height / height);
            while (row >= partTop + _parts[partIndex].Height) { partTop += _parts[partIndex++].Height; }
            var part = _parts[partIndex];
            for (var x = 0; x < width; x++)
            {
                var column = (int)((long)x * Width / width);
                Buffer.BlockCopy(part.Pixels, ((row - partTop) * Width + column) * 4, pixels, (y * width + x) * 4, 4);
            }
        }
        return new ImageFrame(width, height, pixels);
    }

    private static byte[] Sample(ImageFrame frame)
    {
        var samples = new byte[frame.Height * Columns];
        for (var y = 0; y < frame.Height; y++)
            for (var col = 0; col < Columns; col++)
            {
                var x = Math.Clamp((int)(frame.Width * (.05 + .90 * col / (Columns - 1))), 0, frame.Width - 1);
                var p = (y * frame.Width + x) * 4;
                samples[y * Columns + col] = (byte)((frame.Pixels[p] * 114 + frame.Pixels[p + 1] * 587 + frame.Pixels[p + 2] * 299) / 1000);
            }
        return samples;
    }

    private static double Difference(byte[] a, byte[] b)
    {
        long sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }

    private static (int Offset, bool Ambiguous) Find(byte[] previous, byte[] current, int height, CancellationToken token)
    {
        var minimumOverlap = Math.Max(32, height / 20);
        if (minimumOverlap >= height) return (0, false);
        var candidates = new List<(int Offset, double Error)>();
        for (var offset = 1; offset <= height - minimumOverlap; offset++)
        {
            if ((offset & 31) == 0) token.ThrowIfCancellationRequested();
            var error = Score(previous, current, offset, height, detailed: false);
            if (error < 5) candidates.Add((offset, error));
        }
        if (candidates.Count == 0) return (0, false);
        var ordered = candidates.OrderBy(x => x.Error).Take(12).ToList();
        var detailed = ordered.Select(x => (x.Offset, Error: Score(previous, current, x.Offset, height, detailed: true))).OrderBy(x => x.Error).ToList();
        var best = detailed[0];
        if (best.Error > 2.5) return (0, false);
        var competitor = detailed.FirstOrDefault(x => Math.Abs(x.Offset - best.Offset) > 2);
        if (competitor.Offset > 0 && competitor.Error < best.Error + .7) return (best.Offset, true);
        return (best.Offset, false);
    }

    private static double Score(byte[] a, byte[] b, int offset, int height, bool detailed)
    {
        var overlap = height - offset;
        var rowStep = Math.Max(1, overlap / (detailed ? 280 : 100));
        var columnStep = detailed ? 1 : 3;
        long error = 0;
        long weight = 0;
        var textured = 0;
        long bandError = 0;
        long bandWeight = 0;
        // 剩余像素全部零误差、最大权重也无法达标时立即排除。
        // 这是误差下界，不会因为前几行有动态内容而误杀可靠匹配。
        var rows = Math.Max(0, (overlap - 5) / rowStep + 1);
        var columns = (Columns - 3) / columnStep + 1;
        var maximumError = 5L * rows * columns * 4;
        for (var y = 2; y < overlap - 2; y += rowStep)
        {
            for (var x = 1; x < Columns - 1; x += columnStep)
            {
                var ai = (y + offset) * Columns + x;
                var bi = y * Columns + x;
                var gradient = Math.Max(Math.Abs(a[ai] - a[ai - Columns]), Math.Abs(a[ai] - a[ai - 1]));
                var w = gradient > 14 ? 4 : 1;
                if (w > 1) textured++;
                var delta = Math.Abs(a[ai] - b[bi]);
                error += delta * w;
                weight += w;
                if (y >= overlap - Math.Max(16, overlap / 8)) { bandError += delta * w; bandWeight += w; }
            }
            if (error > maximumError) return 255;
        }
        if (textured < (detailed ? 24 : 8) || weight == 0) return 255;
        // 尾端不匹配常见于固定底栏，不能把底栏反复接进结果。
        if (detailed && bandWeight > 0 && (double)bandError / bandWeight > 5) return 255;
        return (double)error / weight;
    }
}
