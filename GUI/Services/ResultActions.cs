using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Kscan.Core.Imaging;
using Kscan.Core.Model;
using Kscan.Core.Pipeline;
using Kscan.Platform;

namespace Kscan.App.Services;

/// <summary>识别结果上可执行的动作。全部用系统默认程序，不自己实现浏览器/拨号。</summary>
public static class ResultActions
{
    /// <summary>把文本写进系统剪贴板。</summary>
    public static async Task<bool> CopyAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is null)
        {
            return false;
        }

        var clipboard = TopLevelAccessor.GetClipboard();
        if (clipboard is null)
        {
            return false;
        }

        try
        {
            await clipboard.SetTextAsync(text);

            // 自己写的剪贴板不算"新内容"（监听只关心图片，这里顺手省掉一次无谓的读取）
            PlatformServices.ClipboardWatcher.MarkHandled();

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 用系统默认程序打开目标：URL 走浏览器，<c>tel:</c>/<c>geo:</c>/<c>mailto:</c> 走 desktop 注册的处理器，
    /// 文件路径走默认关联程序。Linux 上 <c>UseShellExecute=true</c> 会转成 <c>xdg-open</c>。
    /// </summary>
    public static Task<(bool Success, string? Error)> OpenAsync(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return Task.FromResult<(bool, string?)>((true, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult<(bool, string?)>((false, ex.Message));
        }
    }

    /// <summary>让用户选一个或多个图片文件。</summary>
    public static async Task<IReadOnlyList<string>> PickImagesAsync()
    {
        var storage = TopLevelAccessor.GetStorageProvider();
        if (storage is null)
        {
            return [];
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = I18n.T("Str.Scan.PickImagesTitle"),
            AllowMultiple = true,
            FileTypeFilter = [ImageFileType()],
        });

        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    /// <summary>让用户选一张图片文件（例如生成条码时用的 Logo）。</summary>
    public static async Task<string?> PickImageAsync(string? title = null)
    {
        var storage = TopLevelAccessor.GetStorageProvider();
        if (storage is null)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title ?? I18n.T("Str.Dialog.PickImage"),
            AllowMultiple = false,
            FileTypeFilter = [ImageFileType()],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>让用户选保存位置（导出生成的条码）。</summary>
    public static async Task<string?> PickSavePngAsync(string suggestedName)
    {
        var storage = TopLevelAccessor.GetStorageProvider();
        if (storage is null)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = I18n.T("Str.Dialog.SaveBarcode"),
            SuggestedFileName = suggestedName,
            DefaultExtension = "png",
            FileTypeChoices =
            [
                new FilePickerFileType(I18n.T("Str.Dialog.PngType")) { Patterns = ["*.png"] },
                new FilePickerFileType(I18n.T("Str.Dialog.SvgType")) { Patterns = ["*.svg"] },
            ],
        });

        return file?.TryGetLocalPath();
    }

    /// <summary>让用户选保存位置（动图按原始格式另存，例如 .gif / .webp）。</summary>
    public static async Task<string?> PickSaveImageAsync(string suggestedName, string extension)
    {
        var storage = TopLevelAccessor.GetStorageProvider();
        if (storage is null)
        {
            return null;
        }

        var ext = string.IsNullOrWhiteSpace(extension) ? "gif" : extension.TrimStart('.').ToLowerInvariant();

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = I18n.T("Str.Pin.SaveAs"),
            SuggestedFileName = suggestedName,
            DefaultExtension = ext,
            FileTypeChoices =
            [
                new FilePickerFileType(ext.ToUpperInvariant()) { Patterns = ["*." + ext] },
            ],
        });

        return file?.TryGetLocalPath();
    }

    /// <summary>
    /// 把图像帧变 Avalonia 位图（用于界面预览）。
    /// <para>
    /// 直接拷像素：<see cref="ImageFrame"/> 本来就是 BGRA32（straight alpha），与 Avalonia 的
    /// <c>Bgra8888 + Unpremul</c> 一一对应，不需要任何转换。
    /// 以前这里是"编码成 PNG、再让 Avalonia 解码"——整屏抓图（2560×1440）光这一来一回就要几百毫秒，
    /// 而它跑在 UI 线程上、还在框选窗口 <c>Show()</c> <b>之前</b>，所以表现正是"触发框选时先卡一下"。
    /// </para>
    /// </summary>
    public static Avalonia.Media.Imaging.Bitmap ToBitmap(ImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(
            new PixelSize(frame.Width, frame.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using var buffer = bitmap.Lock();
        var rowBytes = frame.Stride;

        for (var row = 0; row < frame.Height; row++)
        {
            // 逐行拷：锁定缓冲的行距可能带对齐填充，不等于图像行距
            Marshal.Copy(
                frame.Pixels,
                row * rowBytes,
                buffer.Address + (row * buffer.RowBytes),
                rowBytes);
        }

        return bitmap;
    }

    /// <summary>识别一帧图像。</summary>
    public static ScanOutcome Scan(ImageFrame frame) => ScanPipeline.CreateDefault().Scan(frame);

    /// <summary>当前平台能力（设置页展示）。</summary>
    public static PlatformCapabilities Capabilities => PlatformServices.Capabilities;

    /// <summary>按内容类型解析识别结果。</summary>
    public static ResultContent Parse(string text) => ResultContent.Parse(text);

    /// <summary>图片文件类型过滤器（文案走 i18n，每次现取）。</summary>
    private static FilePickerFileType ImageFileType() => new(I18n.T("Str.Dialog.ImagesType"))
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"],
    };
}

/// <summary>
/// 取当前窗口的剪贴板 / StorageProvider。
/// Avalonia 里这两样都属于 TopLevel（窗口），所以必须从当前活动窗口拿，不能从静类里拿。
/// </summary>
internal static class TopLevelAccessor
{
    public static Window? ActiveWindow =>
        Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow
            : null;

    /// <summary>
    /// 任意一个还活着的窗口，用来取 <c>Screens</c>。
    /// <para>
    /// 不能只用 <see cref="ActiveWindow"/>：点一下别的屏幕之后 kscan 没有任何活动窗口，
    /// 而主窗口收进托盘时 <c>desktop.MainWindow</c> 会被置空 —— 两者都拿不到，取屏就失败，
    /// 按热键会退化成"抓整个虚拟桌面"，表现为"这块屏上按热键没反应"。
    /// </para>
    /// </summary>
    public static Window? AnyWindow
    {
        get
        {
            if (Application.Current?.ApplicationLifetime is not
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                return null;
            }

            return desktop.Windows.FirstOrDefault(w => w.IsActive)
                   ?? desktop.MainWindow
                   ?? desktop.Windows.FirstOrDefault();
        }
    }

    public static Screens? GetScreens() => AnyWindow?.Screens;

    public static IClipboard? GetClipboard() => ActiveWindow?.Clipboard;

    public static IStorageProvider? GetStorageProvider() => ActiveWindow?.StorageProvider;
}
