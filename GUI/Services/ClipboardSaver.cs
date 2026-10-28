using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;

namespace Kscan.App.Services;

/// <summary>
/// 把剪贴板里<b>最近的一项</b>存成文件：图片 → PNG，文本 → TXT。
/// <para>
/// 热键「保存剪贴板内容到文件夹」用它。目标文件夹由调用方从设置里取，
/// 这里只负责"读剪贴板 + 写文件"，不做任何界面交互（热键可能在后台触发）。
/// </para>
/// <para>
/// 文件按命名模板保存，重名时追加序号，
/// 不覆盖已有文件 —— 保存这类动作最忌讳"悄悄盖掉上一次的内容"。
/// </para>
/// </summary>
internal static class ClipboardSaver
{
    /// <summary>
    /// 保存剪贴板内容。成功返回写出的文件路径；剪贴板里既没有图片也没有文本时返回 <c>null</c>。
    /// </summary>
    public static async Task<string?> SaveLatestAsync(string folder, IClipboard clipboard)
    {
        Directory.CreateDirectory(folder);

        // 图片优先：「截图后保存」是最常见的用法。注意位图归本方法所有，用完要释放。
        var bitmap = await clipboard.TryGetBitmapAsync();
        if (bitmap is not null)
        {
            using (bitmap)
            {
                using var reservation = FileNaming.ReservePath(folder, ".png", FileNaming.Context("screenshot", bitmap.PixelSize.Width, bitmap.PixelSize.Height, extension: "png"));
                var path = reservation.Path;
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                bitmap.Save(output, new PngBitmapEncoderOptions());
                return path;
            }
        }

        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        using var textReservation = FileNaming.ReservePath(folder, ".txt", FileNaming.Context("clipboard", extension: "txt"));
        var textPath = textReservation.Path;
        await using var outputText = new FileStream(textPath, FileMode.CreateNew, FileAccess.Write);
        await using var writer = new StreamWriter(outputText);
        await writer.WriteAsync(text);
        return textPath;
    }

}
