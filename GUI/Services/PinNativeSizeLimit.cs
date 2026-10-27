using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Kscan.App.Services;

/// <summary>Keep Win32's maximum tracking size and Avalonia's cached layout hint in sync.</summary>
internal static class PinNativeSizeLimit
{
    internal const uint GetMinMaxInfo = 0x0024;
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Limits
    {
        public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    internal static void Apply(uint message, IntPtr pointer, double maximumDip, double scaling)
    {
        if (message != GetMinMaxInfo || pointer == IntPtr.Zero) return;
        if (!double.IsFinite(scaling) || scaling <= 0) scaling = 1;
        var pixels = (int)Math.Clamp(Math.Ceiling(maximumDip * scaling), 1, 32760);
        var limits = Marshal.PtrToStructure<Limits>(pointer);
        limits.MaxTrackSize = new Point { X = pixels, Y = pixels };
        Marshal.StructureToPtr(limits, pointer, false);
        // Do not mark this message handled: Avalonia must see the updated values
        // to refresh MaxAutoSizeHint, otherwise its layout still clips large pins.
    }

    internal static void Refresh(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.HandleDescriptor != "HWND" || handle.Handle == IntPtr.Zero) return;
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Limits>());
        try
        {
            Marshal.StructureToPtr(new Limits(), pointer, false);
            SendMessage(handle.Handle, GetMinMaxInfo, IntPtr.Zero, pointer);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
