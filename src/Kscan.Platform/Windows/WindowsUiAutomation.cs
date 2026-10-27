using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Windows;

/// <summary>
/// UI Automation：取光标下最内层元素（子控件：按钮、输入框、列表项……）的矩形。
/// 让框选截图可以"单击截一个元素"，而不只是整个窗口。
/// <para>
/// <b>不能只用 <c>ElementFromPoint</c></b>：框选遮罩是 Topmost 全屏窗口，UIA 命中的永远是遮罩
/// 自己的控件（实测命中的是光标图像那个 48x48 的 Image），排除本进程后元素识别就恒为 <c>null</c>。
/// 所以这里从<b>真实窗口句柄</b>出发（<c>ElementFromHandle</c>），再<b>逐层下降</b>：每层只取直接
/// 子元素（<c>TreeScope_Children</c>），在包含光标点的里面挑面积最小的，继续往下钻。
/// </para>
/// <para>
/// 逐层下降而不是一次 <c>TreeScope_Descendants</c> 全量枚举：子元素总数可能上千，每个都要跨进程
/// 读一次属性，遮罩上 50ms 一次的探测会被拖到卡顿。拿不到时（跨完整性级别、自绘界面、COM 失败）
/// 返回 <c>null</c>，调用方退回窗口矩形。
/// </para>
/// </summary>
internal static class WindowsUiAutomation
{
    /// <summary>UIA_BoundingRectanglePropertyId：元素矩形（VT_ARRAY | VT_R8，4 个 double）。</summary>
    private const int BoundingRectanglePropertyId = 30001;

    /// <summary>UIA_ProcessIdPropertyId：元素所属进程（VT_I4）。</summary>
    private const int ProcessIdPropertyId = 30002;

    /// <summary>VT_I4。</summary>
    private const ushort VariantTypeI4 = 0x0003;

    /// <summary>VT_ARRAY | VT_R8。</summary>
    private const ushort BoundingRectangleVariantType = 0x2005;

    /// <summary>TreeScope_Children：只取直接子元素，配合逐层下降使用。</summary>
    private const int TreeScopeChildren = 2;

    /// <summary>最多下降的层数：再深就只是按钮内部的文本，没有截取意义。</summary>
    private const int MaxDepth = 6;

    /// <summary>单层子元素上限（极端窗口可能有上千个，逐个跨进程读属性会卡住遮罩）。</summary>
    private const int MaxChildrenPerLevel = 200;

    /// <summary>元素链缓存的有效期：这段时间内、光标挪动不超过容差，直接复用上次结果。</summary>
    private const long CacheTtlMilliseconds = 200;

    /// <summary>缓存允许的光标位移容差（像素）。</summary>
    private const int CacheTolerancePixels = 6;

    private static readonly Lazy<IUIAutomation?> Automation = new(Create);

    /// <summary>UIA 的"永远成立"条件，进程内复用一份（<c>CreateTrueCondition</c>）。</summary>
    private static readonly Lazy<IntPtr> TrueCondition = new(CreateTrueCondition);

    /// <summary>
    /// 屏幕某点下的<b>元素链</b>：从最外层子元素到最内层（不含窗口自身，窗口由调用方的
    /// <c>GetRectAt</c> 提供）。拿不到返回 <c>null</c>；没有子元素时返回空链。
    /// <para>
    /// 供遮罩上的「层级依次往上」用：调用方持整条链，滚轮 / Tab 在链上移动，
    /// 不必每换一层就重新查 UIA。
    /// </para>
    /// </summary>
    public static IReadOnlyList<ScreenRect>? GetElementChainAt(int x, int y, IntPtr windowHandle = default)
    {
        // 分析器（CA1416）要求调用点自己有平台守卫：本类虽然只在 Windows 前台探测里用，
        // 但 Kscan.Platform 的程序集目标不是 windows TFM。
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (TryGetCached(windowHandle, x, y, out var cached))
        {
            return cached;
        }

        var chain = ComputeChain(x, y, windowHandle);
        StoreCached(windowHandle, x, y, chain);
        return chain;
    }

