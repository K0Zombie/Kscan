using System.Buffers.Binary;
using Kscan.App.Services;

namespace Kscan.App.Tests;

public class ClipboardImageReaderTests
{
    [Theory]
    [InlineData(24, 2)]
    [InlineData(24, -2)]
    [InlineData(32, 2)]
    [InlineData(32, -2)]
    public void Reserved_alpha_does_not_wash_out_rgb(int bits, int height)
    {
        var data = new byte[48]; // 1 pixel wide, two DWORD-aligned rows
        BinaryPrimitives.WriteInt32LittleEndian(data, 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), height);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(14), (short)bits);
        new byte[] { 20, 40, 60, 0, 80, 100, 120, 17 }.CopyTo(data, 40);
        var frame = ClipboardImageReader.DecodeOpaqueDib(data)!;
        Assert.NotNull(frame);
        Assert.Equal(height < 0
            ? new byte[] { 20, 40, 60, 255, 80, 100, 120, 255 }
            : new byte[] { 80, 100, 120, 255, 20, 40, 60, 255 }, frame.Pixels);
        Assert.Null(ClipboardImageReader.DecodeOpaqueDib(data.AsSpan(0, 47)));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 3);
        Assert.Null(ClipboardImageReader.DecodeOpaqueDib(data)); // explicit masks use normal decoder
    }
}
