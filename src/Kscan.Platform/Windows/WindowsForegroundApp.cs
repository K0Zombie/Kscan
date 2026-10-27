using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Windows;

/// <summary>
/// Windows 前台应用探测：前台窗口 → 进程，以及"屏幕某点下最上层窗口"（供框选排除应用用）。
/// <para>
/// 点选窗口<b>不用</b> <c>WindowFromPoint</c>：全屏遮罩窗口本身会挡住命中测试。
/// 这里沿 Z 序从上往下枚举顶层窗口，跳过遮罩句柄，找到第一个可见且包含该点的窗口 ——
/// 遮罩不需要鼠标穿透，点击也不会误落到目标窗口上。
/// </para>
/// </summary>
internal sealed class WindowsForegroundApp : IForegroundApp
{
    private const uint GwHwndNext = 2;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public bool IsSupported => OperatingSystem.IsWindows();

    public ForegroundAppInfo? GetForeground() => InfoForWindow(GetForegroundWindow());

    public ForegroundAppInfo? GetAt(int screenX, int screenY, IntPtr ignoreHandle = default) =>
        InfoForWindow(WindowAtPoint(screenX, screenY, ignoreHandle));

    /// <summary>
    /// 光标下窗口的<b>可见</b>矩形（DWM 扩展边框，不含 Vista 之后那圈透明拖拽边框）。
    /// 用 <c>GetWindowRect</c> 的话左 / 右 / 下会各多 7~8 px，整窗截图会多出一圈桌面。
    /// </summary>
    public ScreenRect? GetRectAt(int screenX, int screenY, IntPtr ignoreHandle = default) =>
        WindowAtPoint(screenX, screenY, ignoreHandle) is var handle && handle != IntPtr.Zero
            ? WindowsWindowGeometry.VisibleRect(handle)
            : null;

    /// <summary>已知句柄的可见矩形（语义同 <see cref="GetRectAt"/>，只是不再做命中测试）。</summary>
    public ScreenRect? GetRectOf(IntPtr handle) =>
        handle == IntPtr.Zero ? null : WindowsWindowGeometry.VisibleRect(handle);

    public IntPtr GetWindowHandleAt(int screenX, int screenY, IntPtr ignoreHandle = default) =>
        WindowAtPoint(screenX, screenY, ignoreHandle);

    /// <summary>
    /// 光标下的<b>元素链</b>（UIA）：从最外层子元素到最内层，不含窗口自身。
    /// 拿不到返回 <c>null</c>；没有子元素返回空链 —— 调用方据此退回窗口矩形。
    /// <para>
    /// 必须把<b>真实窗口句柄</b>交给 UIA：遮罩自己挡在最上面，<c>ElementFromPoint</c> 只会命中遮罩。
    /// </para>
    /// </summary>
    public IReadOnlyList<ScreenRect>? GetElementChainAt(int screenX, int screenY, IntPtr ignoreHandle = default) =>
        WindowsUiAutomation.GetElementChainAt(screenX, screenY, WindowAtPoint(screenX, screenY, ignoreHandle));

    /// <summary>
    /// 光标下最内层的 UI 元素（UIA）。拿不到时返回 <c>null</c>；
    /// 调用方再退回 <see cref="GetRectAt"/> 的窗口矩形。
    /// <para>
    /// 必须把<b>真实窗口句柄</b>交给 UIA：遮罩自己挡在最上面，<c>ElementFromPoint</c> 只会命中遮罩。
    /// </para>
    /// </summary>
    public ScreenRect? GetElementRectAt(int screenX, int screenY, IntPtr ignoreHandle = default) =>
        WindowsUiAutomation.GetElementRectAt(screenX, screenY, WindowAtPoint(screenX, screenY, ignoreHandle));

    /// <summary>
    /// 目标窗口是否被其它窗口挡住：Z 序排在它上面的可见窗口里，有矩形与它重叠的就算。
    /// <para>
    /// <paramref name="ignoreHandle"/> 传遮罩句柄 —— 遮罩是全屏 Topmost，不排除的话永远算"被挡住"。
    /// 最小化窗口的矩形在 (-32000,-32000)，与目标不会相交，不需要额外判断。
    /// </para>
    /// </summary>
    public bool IsWindowCovered(IntPtr handle, IntPtr ignoreHandle = default)
    {
        if (handle == IntPtr.Zero || WindowsWindowGeometry.VisibleRect(handle) is not { } targetRect)
        {
            return false;
        }

        // GetTopWindow 是最上层，GW_HWNDNEXT 逐层往下；碰到目标窗口就停 —— 中间那些都在它上面
        for (var current = GetTopWindow(IntPtr.Zero);
             current != IntPtr.Zero && current != handle;
             current = GetWindow(current, GwHwndNext))
        {
            if (current == ignoreHandle || !IsWindowVisible(current) || WindowsWindowGeometry.VisibleRect(current) is not { } other)
            {
                continue;
            }

            if (other.IntersectsWith(targetRect))
            {
                return true;
            }
        }

        return false;
    }

