using System.Runtime.InteropServices;
using Avalonia.Threading;
using Kscan.Platform;

namespace Kscan.App.Services;

/// <summary>A shared, capture-visible layered cursor using the screenshot cursor size policy.
/// Never changes the system cursor. Reference counted so concurrent recordings draw only one cursor.</summary>
internal sealed class RecordingCursor : IDisposable
{
    private static RecordingCursor? _shared;
    private int _users;
    private readonly IntPtr _window;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private IntPtr _dc, _bitmap, _bits, _old;
    private int _width, _height;
    private Exception? _error;

    internal static Lease Acquire()
    {
        Dispatcher.UIThread.VerifyAccess();
        var cursor = _shared ??= new RecordingCursor();
        cursor._users++;
        return new Lease(cursor);
    }
    internal sealed class Lease(RecordingCursor owner) : IDisposable
    {
        private RecordingCursor? _owner = owner;
        internal Exception? Error => _owner?._error;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            void Release() { if (--current._users == 0) { current.Dispose(); _shared = null; } }
            if (Dispatcher.UIThread.CheckAccess()) Release(); else Dispatcher.UIThread.Post(Release);
        }
    }
    private RecordingCursor()
    {
        // WS_EX_LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE | TOPMOST.
        _window = CreateWindowEx(0x080800A8, "STATIC", "Kscan recording cursor", 0x80000000,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_window == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            Update();
            _timer.Tick += (_, _) =>
            {
                try { Update(); }
                catch (Exception ex) { _error = ex; _timer.Stop(); ShowWindow(_window, 0); GuiLog.Write($"recording: cursor update failed {ex}"); }
            };
            _timer.Start();
        }
        catch { Dispose(); throw; }
    }
    private void Update()
    {
        var cursor = PlatformServices.CursorCapture.Capture();
        if (cursor is null) { ShowWindow(_window, 0); return; }
        var image = cursor.Image;
        if (_width != image.Width || _height != image.Height)
        {
            FreeBitmap();
            _width = image.Width; _height = image.Height;
            _dc = CreateCompatibleDC(IntPtr.Zero);
            var info = new BitmapInfo { Size = 40, Width = _width, Height = -_height, Planes = 1, Bits = 32 };
            _bitmap = CreateDIBSection(_dc, ref info, 0, out _bits, IntPtr.Zero, 0);
            if (_dc == IntPtr.Zero || _bitmap == IntPtr.Zero) throw new InvalidOperationException("无法绘制录制光标。");
            _old = SelectObject(_dc, _bitmap);
        }
        // UpdateLayeredWindow requires premultiplied BGRA; screenshot capture supplies straight alpha.
        var pixels = Premultiply(image.Pixels);
        Marshal.Copy(pixels, 0, _bits, pixels.Length);
        var destination = new NativePoint(cursor.Left, cursor.Top);
        var size = new NativePoint(_width, _height);
        var source = new NativePoint(0, 0);
        var blend = new Blend { Alpha = 255, Format = 1 };
        if (!UpdateLayeredWindow(_window, IntPtr.Zero, ref destination, ref size, _dc, ref source, 0, ref blend, 2))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        ShowWindow(_window, 4); // SW_SHOWNOACTIVATE; no capture exclusion, gdigrab captures these pixels.
    }
    internal static byte[] Premultiply(byte[] source)
    {
        var result = new byte[source.Length];
        for (var i = 0; i < source.Length; i += 4)
        {
            var alpha = source[i + 3];
            result[i] = (byte)((source[i] * alpha + 127) / 255);
            result[i + 1] = (byte)((source[i + 1] * alpha + 127) / 255);
            result[i + 2] = (byte)((source[i + 2] * alpha + 127) / 255);
            result[i + 3] = alpha;
        }
        return result;
    }
    private void FreeBitmap()
    {
        if (_old != IntPtr.Zero) SelectObject(_dc, _old);
        if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
        if (_dc != IntPtr.Zero) DeleteDC(_dc);
        _old = _bitmap = _dc = _bits = IntPtr.Zero;
    }
    public void Dispose() { _timer.Stop(); FreeBitmap(); DestroyWindow(_window); }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { public byte Operation, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits;
        public uint Compression, ImageSize; public int XPels, YPels; public uint Used, Important, Color;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint ex, string name, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc, ref NativePoint position, ref NativePoint size, IntPtr sourceDc, ref NativePoint source, uint color, ref Blend blend, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
