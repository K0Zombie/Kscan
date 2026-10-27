using System.Runtime.InteropServices;

namespace Kscan.Platform.Windows;

/// <summary>
/// 光标相关的注册表 / 系统度量读取。
/// <para>
/// 和 <see cref="WindowsAutoStart"/> 一样用 advapi32 的 P/Invoke，不引
/// <c>Microsoft.Win32.Registry</c> 包（平台层不加依赖，Linux 上也能编译同一份程序集）。
/// </para>
/// </summary>
internal static class WindowsCursorMetrics
{
    private const string CursorsKey = @"Control Panel\Cursors";
    private const string BaseSizeValue = "CursorBaseSize";

    private const uint KeyQueryValue = 0x0001;
    private const int ErrorSuccess = 0;
    private const uint RegDword = 4;

    /// <summary>HKEY_CURRENT_USER 的预定义句柄值。</summary>
    private static readonly IntPtr HkeyCurrentUser = new(-2147483647);

    /// <summary>
    /// Windows「辅助功能 → 鼠标指针和触控 → 大小」对应的**光标基准尺寸**（100% 缩放下的像素，默认 32）。
    /// 用户把指针调大后这里是 48 / 64 / 80…，而 <c>SM_CXCURSOR</c> 仍然是 32 ——
    /// 只看系统度量会把光标画得只有真实光标的 40% 大。
    /// 读不到（老系统 / 非 Windows）返回 <c>0</c>。
    /// </summary>
    public static int CursorBaseSize()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        try
        {
            if (RegOpenKeyExW(HkeyCurrentUser, CursorsKey, 0, KeyQueryValue, out var key) != ErrorSuccess)
            {
                return 0;
            }

            try
            {
                var buffer = new byte[4];
                uint size = 4;

                if (RegQueryValueExW(key, BaseSizeValue, IntPtr.Zero, out var type, buffer, ref size) != ErrorSuccess ||
                    type != RegDword ||
                    size < 4)
                {
                    return 0;
                }

                var value = BitConverter.ToInt32(buffer, 0);
                return value > 0 ? value : 0;
            }
            finally
            {
                RegCloseKey(key);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0;
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyExW(IntPtr hKey, string subKey, uint options, uint desiredAccess, out IntPtr result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueExW(
        IntPtr hKey, string valueName, IntPtr reserved, out uint type, byte[]? data, ref uint dataSize);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);
}

/// <summary>
/// 光标在屏幕上画出来有多大（物理像素）的判定规则。纯函数，便于单测。
/// <para>
/// 三个来源互相取大 —— 每一个都可能缺，也可能被用户改过：
/// </para>
/// <list type="number">
/// <item>系统声明的大小 <c>GetSystemMetricsForDpi(SM_CXCURSOR)</c>（已按该屏 DPI 缩放）；</item>
/// <item>辅助功能里的光标基准尺寸 <c>CursorBaseSize</c> × 该屏 DPI / 96（调大指针后 <c>SM_CXCURSOR</c> 不变，只能靠它）；</item>
/// <item>光标位图自身尺寸 × DPI / 96（**只放大不缩小**：位图就是一个下限）。</item>
/// </list>
/// </summary>
internal static class CursorSizePolicy
{
    /// <summary>比这还小的尺寸不可能是光标（系统度量拿不到时返回 0）。</summary>
    public const int MinSize = 8;

    /// <summary>上限：再大就不是光标了，防止异常的注册表值把 DIB 撑爆。</summary>
    public const int MaxSize = 512;

    public static (int Width, int Height) Resolve(
        int dpi,
        int iconWidth,
        int iconHeight,
        int systemWidth,
        int systemHeight,
        int cursorBaseSize)
    {
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;

        var width = Math.Max(
            Math.Max(systemWidth, Scale(cursorBaseSize, scale)),
            Math.Max(iconWidth, Scale(iconWidth, scale)));

        var height = Math.Max(
            Math.Max(systemHeight, Scale(cursorBaseSize, scale)),
            Math.Max(iconHeight, Scale(iconHeight, scale)));

        return (Clamp(width), Clamp(height));
    }

    private static int Scale(int value, double scale) =>
        value <= 0 ? 0 : (int)Math.Round(value * scale);

    private static int Clamp(int value) => Math.Clamp(value, MinSize, MaxSize);
}