    public ScreenPoint? GetCursorPosition() => WindowsCursor.GetPosition();

    /// <summary>
    /// 被 DWM 藏起来的窗口（cloaked）对用户不可见，却会按矩形抢先命中 —— 全屏的"输入体验"壳就是这样
    /// 把桌面/任务栏/普通窗口全部挡掉的（用户报的"幽灵窗口""任务栏后永远全屏"）。
    /// 本进程例外：遮罩显示期间我们自己的主窗口也会被标成 cloaked，但它必须能被框选。
    /// </summary>
    private static bool IsCloakedOther(IntPtr handle)
    {
        _ = GetWindowThreadProcessId(handle, out var processId);
        if (processId == (uint)Environment.ProcessId)
        {
            return false;
        }

        return DwmGetWindowAttribute(handle, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    /// <summary>桌面本体（Progman / WorkerW / 无标题的 ApplicationFrameWindow）。</summary>
    public bool IsDesktopSurface(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var name = new System.Text.StringBuilder(64);
        GetClassName(handle, name, name.Capacity);

        return name.ToString() switch
        {
            "Progman" or "WorkerW" => true,
            "ApplicationFrameWindow" => GetWindowTextLength(handle) == 0,
            _ => false,
        };
    }

    /// <summary>任务栏（主 / 副屏）：它"看得见的矩形"被特意设成整屏，判全屏时要排除掉。</summary>
    public bool IsTaskbar(IntPtr handle) =>
        handle != IntPtr.Zero && WindowsWindowGeometry.IsTaskbar(handle);

    /// <summary>
    /// 光标下最上层窗口。
    /// <para>
    /// 这里以前有一段"每次命中都往 <c>probe.log</c> 追加一行"的调试代码 —— 单屏框选时只有鼠标移动
    /// 才探测，问题不明显；跨屏改为 40ms 悬停轮询后它会每秒往磁盘写二十多行（日志已经涨到 1.9MB）。
    /// 诊断信息改由调用方按需记录（见 <c>multiscreen: hover …</c>），这里保持纯净。
    /// </para>
    /// </summary>
    private static IntPtr WindowAtPoint(int screenX, int screenY, IntPtr ignoreHandle) =>
        WindowAtPointCore(screenX, screenY, ignoreHandle);

    private static IntPtr WindowAtPointCore(int screenX, int screenY, IntPtr ignoreHandle)
    {
        // Z 序最上层的顶层窗口开始；GetTopWindow(NULL) 就是最上面那个
        var handle = GetTopWindow(IntPtr.Zero);

        while (handle != IntPtr.Zero)
        {
            if (handle != ignoreHandle && !IsCloakedOther(handle) && IsWindowVisible(handle) && WindowsWindowGeometry.HitRect(handle) is { } visible
                && screenX >= visible.X && screenX < visible.X + visible.Width
                && screenY >= visible.Y && screenY < visible.Y + visible.Height)
            {
                return handle;
            }

            handle = GetWindow(handle, GwHwndNext);
        }

        return IntPtr.Zero;
    }

    private static ForegroundAppInfo? InfoForWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0)
        {
            return null;
        }

        var path = TryGetExecutablePath((int)processId);

        if (path is null)
        {
            // 拿不到路径时退回 Process.ProcessName（不带 .exe），保证至少能按进程名匹配
            try
            {
                using var process = Process.GetProcessById((int)processId);
                return new ForegroundAppInfo(process.ProcessName + ".exe", null);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return null;
            }
        }

        return new ForegroundAppInfo(Path.GetFileName(path), path);
    }

    private static string? TryGetExecutablePath(int processId)
    {
        // PROCESS_QUERY_LIMITED_INFORMATION 对同用户的提权进程也可用（这正是任务管理器场景需要的能力）
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size)
                ? buffer.ToString(0, (int)size)
                : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }



    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr h, StringBuilder name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);



    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder buffer, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
