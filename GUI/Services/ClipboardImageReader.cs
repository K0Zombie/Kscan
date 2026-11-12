using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Kscan.Core.Imaging;

namespace Kscan.App.Services;

internal static class ClipboardImageReader
{
    public static async Task<Bitmap?> ReadAsync(IClipboard clipboard)
    {
        // BI_RGB DIBs have reserved bytes, not premultiplied alpha. Some producers
        // leave them zero or uninitialized; passing these to a premul decoder washes out RGB.
        if (OperatingSystem.IsWindows()
            && clipboard.GetType().Assembly.GetName().Name == "Avalonia.Win32"
            && TryReadOpaqueDib() is { } frame)
            return ResultActions.ToBitmap(frame);
        return await clipboard.TryGetBitmapAsync();
    }

    private static ImageFrame? TryReadOpaqueDib()
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            // PNG carries defined alpha and must keep the platform's preferred path.
            if (IsClipboardFormatAvailable(RegisterClipboardFormat("PNG"))) return null;
            var handle = GetClipboardData(8); // CF_DIB, no clipboard mutation
            if (handle == IntPtr.Zero) return null;
            var size = GlobalSize(handle).ToUInt64();
            if (size < 40 || size > 512 * 1024 * 1024) return null;
            var address = GlobalLock(handle);
            if (address == IntPtr.Zero) return null;
            try
            {
                var bytes = new byte[(int)size];
                Marshal.Copy(address, bytes, 0, bytes.Length);
                return DecodeOpaqueDib(bytes);
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    internal static ImageFrame? DecodeOpaqueDib(ReadOnlySpan<byte> data)
    {
        if (data.Length < 40) return null;
        var header = BinaryPrimitives.ReadUInt32LittleEndian(data);
        // Extended headers may carry real alpha masks or color profiles: leave them
        // to the platform decoder rather than discarding their metadata.
        if (header != 40) return null;
        var width = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        if (width <= 0 || signedHeight == 0 || signedHeight == int.MinValue
            || BinaryPrimitives.ReadUInt16LittleEndian(data[12..]) != 1
            || bits is not (24 or 32)
            || BinaryPrimitives.ReadUInt32LittleEndian(data[16..]) != 0) return null;
        var height = Math.Abs(signedHeight);
        var stride = ((long)width * bits + 31) / 32 * 4;
        var offset = 40L + BinaryPrimitives.ReadUInt32LittleEndian(data[32..]) * 4L;
        if (offset + stride * height > data.Length || (long)width * height * 4 > int.MaxValue) return null;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sourceY = signedHeight < 0 ? y : height - 1 - y;
            var row = data.Slice((int)(offset + sourceY * stride), (int)stride);
            for (var x = 0; x < width; x++)
            {
                var source = x * (bits / 8);
                var target = (y * width + x) * 4;
                pixels[target] = row[source];
                pixels[target + 1] = row[source + 1];
                pixels[target + 2] = row[source + 2];
                pixels[target + 3] = 255;
            }
        }
        return new ImageFrame(width, height, pixels);
    }

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
}
