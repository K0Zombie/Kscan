using System.Runtime.InteropServices;
using Kscan.App.Services;

namespace Kscan.App.Tests;

public sealed class PinNativeSizeLimitTests
{
    [Theory]
    [InlineData(1, 8192)]
    [InlineData(1.25, 10240)]
    [InlineData(1.5, 12288)]
    [InlineData(2, 16384)]
    public void Native_tracking_limit_allows_zoom_beyond_monitor_size_without_changing_other_limits(double scaling, int expected)
    {
        var limits = new PinNativeSizeLimit.Limits
        {
            MaxTrackSize = new() { X = 1920, Y = 1080 },
            MaxSize = new() { X = 1920, Y = 1040 },
            MaxPosition = new() { X = -2560, Y = -365 },
            MinTrackSize = new() { X = 40, Y = 40 },
        };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<PinNativeSizeLimit.Limits>());
        try
        {
            Marshal.StructureToPtr(limits, pointer, false);
            PinNativeSizeLimit.Apply(PinNativeSizeLimit.GetMinMaxInfo, pointer, 8192, scaling);
            var result = Marshal.PtrToStructure<PinNativeSizeLimit.Limits>(pointer);
            Assert.Equal(expected, result.MaxTrackSize.X);
            Assert.Equal(expected, result.MaxTrackSize.Y);
            Assert.Equal(limits.MaxPosition.X, result.MaxPosition.X);
            Assert.Equal(limits.MaxPosition.Y, result.MaxPosition.Y);
            Assert.Equal(limits.MinTrackSize.X, result.MinTrackSize.X);
            Assert.Equal(limits.MaxSize.Y, result.MaxSize.Y);
            PinNativeSizeLimit.Apply(0x0005, pointer, 1024, scaling);
            Assert.Equal(expected, Marshal.PtrToStructure<PinNativeSizeLimit.Limits>(pointer).MaxTrackSize.X);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
}
