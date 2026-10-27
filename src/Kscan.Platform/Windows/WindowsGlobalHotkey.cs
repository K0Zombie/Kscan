using System.Runtime.InteropServices;
using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Windows;

/// <summary>
/// Windows 全局热键：<c>RegisterHotKey</c> + 一条专属的消息循环线程；一批热键一次注册
/// （同一动作的多条也各占一个 id，一条失败不影响另一条）。
/// <para>
/// 为什么要单独一条线程：<c>RegisterHotKey(hWnd: NULL, ...)</c> 是把热键<b>绑到调用线程的消息队列</b>上的，
/// 而消息循环（<c>GetMessage</c>）会阻塞，不能放在 UI 线程上跟 Avalonia 抢。
/// 线程退出时自己把注册过的热键全部解绑。
/// </para>
/// <para>
/// 每条热键还会额外注册一个"原组合 + 强制修饰键"的变体（例如 <c>F2</c> 之外再注册 <c>Ctrl+Alt+F2</c>）：
/// <c>RegisterHotKey</c> 是精确匹配修饰键的，只有这样才能实现"排除应用里按住 Ctrl+Alt 强制触发"。
/// </para>
/// </summary>
internal sealed class WindowsGlobalHotkey : IGlobalHotkey
{
    /// <summary>WM_HOTKEY。</summary>
    private const uint WmHotkey = 0x0312;

    /// <summary>WM_QUIT：投给线程自己，用来让 GetMessage 返回 0、结束循环。</summary>
    private const uint WmQuit = 0x0012;

    private const uint ModAlt = 0x0001;

    private const uint ModControl = 0x0002;

    private const uint ModShift = 0x0004;

    private const uint ModWin = 0x0008;

    /// <summary>长按不重复触发（否则按住热键会不停弹框选）。</summary>
    private const uint ModNoRepeat = 0x4000;

    /// <summary>ERROR_HOTKEY_ALREADY_REGISTERED。</summary>
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private Thread? _thread;

    private uint _threadId;

    // 热键线程写、UI 线程读：字段声明为 volatile，写入后靠 TaskCompletionSource 的等待建立可见性
    private volatile IReadOnlyList<GlobalHotkeyBinding> _registered = [];

    private volatile IReadOnlyDictionary<int, (string ActionId, bool Forced)> _idToRoute =
        new Dictionary<int, (string ActionId, bool Forced)>();

    public bool IsSupported => true;

    public string Description => "RegisterHotKey（系统级，任何窗口在前台都有效）";

    public string? DescriptionKey => "Platform.Hotkey.Windows";

    public IReadOnlyList<GlobalHotkeyBinding> Registered => _registered;

    public event EventHandler<GlobalHotkeyPressedEventArgs>? Pressed;

