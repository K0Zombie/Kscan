using System.Runtime.InteropServices;

namespace Kscan.Platform.Windows;

internal static class WindowsTraySupport
{
    public static bool IsAvailable => FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);
}