    private static IReadOnlyList<ScreenRect>? ComputeChain(int x, int y, IntPtr windowHandle)
    {
        var automation = Automation.Value;
        if (automation is null)
        {
            return null;
        }

        try
        {
            if (windowHandle != IntPtr.Zero && ChainFromWindow(automation, windowHandle, x, y) is { } chain)
            {
                return chain;
            }

            return ChainFromPoint(automation, x, y);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }

    // ---- 结果缓存 ----
    // 遮罩上每 66ms 探一次；鼠标在同一元素里移动时跨进程 UIA 往返完全是浪费。
    // 缓存键 = 窗口句柄 + 光标位置（带容差）+ 时间；命中时零 COM 调用。

    private static readonly object CacheGate = new();
    private static IntPtr _cacheWindow;
    private static int _cacheX;
    private static int _cacheY;
    private static long _cacheAt = -1;
    private static IReadOnlyList<ScreenRect>? _cacheChain;
    private static bool _cacheValid;

    private static bool TryGetCached(IntPtr windowHandle, int x, int y, out IReadOnlyList<ScreenRect>? chain)
    {
        lock (CacheGate)
        {
            if (_cacheValid
                && _cacheWindow == windowHandle
                && Math.Abs(x - _cacheX) <= CacheTolerancePixels
                && Math.Abs(y - _cacheY) <= CacheTolerancePixels
                && Environment.TickCount64 - _cacheAt <= CacheTtlMilliseconds)
            {
                chain = _cacheChain;
                return true;
            }
        }

        chain = null;
        return false;
    }

    private static void StoreCached(IntPtr windowHandle, int x, int y, IReadOnlyList<ScreenRect>? chain)
    {
        lock (CacheGate)
        {
            _cacheWindow = windowHandle;
            _cacheX = x;
            _cacheY = y;
            _cacheAt = Environment.TickCount64;
            _cacheChain = chain;
            _cacheValid = true;
        }
    }

    /// <summary>
    /// 屏幕某点下最内层元素的矩形。
    /// <para>
    /// <paramref name="windowHandle"/> 是光标下的<b>真实窗口</b>句柄（调用方已排除遮罩）。
    /// 从它出发逐层往下钻；句柄不可用时退回 <c>ElementFromPoint</c> —— 那条路上必须过滤本进程元素，
    /// 否则命中的是遮罩自己的控件，"元素框"会正好套在光标图像外面。
    /// </para>
    /// </summary>
    public static ScreenRect? GetElementRectAt(int x, int y, IntPtr windowHandle = default) =>
        GetElementChainAt(x, y, windowHandle) is { Count: > 0 } chain ? chain[^1] : null;


    /// <summary>
    /// 从窗口元素开始逐层下降，收集每一层「包含该点、面积最小的子元素」矩形，形成
    /// <b>从外到内</b>的链（最后一项 = 最内层）。没有更小的子元素时返回空链。
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<ScreenRect>? ChainFromWindow(IUIAutomation automation, IntPtr windowHandle, int x, int y)
    {
        if (automation.ElementFromHandle(windowHandle, out var windowPtr) != 0 || windowPtr == IntPtr.Zero)
        {
            return null;
        }

        IUIAutomationElement current;
        try
        {
            current = (IUIAutomationElement)Marshal.GetObjectForIUnknown(windowPtr);
        }
        finally
        {
            Marshal.Release(windowPtr);
        }

        var chain = new List<ScreenRect>();

        try
        {
            for (var depth = 0; depth < MaxDepth; depth++)
            {
                if (SmallestChildAt(current, x, y) is not { } child)
                {
                    break;
                }

                // 有些控件会报出与父级几乎一样大的矩形（透明包裹层），不必单独占一层
                if (chain.Count == 0 || !NearlySame(chain[^1], child.Rect))
                {
                    chain.Add(child.Rect);
                }

                Marshal.ReleaseComObject(current);
                current = child.Element;
            }

            return chain;
        }
        finally
        {
            Marshal.ReleaseComObject(current);
        }
    }

    /// <summary>两个矩形是否几乎一样（各边差 ≤ 3px）。</summary>
    private static bool NearlySame(ScreenRect a, ScreenRect b) =>
        Math.Abs(a.X - b.X) <= 3
        && Math.Abs(a.Y - b.Y) <= 3
        && Math.Abs(a.Width - b.Width) <= 3
        && Math.Abs(a.Height - b.Height) <= 3;

    /// <summary>
    /// 在 <paramref name="parent"/> 的直接子元素里找包含该点、面积最小的那个。
    /// 返回的元素由调用方负责 <see cref="Marshal.ReleaseComObject(object)"/>。
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static (ScreenRect Rect, IUIAutomationElement Element)? SmallestChildAt(
        IUIAutomationElement parent, int x, int y)
    {
        var condition = TrueCondition.Value;
        if (condition == IntPtr.Zero)
        {
            return null;
        }

        if (parent.FindAll(TreeScopeChildren, condition, out var arrayPtr) != 0 || arrayPtr == IntPtr.Zero)
        {
            return null;
        }

        IUIAutomationElementArray array;
        try
        {
            array = (IUIAutomationElementArray)Marshal.GetObjectForIUnknown(arrayPtr);
        }
        finally
        {
            Marshal.Release(arrayPtr);
        }

        try
        {
            if (array.GetLength(out var length) != 0 || length <= 0)
            {
                return null;
            }

            var count = Math.Min(length, MaxChildrenPerLevel);
            ScreenRect bestRect = default;
            long bestArea = long.MaxValue;
            IUIAutomationElement? best = null;

            for (var index = 0; index < count; index++)
            {
                if (array.GetElement(index, out var childPtr) != 0 || childPtr == IntPtr.Zero)
                {
                    continue;
                }

                IUIAutomationElement? child = null;
                try
                {
                    child = (IUIAutomationElement)Marshal.GetObjectForIUnknown(childPtr);
                    Marshal.Release(childPtr);

                    if (ReadElementRect(child) is not { } rect || !rect.Contains(x, y))
                    {
                        continue;
                    }

                    var area = (long)rect.Width * rect.Height;

                    // 面积相同时让后出现的赢（UIA 兄弟顺序里后面的通常叠在上面），与 PickSmallestContaining 一致
                    if (area > bestArea)
                    {
                        continue;
                    }

                    // 更合适的候选：只保留它一个 RCW，上一个立刻释放。
                    // 这样收尾不必再 GetElement(最好下标) 取一次 —— 每次探测少一轮跨进程调用。
                    if (best is not null)
                    {
                        Marshal.ReleaseComObject(best);
                    }

                    best = child;
                    child = null;
                    bestArea = area;
                    bestRect = rect;
                }
                finally
                {
                    if (child is not null)
                    {
                        Marshal.ReleaseComObject(child);
                    }
                }
            }

            return best is null ? null : (bestRect, best);
        }
        finally
        {
            Marshal.ReleaseComObject(array);
        }
    }

    /// <summary>
    /// 从候选矩形里挑"包含该点、面积最小"的那个，返回下标；没有候选返回 -1。
    /// 面积相同时取<b>靠后</b>的候选（UIA 的兄弟顺序里后面的通常叠在上面）。
    /// </summary>
    internal static int PickSmallestContaining(IReadOnlyList<ScreenRect> rects, int x, int y)
    {
        var best = -1;
        var bestArea = long.MaxValue;

        for (var index = 0; index < rects.Count; index++)
        {
            var rect = rects[index];
            if (!rect.Contains(x, y))
            {
                continue;
            }

            var area = (long)rect.Width * rect.Height;
            if (area > bestArea)
            {
                continue;
            }

            bestArea = area;
            best = index;
        }

        return best;
    }

    /// <summary>
    /// 退回 <c>ElementFromPoint</c>：只在拿不到窗口句柄时用，只能给出单元素链。
    /// <b>必须排除本进程</b>——否则命中的是遮罩自己的控件，"元素框"会正好套在光标图像外面。
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<ScreenRect>? ChainFromPoint(IUIAutomation automation, int x, int y)
    {
        var hr = automation.ElementFromPoint(new NativePoint { X = x, Y = y }, out var pointer);
        if (hr != 0 || pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var element = (IUIAutomationElement)Marshal.GetObjectForIUnknown(pointer);
            try
            {
                if (IsOwnProcess(element) || ReadElementRect(element) is not { } rect)
                {
                    return null;
                }

                return [rect];
            }
            finally
            {
                Marshal.ReleaseComObject(element);
            }
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    /// <summary>元素的屏幕矩形（物理像素）；读不到返回 <c>null</c>。</summary>
    private static ScreenRect? ReadElementRect(IUIAutomationElement element)
    {
        if (element.GetCurrentPropertyValue(BoundingRectanglePropertyId, out var value) != 0)
        {
            return null;
        }

        try
        {
            return value.Type == BoundingRectangleVariantType ? ReadRect(value.Data) : null;
        }
        finally
        {
            VariantClear(ref value);
        }
    }

    /// <summary>该 UIA 元素是不是本进程（= 框选遮罩）的控件。</summary>
    private static bool IsOwnProcess(IUIAutomationElement element)
    {
        if (element.GetCurrentPropertyValue(ProcessIdPropertyId, out var value) != 0)
        {
            return false;
        }

        try
        {
            return value.Type == VariantTypeI4 && (int)value.Data == Environment.ProcessId;
        }
        finally
        {
            VariantClear(ref value);
        }
    }

    /// <summary>SAFEARRAY(VT_R8) 里依次是 left / top / width / height。</summary>
    private static ScreenRect? ReadRect(IntPtr array)
    {
        if (array == IntPtr.Zero)
        {
            return null;
        }

        if (SafeArrayGetElement(array, [0], out var left) != 0
            || SafeArrayGetElement(array, [1], out var top) != 0
            || SafeArrayGetElement(array, [2], out var width) != 0
            || SafeArrayGetElement(array, [3], out var height) != 0)
        {
            return null;
        }

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        return new ScreenRect(
            (int)Math.Round(left),
            (int)Math.Round(top),
            (int)Math.Round(width),
            (int)Math.Round(height));
    }

    private static IUIAutomation? Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return (IUIAutomation)new CUIAutomation();
        }
        catch (COMException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// VARIANT 的最小可用形态：只需要读 vt 与 union 里的 SAFEARRAY*。
    /// x64 的 VARIANT 是 24 字节（union 占 16 字节），声明成 24 在 x86 下也够用。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Type;

        [FieldOffset(8)] public IntPtr Data;
    }

    [ComImport]
    [Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        [PreserveSig] int CompareElements(IntPtr element1, IntPtr element2, out int areSame);

        [PreserveSig] int CompareRuntimeIds(IntPtr runtimeId1, IntPtr runtimeId2, out int areSame);

        [PreserveSig] int GetRootElement(out IntPtr root);

        [PreserveSig] int ElementFromHandle(IntPtr hwnd, out IntPtr element);

        [PreserveSig] int ElementFromPoint(NativePoint point, out IntPtr element);

        [PreserveSig] int GetFocusedElement(out IntPtr element);

        [PreserveSig] int GetRootElementBuildCache(IntPtr cacheRequest, out IntPtr root);

        [PreserveSig] int ElementFromHandleBuildCache(IntPtr hwnd, IntPtr cacheRequest, out IntPtr element);

        [PreserveSig] int ElementFromPointBuildCache(NativePoint point, IntPtr cacheRequest, out IntPtr element);

        [PreserveSig] int GetFocusedElementBuildCache(IntPtr cacheRequest, out IntPtr element);

        // 下面 7 个（TreeWalker 与三种 View 的 walker / condition）本项目用不到，
        // 但必须留作占位：vtable 顺序由 IDL 决定，少一个就会把后面的方法整体调错。
        // 「元素链恒为空」的根因就是这里漏了它们 —— CreateTrueCondition 落到了
        // get_ControlViewWalker 的槽位上，拿到的是 TreeWalker，FindAll 拿它当条件 → E_FAIL。
        [PreserveSig] int CreateTreeWalker(IntPtr condition, out IntPtr walker);

        [PreserveSig] int GetControlViewWalker(out IntPtr walker);

        [PreserveSig] int GetContentViewWalker(out IntPtr walker);

        [PreserveSig] int GetRawViewWalker(out IntPtr walker);

        [PreserveSig] int GetRawViewCondition(out IntPtr condition);

        [PreserveSig] int GetControlViewCondition(out IntPtr condition);

        [PreserveSig] int GetContentViewCondition(out IntPtr condition);

        [PreserveSig] int CreateCacheRequest(out IntPtr cacheRequest);

        [PreserveSig] int CreateTrueCondition(out IntPtr condition);
    }

    /// <summary><c>IUIAutomationElementArray</c>：只有长度与按序号取元素两个方法。</summary>
    [ComImport]
    [Guid("14314595-b4bc-4055-95f2-58f2e42c9855")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        [PreserveSig] int GetLength(out int length);

        [PreserveSig] int GetElement(int index, out IntPtr element);
    }

    /// <summary>创建（一次后复用）"永远成立"的 UIA 条件，供 <c>FindAll</c> 使用。</summary>
    private static IntPtr CreateTrueCondition()
    {
        if (!OperatingSystem.IsWindows())
        {
            return IntPtr.Zero;
        }

        var automation = Automation.Value;
        return automation is not null && automation.CreateTrueCondition(out var condition) == 0
            ? condition
            : IntPtr.Zero;
    }

    [ComImport]
    [Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        [PreserveSig] int SetFocus();

        [PreserveSig] int GetRuntimeId(out IntPtr runtimeId);

        [PreserveSig] int FindFirst(int scope, IntPtr condition, out IntPtr found);

        [PreserveSig] int FindAll(int scope, IntPtr condition, out IntPtr found);

        [PreserveSig] int FindFirstBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IntPtr found);

        [PreserveSig] int FindAllBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IntPtr found);

        [PreserveSig] int BuildUpdatedCache(IntPtr cacheRequest, out IntPtr updatedElement);

        [PreserveSig] int GetCurrentPropertyValue(int propertyId, out Variant value);
    }

    [ComImport]
    [Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation
    {
    }

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetElement(IntPtr array, int[] indices, out double value);

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(ref Variant value);
}
