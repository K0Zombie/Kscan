using System.Runtime.InteropServices;
using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Windows;

/// <summary>Windows 的鼠标位置（物理像素，虚拟桌面坐标）。</summary>
internal static class WindowsCursor
{
    public static ScreenPoint? GetPosition() =>
        GetCursorPos(out var point) ? new ScreenPoint(point.X, point.Y) : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
}
