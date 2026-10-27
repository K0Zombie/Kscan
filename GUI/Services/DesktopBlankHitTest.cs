using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;

namespace Kscan.App.Services;

internal static class DesktopBlankHitTest
{
    // MSAA 区分桌面列表背景和图标，失败时保守地拒绝本地贴出。
    public static bool IsBlank(IntPtr list, PixelPoint point)
    {
        object? accessible = null;
        try
        {
            var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
            if (AccessibleObjectFromWindow(list, 0xFFFFFFFC, ref iid, out accessible) < 0 || accessible is null)
                return false;
            var hit = accessible.GetType().InvokeMember("accHitTest", BindingFlags.InvokeMethod, null, accessible, [point.X, point.Y]);
            try { return hit is int child && child == 0; } // CHILDID_SELF = 列表背景，图标是子项
            finally { if (hit is not null && Marshal.IsComObject(hit)) Marshal.ReleaseComObject(hit); }
        }
        catch { return false; }
        finally
        {
            if (accessible is not null && Marshal.IsComObject(accessible)) Marshal.ReleaseComObject(accessible);
        }
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid iid,
        [MarshalAs(UnmanagedType.IDispatch)] out object? accessible);
}
