using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Kscan.App.Services;

namespace Kscan.App.Tests;

public sealed class ClipboardFilePinTests
{
    [AvaloniaTheory]
    [InlineData("anim.gif")]
    [InlineData("anim.webp")]
    public async Task File_clipboard_pins_an_animation_instead_of_its_bitmap_preview(string asset)
    {
        var host = new Window(); host.Show();
        var manager = new PinManager();
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", asset);
            // The headless provider cannot resolve local files. Use Avalonia's own file item
            // to exercise real DataTransfer file+bitmap handling (storage interfaces are sealed).
            var type = typeof(IStorageFile).Assembly.GetType("Avalonia.Platform.Storage.FileIO.BclStorageFile", throwOnError: true)!;
            using var file = (IStorageFile)Activator.CreateInstance(type,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                null, [new FileInfo(path)], null)!;
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateFile(file));
            using var preview = new Avalonia.Media.Imaging.Bitmap(path);
            transfer.Add(DataTransferItem.Create(DataFormat.Bitmap, preview));
            await host.Clipboard!.SetDataAsync(transfer);
            Assert.True(await manager.PinFromClipboardAsync(host.Clipboard));
            Assert.True(manager.Last!.IsAnimated);
            Assert.Equal(path, manager.Last.RetainedSourcePath);
        }
        finally { foreach (var pin in manager.Windows.ToArray()) pin.Close(); host.Close(); }
    }


    [AvaloniaFact]
    public void Invalid_or_non_image_files_do_not_create_pins()
    {
        var manager = new PinManager();
        Assert.False(manager.PinClipboardFile(Path.Combine(Path.GetTempPath(), "missing-pin.gif")));
        var path = Path.Combine(Path.GetTempPath(), "invalid-pin-" + Guid.NewGuid().ToString("N") + ".gif");
        try
        {
            File.WriteAllText(path, "not an image");
            Assert.False(manager.PinClipboardFile(path));
            Assert.False(manager.PinClipboardFile(Path.ChangeExtension(path, ".mp4")));
            Assert.Empty(manager.Windows);
        }
        finally { File.Delete(path); }
    }
}
