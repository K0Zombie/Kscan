namespace Kscan.Platform.Abstractions;

/// <summary>前台窗口（或鼠标下窗口）所属的进程。</summary>
/// <param name="ProcessName">进程文件名，带扩展名，例如 <c>taskmgr.exe</c>。</param>
/// <param name="ExecutablePath">完整 exe 路径；拿不到时为 <c>null</c>。</param>
public sealed record ForegroundAppInfo(string ProcessName, string? ExecutablePath);

/// <summary>屏幕上的一个物理像素矩形（左上角 + 宽高）。</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x < X + Width && y < Y + Height;

    /// <summary>两个矩形是否有重叠区域（只挨着边不算）。</summary>
    public bool IntersectsWith(ScreenRect other) =>
        X < other.X + other.Width
        && other.X < X + Width
        && Y < other.Y + other.Height
        && other.Y < Y + Height;

    /// <summary>
    /// 四边各外扩 <paramref name="padding"/> 像素（负数 = 向内缩）。
    /// 缩到没有面积时返回 <c>null</c>，调用方应保留原矩形。
    /// </summary>
    public ScreenRect? Inflate(int padding)
    {
        if (padding == 0)
        {
            return this;
        }

        var width = Width + (padding * 2);
        var height = Height + (padding * 2);

        return width > 0 && height > 0
            ? new ScreenRect(X - padding, Y - padding, width, height)
            : null;
    }
}

/// <summary>屏幕上的一个物理像素坐标点（虚拟桌面坐标）。</summary>
public readonly record struct ScreenPoint(int X, int Y);

/// <summary>
/// 前台应用探测：全局热键的"排除应用"要用它判断焦点在谁那里；
/// "框选排除应用"热键要用它找出鼠标下的窗口属于哪个程序。
/// <para>
/// Windows 用 Win32；其它平台没有实现（Wayland 下应用连"全局输入"都拿不到）。
/// </para>
/// </summary>
public interface IForegroundApp
{
    bool IsSupported { get; }

    /// <summary>当前前台窗口的进程；拿不到返回 <c>null</c>。</summary>
    ForegroundAppInfo? GetForeground();

    /// <summary>
    /// 屏幕坐标（物理像素）下最上层窗口的进程。用于"点选窗口加入排除列表"。
    /// <paramref name="ignoreHandle"/> 传自己的全屏遮罩句柄，否则永远命中遮罩本身。
    /// </summary>
    ForegroundAppInfo? GetAt(int screenX, int screenY, IntPtr ignoreHandle = default);

    /// <summary>屏幕坐标下最上层窗口的矩形（物理像素）；拿不到返回 <c>null</c>。</summary>
    ScreenRect? GetRectAt(int screenX, int screenY, IntPtr ignoreHandle = default);

    /// <summary>
    /// 已知句柄的可见矩形；拿不到返回 <c>null</c>。平台不单独实现时返回 <c>null</c>。
    /// <para>
    /// 和 <see cref="GetRectAt"/> 只差<b>不再做命中测试</b>：调用方先拿到
    /// <see cref="GetWindowHandleAt"/> 的句柄，再用它取同一个窗口的矩形，
    /// 就不必在同一个点命中两次 —— 也不会出现"句柄是这一次的、矩形是上一次的"这种错配。
    /// </para>
    /// </summary>
    ScreenRect? GetRectOf(IntPtr handle) => null;

    /// <summary>
    /// 屏幕坐标下最上层窗口的句柄；拿不到返回 <see cref="IntPtr.Zero"/>。
    /// 供"整窗离屏渲染"（<see cref="IWindowCapture"/>）用 —— 比矩形多一个句柄。
    /// </summary>
    IntPtr GetWindowHandleAt(int screenX, int screenY, IntPtr ignoreHandle = default);

    /// <summary>
    /// 屏幕坐标下最内层 UI 元素（子控件）的矩形；拿不到返回 <c>null</c>。
    /// 调用方应与 <see cref="GetRectAt"/> 的窗口矩形比较 —— 一样大说明没有更细的元素。
    /// </summary>
    ScreenRect? GetElementRectAt(int screenX, int screenY, IntPtr ignoreHandle = default);

