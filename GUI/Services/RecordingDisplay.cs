using System.Runtime.InteropServices;

namespace Kscan.App.Services;

internal static class RecordingDisplay
{
    public static int RefreshRate(string? display)
    {
        var mode = new byte[220];
        BitConverter.GetBytes((short)220).CopyTo(mode, 68);
        if (!EnumDisplaySettings(display, -1, mode)) return 60;
        var rate = BitConverter.ToInt32(mode, 184);
        return rate is >= 10 and <= 360 ? rate : 60;
    }
    public static void Exclude(Avalonia.Controls.Window window)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero || !SetWindowDisplayAffinity(handle, 0x11))
            throw new InvalidOperationException("无法将录制控件排除出画面，请使用 Windows 10 2004 或更新版本。");
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? device, int mode, byte[] settings);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
}