    public IReadOnlyList<GlobalHotkeyFailure> Apply(IReadOnlyList<GlobalHotkeyBinding> hotkeys)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);

        StopThread();

        var failures = new List<GlobalHotkeyFailure>();
        var plan = new List<(int Id, GlobalHotkeyBinding Binding)>();
        var nextId = 1;

        foreach (var binding in hotkeys)
        {
            if (VirtualKeyFor(binding.Hotkey.Key) is null)
            {
                // 连虚拟键码都拿不到，压根不用起线程
                failures.Add(new GlobalHotkeyFailure(
                    binding.ActionId,
                    binding.Hotkey.ToString(),
                    $"这个键在 Windows 上没法注册：{binding.Hotkey.Key}"));
                continue;
            }

            plan.Add((nextId++, binding));
        }

        if (plan.Count == 0)
        {
            _registered = [];
            _idToRoute = new Dictionary<int, (string ActionId, bool Forced)>();
            return failures;
        }

        var ready = new TaskCompletionSource<List<GlobalHotkeyFailure>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() => RunHotkeyThread(plan, ready))
        {
            IsBackground = true,
            Name = "kscan-global-hotkey",
        };

        thread.Start();

        // 等 ready 也顺便建立了内存屏障：线程里对 _registered / _idToRoute 的写入在下面可见
        failures.AddRange(ready.Task.GetAwaiter().GetResult());

        _thread = thread;

        // 一条都没成功：线程已经退出，Join 收干净（否则它会挂着等消息）
        if (_registered.Count == 0)
        {
            StopThread();
        }

        return failures;
    }

    public void UnregisterAll()
    {
        StopThread();

        _registered = [];
        _idToRoute = new Dictionary<int, (string ActionId, bool Forced)>();
    }

    private void StopThread()
    {
        var thread = _thread;
        if (thread is null)
        {
            return;
        }

        _thread = null;

        // 让该线程的 GetMessage 返回 0：线程随后自己 UnregisterHotKey 再退出
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        thread.Join(TimeSpan.FromSeconds(2));
        _threadId = 0;
    }

    private void RunHotkeyThread(
        List<(int Id, GlobalHotkeyBinding Binding)> plan,
        TaskCompletionSource<List<GlobalHotkeyFailure>> ready)
    {
        _threadId = GetCurrentThreadId();

        var results = new List<GlobalHotkeyFailure>();
        var registeredIds = new List<int>();
        var idToRoute = new Dictionary<int, (string ActionId, bool Forced)>();
        var registered = new List<GlobalHotkeyBinding>();

        // 强制变体用独立的 id 段，避免和主键 id 撞车
        var nextForcedId = plan.Count + 1;

        foreach (var (id, binding) in plan)
        {
            var virtualKey = VirtualKeyFor(binding.Hotkey.Key)!.Value;
            var modifiers = ToWinModifiers(binding.Hotkey.Modifiers);

            if (!RegisterHotKey(IntPtr.Zero, id, modifiers | ModNoRepeat, virtualKey))
            {
                var errorCode = Marshal.GetLastWin32Error();

                results.Add(new GlobalHotkeyFailure(
                    binding.ActionId,
                    binding.Hotkey.ToString(),
                    errorCode == ErrorHotkeyAlreadyRegistered
                        ? $"{binding.Hotkey} 已被其它程序占用，换一个键试试。"
                        : $"注册 {binding.Hotkey} 失败（Win32 错误 {errorCode}）。"));
                continue;
            }

            registeredIds.Add(id);
            idToRoute[id] = (binding.ActionId, false);
            registered.Add(binding);

            RegisterForcedVariant(binding, virtualKey, idToRoute, registeredIds, ref nextForcedId);
        }

        _idToRoute = idToRoute;
        _registered = registered;

        ready.TrySetResult(results);

        if (registeredIds.Count == 0)
        {
            return;   // 全失败，没必要跑消息循环
        }

        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.Message != WmHotkey)
            {
                continue;
            }

            // WM_HOTKEY 的 wParam 就是注册时给的 id
            var id = (int)message.WParam.ToInt64();

            if (idToRoute.TryGetValue(id, out var route))
            {
                // 订阅方负责投递到 UI 线程（这里不是 UI 线程）
                Pressed?.Invoke(this, new GlobalHotkeyPressedEventArgs(route.ActionId, route.Forced));
            }
        }

        foreach (var id in registeredIds)
        {
            UnregisterHotKey(IntPtr.Zero, id);
        }
    }

    /// <summary>
    /// 注册"原组合 + 强制修饰键"的变体。注册失败（被占用等）静默忽略：主键仍然有效，
    /// 只是排除应用里没法用强制修饰键触发 —— 不值得为此整条热键报错。
    /// </summary>
    private static void RegisterForcedVariant(
        GlobalHotkeyBinding binding,
        uint virtualKey,
        Dictionary<int, (string ActionId, bool Forced)> idToRoute,
        List<int> registeredIds,
        ref int nextForcedId)
    {
        if (binding.ForceModifiers == HotkeyModifiers.None)
        {
            return;
        }

        var forcedModifiers = binding.Hotkey.Modifiers | binding.ForceModifiers;

        // 热键本身已经带了全部强制修饰键（例如 Ctrl+Alt+F5）—— 变体和主键一样，不用再注册
        if (forcedModifiers == binding.Hotkey.Modifiers)
        {
            return;
        }

        var forcedId = nextForcedId++;

        if (RegisterHotKey(IntPtr.Zero, forcedId, ToWinModifiers(forcedModifiers) | ModNoRepeat, virtualKey))
        {
            registeredIds.Add(forcedId);
            idToRoute[forcedId] = (binding.ActionId, true);
        }
    }

    private static uint ToWinModifiers(HotkeyModifiers modifiers)
    {
        var value = 0u;

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            value |= ModAlt;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            value |= ModControl;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            value |= ModShift;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Win))
        {
            value |= ModWin;
        }

        return value;
    }

    /// <summary>键名 → 虚拟键码。认不出返回 <c>null</c>。</summary>
    private static uint? VirtualKeyFor(string key)
    {
        if (key.Length == 1)
        {
            var character = char.ToUpperInvariant(key[0]);
            return char.IsAsciiLetterOrDigit(character) ? character : null;
        }

        if (key.Length >= 2
            && key[0] == 'F'
            && int.TryParse(key[1..], out var number)
            && number is >= 1 and <= 24)
        {
            return (uint)(0x70 + number - 1);
        }

        return key switch
        {
            "Space" => 0x20,
            "Enter" => 0x0D,
            "Tab" => 0x09,
            "Esc" => 0x1B,
            "Backspace" => 0x08,
            "Delete" => 0x2E,
            "Insert" => 0x2D,
            "Home" => 0x24,
            "End" => 0x23,
            "PageUp" => 0x21,
            "PageDown" => 0x22,
            "Left" => 0x25,
            "Right" => 0x27,
            "Up" => 0x26,
            "Down" => 0x28,
            _ => null,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Handle;

        public uint Message;

        public IntPtr WParam;

        public IntPtr LParam;

        public uint Time;

        public int PointX;

        public int PointY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
