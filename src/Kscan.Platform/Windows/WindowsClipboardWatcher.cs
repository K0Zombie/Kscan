using System.Runtime.InteropServices;
using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Windows;

/// <summary>
/// Windows 剪贴板监听：轮询 <c>GetClipboardSequenceNumber</c>。
/// <para>
/// 这个 API 每次剪贴板内容变化就 +1，读一个 DWORD 的开销可以忽略；
/// 不需要挂窗口消息（<c>AddClipboardFormatListener</c> 要 HWND，Avalonia 不暴露），也不需要钩子。
/// </para>
/// </summary>
internal sealed class WindowsClipboardWatcher : IClipboardWatcher
{
    /// <summary>轮询间隔：够快（人感觉是"立刻"），又完全不至于有可测的开销。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(700);

    private readonly object _gate = new();

    private Timer? _timer;
    private uint _lastSequence;

    public bool IsSupported => OperatingSystem.IsWindows();

    public string Description => "Windows 剪贴板序号轮询（约 0.7 秒一次）";

    public string? DescriptionKey => "Platform.Clipboard.Windows";

    public event EventHandler? Changed;

    public void Start()
    {
        if (!IsSupported)
        {
            return;
        }

        lock (_gate)
        {
            if (_timer is not null)
            {
                return;
            }

            _lastSequence = GetClipboardSequenceNumber();
            _timer = new Timer(_ => Poll(), null, Interval, Interval);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    public long? SequenceNumber => IsSupported ? GetClipboardSequenceNumber() : null;

    public void MarkHandled()
    {
        if (IsSupported)
        {
            _lastSequence = GetClipboardSequenceNumber();
        }
    }

    private void Poll()
    {
        uint current;

        try
        {
            current = GetClipboardSequenceNumber();
        }
        catch (Exception)
        {
            // 极端情况下（会话切换等）读不到就跳过这一轮，不影响计时器继续跑
            return;
        }

        lock (_gate)
        {
            if (current == _lastSequence)
            {
                return;
            }

            // 先记账再回调：回调里可能又去写剪贴板，不能让下一轮重复触发
            _lastSequence = current;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    [DllImport("user32.dll", SetLastError = false)]
    private static extern uint GetClipboardSequenceNumber();
}
