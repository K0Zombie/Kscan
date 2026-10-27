using System.ComponentModel;
using System.Runtime.InteropServices;
using Kscan.Platform.Abstractions;

namespace Kscan.App.Services;

/// <summary>Short-lived selection input, without activating a fullscreen window.</summary>
internal sealed class RecordingSelectionInput : IDisposable
{
    internal const uint LeftDown = 0x0201, LeftUp = 0x0202, RightDown = 0x0204, RightUp = 0x0205;
    private readonly HookProc _callback;
    private readonly Queue<(uint Message, ScreenPoint Point)> _buttons = new();
    private IntPtr _hook;
    internal Exception? Error { get; private set; }

    internal RecordingSelectionInput()
    {
        _callback = Hook;
        _hook = SetWindowsHookEx(14, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启用录制框选输入。");
    }

    private IntPtr Hook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (uint)message.ToInt64() is LeftDown or LeftUp or RightDown or RightUp)
        {
            try
            {
                var point = Marshal.PtrToStructure<MouseData>(data);
                if (_buttons.Count < 64) _buttons.Enqueue(((uint)message.ToInt64(), new(point.X, point.Y)));
                else Error = new InvalidOperationException("录制框选输入积压，请重试。");
                return new IntPtr(1); // Suppress this explicit selection click, not keyboard input.
            }
            catch (Exception ex) { Error = ex; }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    internal (uint Message, ScreenPoint Point)[] Drain()
    {
        if (_buttons.Count == 0) return [];
        var events = _buttons.ToArray(); _buttons.Clear(); return events;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        _buttons.Clear(); GC.KeepAlive(_callback);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData { public int X, Y; public uint Mouse, Flags, Time; public UIntPtr Extra; }
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
}
