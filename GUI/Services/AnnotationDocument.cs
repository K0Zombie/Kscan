using SkiaSharp;

namespace Kscan.App.Services;

/// <summary>编辑数据与事务历史；一次拖动、一次属性应用各占一个撤销步骤。</summary>
internal sealed class AnnotationDocument
{
    private readonly List<AnnotationItem> _items = [];
    private readonly HashSet<int> _selection = [];
    private readonly Stack<List<AnnotationItem>> _undo = new();
    private readonly Stack<List<AnnotationItem>> _redo = new();
    private List<AnnotationItem>? _before;
    public IReadOnlyList<AnnotationItem> Items => _items;
    public IReadOnlyCollection<int> Selection => _selection;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int NextNumber => _items.Where(x => x.Tool == AnnotationTool.Number).Select(x => x.Number).DefaultIfEmpty(0).Max() + 1;
    public event Action? Changed;

    public static AnnotationItem Copy(AnnotationItem item) => new()
    {
        Tool = item.Tool, Points = [.. item.Points], Color = item.Color, Thickness = item.Thickness,
        Text = item.Text, Number = item.Number, Block = item.Block, TextWidth = item.TextWidth, FontSize = item.FontSize,
    };

    public void Begin()
    {
        _before ??= _items.Select(Copy).ToList();
    }

    public void Commit()
    {
        if (_before is null) return;
        if (!Same(_before, _items))
        {
            _undo.Push(_before);
            _redo.Clear();
        }
        _before = null;
        Changed?.Invoke();
    }

    public void Cancel()
    {
        if (_before is not { } before) return;
        _before = null;
        Restore(before);
    }

    public void Add(AnnotationItem item)
    {
        Begin();
        if (item.Tool == AnnotationTool.Crop) _items.RemoveAll(x => x.Tool == AnnotationTool.Crop);
        _items.Add(Copy(item));
        _selection.Clear();
        Commit();
    }

    public void Select(int index, bool toggle = false)
    {
        if (!toggle) _selection.Clear();
        if (index >= 0 && index < _items.Count && !_selection.Add(index)) _selection.Remove(index);
        Changed?.Invoke();
    }

    public void SelectArea(SKRect area, bool additive)
    {
        if (!additive) _selection.Clear();
        for (var i = 0; i < _items.Count; i++)
        {
            var b = Bounds(_items[i]);
            if (b.Right >= area.Left && b.Left <= area.Right && b.Bottom >= area.Top && b.Top <= area.Bottom)
                _selection.Add(i);
        }
        Changed?.Invoke();
    }

    public void SelectAll()
    {
        _selection.Clear();
        _selection.UnionWith(Enumerable.Range(0, _items.Count));
        Changed?.Invoke();
    }

    public void Delete()
    {
        if (_selection.Count == 0) return;
        Begin();
        foreach (var i in _selection.OrderDescending()) _items.RemoveAt(i);
        _selection.Clear();
        Commit();
    }

    public void Clear()
    {
        Begin();
        _items.Clear();
        _selection.Clear();
        Commit();
    }

    public void ApplyStyle(uint color, float thickness, int block, string? text = null, float? fontSize = null, float? textWidth = null)
    {
        if (_selection.Count == 0) return;
        Begin();
        foreach (var i in _selection)
        {
            var item = _items[i];
            _items[i] = new AnnotationItem
            {
                Tool = item.Tool, Points = [.. item.Points], Color = color, Thickness = thickness,
                Block = block, Text = item.Tool == AnnotationTool.Text && text is not null ? text : item.Text,
                TextWidth = textWidth ?? item.TextWidth, FontSize = fontSize ?? item.FontSize, Number = item.Number,
            };
        }
        Commit();
    }

    public void Move(float dx, float dy)
    {
        if (_selection.Count == 0) return;
        Begin();
        foreach (var i in _selection)
        {
            var points = _items[i].Points;
            for (var p = 0; p < points.Count; p++) points[p] = new SKPoint(points[p].X + dx, points[p].Y + dy);
        }
        Changed?.Invoke();
    }

    public void Resize(int index, int handle, SKPoint point)
    {
        if (index < 0 || index >= _items.Count) return;
        var item = _items[index];
        if (item.Points.Count != 2) return;
        Begin();
        item.Points[handle] = point;
        Changed?.Invoke();
    }

    public void Undo()
    {
        Cancel();
        if (!_undo.TryPop(out var previous)) return;
        _redo.Push(_items.Select(Copy).ToList());
        Restore(previous);
    }

    public void Redo()
    {
        Cancel();
        if (!_redo.TryPop(out var next)) return;
        _undo.Push(_items.Select(Copy).ToList());
        Restore(next);
    }

    private void Restore(List<AnnotationItem> snapshot)
    {
        _items.Clear();
        _items.AddRange(snapshot.Select(Copy));
        _selection.Clear();
        Changed?.Invoke();
    }

    public static SKRect Bounds(AnnotationItem item)
    {
        if (item.Tool == AnnotationTool.Text) return AnnotationRenderer.MeasureTextBlock(item);
        var b = item.Bounds();
        var pad = item.Tool == AnnotationTool.Number ? Math.Max(12, item.Thickness * 3.2f) : Math.Max(1, item.Thickness / 2);
        b.Inflate(pad, pad);
        return b;
    }

    public int HitTest(SKPoint point, float tolerance)
    {
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            var pad = Math.Max(tolerance, item.Tool == AnnotationTool.Highlight ? item.Thickness * 1.5f : item.Thickness);
            if (item.Tool is AnnotationTool.Line or AnnotationTool.Arrow or AnnotationTool.Dimension or AnnotationTool.Pen or AnnotationTool.Highlight or AnnotationTool.Polyline)
            {
                for (var p = 1; p < item.Points.Count; p++)
                    if (Distance(point, item.Points[p - 1], item.Points[p]) <= pad) return i;
            }
            else
            {
                var b = Bounds(item);
                b.Inflate(tolerance, tolerance);
                if (b.Contains(point)) return i;
            }
        }
        return -1;
    }

    private static float Distance(SKPoint p, SKPoint a, SKPoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / Math.Max(.0001f, dx * dx + dy * dy), 0, 1);
        return SKPoint.Distance(p, new SKPoint(a.X + dx * t, a.Y + dy * t));
    }

    private static bool Same(List<AnnotationItem> a, List<AnnotationItem> b) => a.Count == b.Count && a.Zip(b).All(pair =>
        pair.First.Tool == pair.Second.Tool && pair.First.Color == pair.Second.Color && pair.First.Thickness == pair.Second.Thickness
        && pair.First.Text == pair.Second.Text && pair.First.Block == pair.Second.Block && pair.First.Number == pair.Second.Number
        && pair.First.TextWidth == pair.Second.TextWidth && pair.First.FontSize == pair.Second.FontSize && pair.First.Points.SequenceEqual(pair.Second.Points));
}