    /// <summary>
    /// 屏幕坐标下的<b>元素链</b>：从最外层子元素到最内层（<b>不含窗口自身</b>），供
    /// 「子元素层级依次往上」用。拿不到返回 <c>null</c>；没有子元素返回空链。
    /// 平台不单独实现时退回「只含最内层的单元素链」。
    /// </summary>
    IReadOnlyList<ScreenRect>? GetElementChainAt(int screenX, int screenY, IntPtr ignoreHandle = default) =>
        GetElementRectAt(screenX, screenY, ignoreHandle) is { } rect ? [rect] : null;

    /// <summary>当前鼠标位置（物理像素）；拿不到返回 <c>null</c>。</summary>
    ScreenPoint? GetCursorPosition();

    /// <summary>
    /// 目标窗口是否被其它窗口挡住（Z 序排在它上面的可见窗口里，有矩形与它重叠的就算）。
    /// 用于「截窗口时突破遮挡」：判断要不要走 <see cref="IWindowCapture"/> 整窗渲染。
    /// <paramref name="ignoreHandle"/> 传自己的全屏遮罩句柄，否则遮罩自己就把一切都算成"被挡住"。
    /// </summary>
    bool IsWindowCovered(IntPtr handle, IntPtr ignoreHandle = default);
    /// <summary>这个窗口是不是"桌面本体"（Progman / WorkerW / 无标题桌面壳）。默认 false。</summary>
    bool IsDesktopSurface(IntPtr handle) => false;

    /// <summary>
    /// 这个窗口是不是任务栏（主 / 副屏）。默认 false。
    /// <para>
    /// 存在的理由：任务栏的<b>高亮矩形</b>被特意设成整块屏幕（<c>WindowsWindowGeometry.VisibleRect</c>），
    /// 谁拿矩形去判断"这是不是铺满整屏的全屏窗口"都会把它误判成独占全屏程序。
    /// </para>
    /// </summary>
    bool IsTaskbar(IntPtr handle) => false;
}

/// <summary>
/// 元素链的层级换算（「依次往上」的纯逻辑，方便单测）。
/// <para>
/// 约定：<paramref name="offset"/> = <c>0</c> 表示<b>最内层</b>，每 +1 往外一层；
/// 等于链长时表示<b>窗口层</b>（不算元素，单击就是截整个窗口）。
/// </para>
/// </summary>
public static class ElementChainLevels
{
    /// <summary>把偏移夹在 [0, 链长] 之间（链长 = 窗口层）。</summary>
    public static int Clamp(IReadOnlyList<ScreenRect> chain, int offset) =>
        Math.Clamp(offset, 0, chain.Count);

    /// <summary>当前偏移对应的元素矩形；窗口层（或空链）返回 <c>null</c>。</summary>
    public static ScreenRect? At(IReadOnlyList<ScreenRect> chain, int offset)
    {
        var index = chain.Count - 1 - Clamp(chain, offset);
        return index >= 0 ? chain[index] : null;
    }

    /// <summary>往外一层（父级）；到窗口层后不再变。</summary>
    public static int Parent(IReadOnlyList<ScreenRect> chain, int offset) =>
        Clamp(chain, offset + 1);

    /// <summary>往里一层（子级）；到最内层后不再变。</summary>
    public static int Child(IReadOnlyList<ScreenRect> chain, int offset) =>
        Clamp(chain, offset - 1);

    /// <summary>当前偏移是不是窗口层（不画元素框，单击截整个窗口）。</summary>
    public static bool IsWindowLevel(IReadOnlyList<ScreenRect> chain, int offset) =>
        Clamp(chain, offset) >= chain.Count;
}

/// <summary>不支持前台探测的平台（Linux 等）：一律返回 <c>null</c>。</summary>
internal sealed class UnsupportedForegroundApp : IForegroundApp
{
    public bool IsSupported => false;

    public ForegroundAppInfo? GetForeground() => null;

    public ForegroundAppInfo? GetAt(int screenX, int screenY, IntPtr ignoreHandle = default) => null;

    public ScreenRect? GetRectAt(int screenX, int screenY, IntPtr ignoreHandle = default) => null;

    public ScreenRect? GetRectOf(IntPtr handle) => null;

    public IntPtr GetWindowHandleAt(int screenX, int screenY, IntPtr ignoreHandle = default) => IntPtr.Zero;

    public ScreenRect? GetElementRectAt(int screenX, int screenY, IntPtr ignoreHandle = default) => null;

    public ScreenPoint? GetCursorPosition() => null;

    public bool IsWindowCovered(IntPtr handle, IntPtr ignoreHandle = default) => false;
}
