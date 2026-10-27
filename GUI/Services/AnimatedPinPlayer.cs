using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Kscan.App.Services;

/// <summary>
/// 动图播放器：按每帧的延时推进，把当前帧交给贴图窗口显示。
/// <para>
/// 帧数据归它所有（<see cref="Dispose"/> 时释放）；窗口关掉必须 Dispose，否则大 GIF 会一直占着内存。
/// </para>
/// <para>
/// <see cref="Advance"/> 是 internal：测试可以直接推帧，不用等真实时间。
/// </para>
/// </summary>
internal sealed class AnimatedPinPlayer : IDisposable
{
    private readonly IReadOnlyList<Bitmap> _frames;
    private readonly IReadOnlyList<int> _delays;

    private DispatcherTimer? _timer;
    private int _index;
    private bool _disposed;

    public AnimatedPinPlayer(IReadOnlyList<Bitmap> frames, IReadOnlyList<int> delays)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(delays);

        if (frames.Count == 0 || frames.Count != delays.Count)
        {
            throw new ArgumentException("帧与延时数量必须一致且非空");
        }

        _frames = frames;
        _delays = delays;
    }

    /// <summary>每推进一帧触发一次（参数是新的当前帧）。</summary>
    public event EventHandler<Bitmap>? FrameShown;

    public Bitmap Current => _frames[_index];

    public int FrameCount => _frames.Count;

    public int Index => _index;

    public bool IsRunning => _timer?.IsEnabled == true;

    /// <summary>是否被"定格"了（按住修饰键时）。暂停期间仍显示当前帧。</summary>
    public bool IsPaused { get; private set; }

    /// <summary>显示第一帧并开始循环播放。</summary>
    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        _index = 0;
        IsPaused = false;
        FrameShown?.Invoke(this, Current);
        ScheduleNext();
    }

    public void Stop()
    {
        _timer?.Stop();
        IsPaused = false;
    }

    /// <summary>定格：停表，画面保持在当前帧（不重置帧号）。</summary>
    public void Pause()
    {
        if (_disposed)
        {
            return;
        }

        IsPaused = true;
        _timer?.Stop();
    }

    /// <summary>解除定格：从**当前帧**继续，不从头开始。</summary>
    public void Resume()
    {
        if (_disposed || !IsPaused)
        {
            return;
        }

        IsPaused = false;
        ScheduleNext();
    }

    /// <summary>推进到下一帧（循环）并继续排下一次。</summary>
    internal void Advance()
    {
        if (_disposed)
        {
            return;
        }

        _index = (_index + 1) % _frames.Count;
        FrameShown?.Invoke(this, Current);
        ScheduleNext();
    }

    private void ScheduleNext()
    {
        _timer ??= new DispatcherTimer();
        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(_delays[_index]);
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e) => Advance();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Stop();

        if (_timer is not null)
        {
            _timer.Tick -= OnTick;
            _timer = null;
        }

        foreach (var frame in _frames)
        {
            // 当前帧可能已经被 Image 换下去；统一释放，窗口那边会先清掉 Source
            frame.Dispose();
        }
    }
}
