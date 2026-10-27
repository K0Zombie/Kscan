using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kscan.App.Services;

namespace Kscan.App.Tests;

public sealed class PinDragTests
{
    [Theory]
    [InlineData(0, false, true)]
    [InlineData(0, true, false)]
    [InlineData(1, false, false)]
    [InlineData(2, false, true)]
    [InlineData(3, false, true)]
    [InlineData(-1, false, false)]
    public void Rejected_release_pins_but_accepted_transfer_or_cancel_does_not(int target, bool accepted, bool shouldPin)
    {
        var result = new PinDragResult(target < 0 ? null : (PinDragTarget)target, new(200, 300), accepted);
        Assert.Equal(shouldPin, result.ShouldPin);
    }

    [Theory]
    [InlineData(1, WindowsPinDrag.Cancel)]
    [InlineData(2, WindowsPinDrag.Cancel)]
    [InlineData(0, WindowsPinDrag.Drop)]
    [InlineData(3, WindowsPinDrag.Cancel)]
    public void Release_intercepts_local_targets_before_external_drop(int targetValue, int expected)
    {
        var target = (PinDragTarget)targetValue;
        var point = new PixelPoint(-1234, 567);
        var source = new WindowsPinDrag.Source(() => point, _ => target, _ => { }, () => true);
        Assert.Equal(0, source.QueryContinueDrag(false, 1));
        Assert.Null(source.Target);
        Assert.False(new PinDragResult(source.Target, source.Position, false).ShouldPin);
        Assert.Equal(expected, source.QueryContinueDrag(false, 0));
        Assert.Equal(target, source.Target);
        Assert.Equal(point, source.Position);
    }

    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 2)]
    public void Escape_closed_window_or_right_button_cancels_without_drop(bool escape, bool active, uint keys)
    {
        var source = new WindowsPinDrag.Source(() => new(10, 20), _ => PinDragTarget.Desktop, _ => { }, () => active);
        Assert.Equal(WindowsPinDrag.Cancel, source.QueryContinueDrag(escape, keys));
        Assert.Null(source.Target);
    }

    [Fact]
    public void Grab_position_accounts_for_letterboxing_and_negative_screen_coordinates()
    {
        var grab = PinDropPlacement.GrabFraction(new(25, 50), new(100, 100), new(200, 100));
        Assert.Equal(new Point(.25, .5), grab);
        Assert.Equal(new PixelRect(-1100, 100, 400, 200), PinDropPlacement.At(new(-1000, 200), new(400, 200), grab));
    }

    [Fact]
    public void Shell_file_data_exposes_unicode_original_file_and_source_has_native_interface()
    {
        if (!OperatingSystem.IsWindows()) return;
        RunSta(() =>
        {
        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
        var directory = Path.Combine(Path.GetTempPath(), "pin-drag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "测试 animation.gif");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "anim.gif"), path);
        IDataObject? data = null;
        try
        {
            data = WindowsPinDrag.CreateFileData(path);
            var format = new FORMATETC { cfFormat = 15, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
            Assert.Equal(0, data.QueryGetData(ref format));
            data.GetData(ref format, out var medium);
            try
            {
                Assert.Equal(1u, DragQueryFile(medium.unionmember, uint.MaxValue, null, 0));
                var filename = new StringBuilder(1024);
                DragQueryFile(medium.unionmember, 0, filename, (uint)filename.Capacity);
                Assert.Equal(path, filename.ToString());
            }
            finally { ReleaseStgMedium(ref medium); }
            var source = new WindowsPinDrag.Source(() => new(0, 0), _ => PinDragTarget.External, _ => { }, () => true);
            var pointer = Marshal.GetComInterfaceForObject(source, typeof(IPinDropSource));
            Assert.NotEqual(IntPtr.Zero, pointer);
            try
            {
                var vtable = Marshal.ReadIntPtr(pointer);
                var query = Marshal.GetDelegateForFunctionPointer<QueryNative>(Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
                Assert.Equal(WindowsPinDrag.Cancel, query(pointer, 1, 0));
                Assert.Null(source.Target);
            }
            finally { Marshal.Release(pointer); }
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (data is not null) Marshal.ReleaseComObject(data);
            File.Delete(path);
            Directory.Delete(directory);
            OleUninitialize();
        }
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Shell 测试超时");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryNative(IntPtr source, int escape, uint keys);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? name, uint count);
}
