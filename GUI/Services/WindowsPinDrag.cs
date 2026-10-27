using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Avalonia;

namespace Kscan.App.Services;

internal enum PinDragTarget { External, Manager, Desktop, Blocked }
internal readonly record struct PinDragResult(PinDragTarget? Target, PixelPoint Position, bool Accepted)
{
    // Target 只有正常松开左键才设置；Esc/右键/关窗取消不能触发贴图。
    public bool ShouldPin => Target is PinDragTarget.Desktop or PinDragTarget.Blocked || Target == PinDragTarget.External && !Accepted;
}

[ComVisible(true), Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPinDropSource
{
    [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escape, uint keys);
    [PreserveSig] int GiveFeedback(uint effect);
}

/// <summary>Shell 文件拖拽。桌面和管理器落点在 OLE Drop 前截获，不向桌面复制文件。</summary>
internal static class WindowsPinDrag
{
    internal const int Drop = 0x40100, Cancel = 0x40101, DefaultCursors = 0x40102;

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class Source(Func<PixelPoint> cursor, Func<PixelPoint, PinDragTarget> classify,
        Action<PixelPoint> update, Func<bool> isActive, Action<bool>? localCursor = null) : IPinDropSource
    {
        public PinDragTarget? Target { get; private set; }
        public PixelPoint Position { get; private set; }

        public int QueryContinueDrag(bool escape, uint keys)
        {
            try
            {
                if (escape || !isActive() || (keys & 2) != 0) { Target = null; GuiLog.Write("pin-drag: cancelled by source"); return Cancel; }
                Position = cursor();
                update(Position);
                if ((keys & 1) != 0) return 0;
                Target = classify(Position);
                return Target == PinDragTarget.External ? Drop : Cancel;
            }
            catch (Exception ex) { Target = null; GuiLog.Write($"pin-drag: source failed {ex}"); return Cancel; }
        }

        public int GiveFeedback(uint effect)
        {
            try
            {
                var point = cursor();
                update(point);
                var target = classify(point);
                if ((target != PinDragTarget.External || (effect & 1) == 0) && localCursor is not null)
                {
                    localCursor(true); // 外部不接收时也可以松手贴图
                    return 0;
                }
            }
            catch { return DefaultCursors; }
            return DefaultCursors;
        }
    }

    public static PinDragResult Run(string path, IntPtr owner, Func<PixelPoint, PinDragTarget> classify,
        Action<PixelPoint> update, Func<bool> isActive)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!isActive() || (GetAsyncKeyState(1) & 0x8000) == 0) return new(null, default, false);
        if (!File.Exists(path)) throw new FileNotFoundException("拖出的图片已不存在", path);
        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
        IDataObject? data = null;
        try
        {
            data = CreateFileData(path);
            var source = new Source(CursorPosition, classify, update, isActive,
                allowed => SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(allowed ? 32649 : 32648))));
            GuiLog.Write($"pin-drag: native begin apartment={Thread.CurrentThread.GetApartmentState()}");
            var hr = SHDoDragDrop(owner, data, source, 1, out var effect); // 仅复制，绝不允许移走图库文件
            GuiLog.Write($"pin-drag: native end hr={hr:X} effect={effect}");
            // 目标的 Drop 返回失败也属于拒收；正常松手后可退回贴图。
            if (hr < 0 && source.Target is null) Marshal.ThrowExceptionForHR(hr);
            return new(source.Target, source.Position, hr == Drop && (effect & 1) != 0);
        }
        finally
        {
            if (data is not null && Marshal.IsComObject(data)) Marshal.ReleaseComObject(data);
            OleUninitialize();
        }
    }

    internal static IDataObject CreateFileData(string path)
    {
        IntPtr folder = IntPtr.Zero, file = IntPtr.Zero;
        try
        {
            path = Path.GetFullPath(path);
            Marshal.ThrowExceptionForHR(SHParseDisplayName(Path.GetDirectoryName(path)!, IntPtr.Zero, out folder, 0, out _));
            Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out file, 0, out _));
            var iid = typeof(IDataObject).GUID;
            Marshal.ThrowExceptionForHR(SHCreateDataObject(folder, 1, [ILFindLastID(file)], IntPtr.Zero, ref iid, out var data));
            return data;
        }
        finally { Marshal.FreeCoTaskMem(file); Marshal.FreeCoTaskMem(folder); }
    }

    internal static PixelPoint CursorPosition() => GetCursorPos(out var p) ? new(p.X, p.Y) : throw new InvalidOperationException("无法读取鼠标位置");

    /// <summary>只识别 Shell 的桌面层级；资源管理器窗口里的空白不算桌面。</summary>
    internal static PinDragTarget DesktopTarget(PixelPoint point)
    {
        var window = WindowFromPoint(new NativePoint { X = point.X, Y = point.Y });
        var name = ClassName(window);
        if (name == "SysListView32")
        {
            var view = GetParent(window);
            if (ClassName(view) != "SHELLDLL_DefView" || ClassName(GetParent(view)) is not ("Progman" or "WorkerW")) return PinDragTarget.External;
            return DesktopBlankHitTest.IsBlank(window, point) ? PinDragTarget.Desktop : PinDragTarget.Blocked;
        }
        return name is "Progman" or "WorkerW" ? PinDragTarget.Desktop : PinDragTarget.External;
    }

    internal static bool IsOverWindow(PixelPoint point, IntPtr owner) => owner != IntPtr.Zero &&
        GetAncestor(WindowFromPoint(new NativePoint { X = point.X, Y = point.Y }), 2) == owner;

    private static string ClassName(IntPtr window)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr bind, out IntPtr pidl, uint attributes, out uint result);
    [DllImport("shell32.dll")] private static extern IntPtr ILFindLastID(IntPtr pidl);
    [DllImport("shell32.dll")] private static extern int SHCreateDataObject(IntPtr folder, uint count, IntPtr[] children, IntPtr inner, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IDataObject data);
    [DllImport("shell32.dll")] private static extern int SHDoDragDrop(IntPtr owner, IDataObject data, IPinDropSource source, uint effects, out uint effect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
