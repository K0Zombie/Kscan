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
/// 文件按时间戳命名（<c>clipboard-20261017-223015.png</c>），同一秒内重复触发时追加序号，
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

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        // 图片优先：「截图后保存」是最常见的用法。注意位图归本方法所有，用完要释放。
        var bitmap = await clipboard.TryGetBitmapAsync();
        if (bitmap is not null)
        {
            using (bitmap)
            {
                var path = NextPath(folder, $"clipboard-{stamp}", ".png");
                bitmap.Save(path, new PngBitmapEncoderOptions());
                return path;
            }
        }

        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var textPath = NextPath(folder, $"clipboard-{stamp}", ".txt");
        await File.WriteAllTextAsync(textPath, text);
        return textPath;
    }

    /// <summary>目标文件已存在时追加 <c>-1</c> / <c>-2</c> …，绝不覆盖。</summary>
    private static string NextPath(string folder, string baseName, string extension)
    {
        var path = Path.Combine(folder, baseName + extension);

        for (var index = 1; File.Exists(path); index++)
        {
            path = Path.Combine(folder, $"{baseName}-{index}{extension}");
        }

        return path;
    }
}
