using System.Runtime.InteropServices;
using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Windows;

/// <summary>
/// Windows 全局鼠标手势：用 <c>WH_MOUSE_LL</c> 低级鼠标钩子监听"修饰键 + 拖拽"。
/// <para>
/// 为什么不用 <c>RegisterHotKey</c>：它只能注册"键盘组合键"，拿不到鼠标移动，
/// 而"按住 Win 拖一下"这个手势必须有鼠标位移才能与普通单击区分开。
/// </para>
/// <para>
/// 低级钩子会**阻塞**鼠标输入直到回调返回，所以回调里只做判断、绝不做耗时操作；
/// 真正的工作由订阅方异步处理。
/// </para>
/// </summary>
internal sealed class WindowsMouseGesture : IMouseGesture
{
    private const int WhMouseLl = 14;

    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;

    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    /// <summary>保持强引用：委托被 GC 回收会导致钩子崩溃。</summary>
    private readonly HookProc _callback;

    private IntPtr _hook;
    private MouseGestureOptions _options = MouseGestureOptions.Default;
    private bool _buttonDown;
    private int _originX;
    private int _originY;
    private bool _dragged;

    public WindowsMouseGesture() => _callback = HookCallback;

    public bool IsSupported => OperatingSystem.IsWindows();

    public string Description => "全局鼠标手势（Win + 拖动左键）";

    public string CurrentGesture { get; private set; } = string.Empty;

    public event EventHandler? Triggered;

    public bool Start(MouseGestureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!IsSupported)
        {
            return false;
        }

        Stop();

        _options = options;
        CurrentGesture = options.Describe();

        try
        {
            // 低级钩子挂在当前线程上，因此必须在有消息循环的 UI 线程调用
            _hook = SetWindowsHookEx(WhMouseLl, _callback, GetModuleHandle(null), 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }

        return _hook != IntPtr.Zero;
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            try
            {
                UnhookWindowsHookEx(_hook);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // 忽略：进程退出时 DLL 可能已卸载
            }

            _hook = IntPtr.Zero;
        }

        _buttonDown = false;
        _dragged = false;
        CurrentGesture = string.Empty;
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        // 回调里出任何异常都可能拖垮整个进程，全部吞掉
        try
        {
            if (code >= 0)
            {
                Handle((int)wParam, Marshal.PtrToStructure<MsllHookStruct>(lParam));
            }
        }
        catch
        {
            // 宁可漏一次手势，也不能影响鼠标输入
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void Handle(int message, MsllHookStruct data)
    {
        var (down, up) = MessageFor(_options.Button, data.mouseData);

        if (message == down && ModifiersMatch())
        {
            _buttonDown = true;
            _dragged = false;
            _originX = data.pt.X;
            _originY = data.pt.Y;
            return;
        }

        if (message == WmMouseMove && _buttonDown)
        {
            var dx = Math.Abs(data.pt.X - _originX);
            var dy = Math.Abs(data.pt.Y - _originY);
            if (dx >= _options.DragThreshold || dy >= _options.DragThreshold)
            {
                _dragged = true;
            }

            return;
        }

        if (message == up && _buttonDown)
        {
            _buttonDown = false;

            if (_dragged)
            {
                _dragged = false;
                Triggered?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// 注意：XButton 的按下/抬起共用同一个消息，靠 mouseData 区分子键；
    /// 这里只处理三个主键。
    /// </summary>
    private static (int Down, int Up) MessageFor(MouseGestureButton button, uint mouseData) => button switch
    {
        MouseGestureButton.Middle => (WmMButtonDown, WmMButtonUp),
        MouseGestureButton.Right => (WmRButtonDown, WmRButtonUp),
        _ => (WmLButtonDown, WmLButtonUp),
    };

    private bool ModifiersMatch()
    {
        if (_options.RequireWinKey && !IsVkDown(VkLWin) && !IsVkDown(VkRWin))
        {
            return false;
        }

        if (_options.RequireCtrl && !IsVkDown(VkControl))
        {
            return false;
        }

        if (_options.RequireAlt && !IsVkDown(VkMenu))
        {
            return false;
        }

        if (_options.RequireShift && !IsVkDown(VkShift))
        {
            return false;
        }

        return true;
    }

    private static bool IsVkDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public Point pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
