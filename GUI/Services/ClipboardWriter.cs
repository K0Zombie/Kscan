using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Kscan.Core.Imaging;
using Kscan.Platform;
using System.Diagnostics;
using System.IO.Compression;

namespace Kscan.App.Services;

/// <summary>
/// 把图片写进剪贴板：<b>同时</b>放位图和一个真实的 PNG 文件。
/// <para>
/// 只放位图的话，粘到聊天窗口、编辑器里没问题，但**在资源管理器里按 Ctrl+V 什么也粘不出来** ——
/// 那边要的是"文件"（Windows 的 <c>CF_HDROP</c>）。所以这里顺手把图落成一个临时 PNG，
/// 再把它的文件项一起塞进同一个数据包（Avalonia 12 的 <c>IDataTransfer</c> 支持多格式并存）。
/// </para>
/// <para>
/// 临时 PNG 只是"加持"：<b>写不出来也照样把位图放进剪贴板</b>。以前整条链共用一个 try，
/// 临时目录一被占（杀软扫描、磁盘满、权限被锁）就连位图一起放弃，用户只看到"剪贴板不可用"。
/// </para>
/// <para>
/// 位图对象<b>不要</b>在这里 Dispose：Windows 剪贴板是"延迟取数据"的（OLE 延迟渲染），
/// 位图要活到别人真正来粘贴的那一刻（提前释放会让剪贴板里"看着有位图"却谁也读不出来）。
/// </para>
/// </summary>
internal static class ClipboardWriter
{
    /// <summary>
    /// 临时 PNG 的候选目录（按顺序试，第一个写得进去的用；结果缓存）。
    /// <para>
    /// 首选仍然是系统临时目录（与旧版本一致）。但实测有机器在某些运行状态下
    /// <c>%TEMP%</c> 里新建文件会报"拒绝访问"（ACL 明明给足了权限），此时整条"文件项"就没了 ——
    /// 表现是"能粘进聊天窗口，但在桌面/资源管理器里粘不出东西"。
    /// 所以这里串一条回退链，任何一环成功都能保住文件项。
    /// </para>
    /// </summary>
    private static IEnumerable<string> CandidateDirectories()
    {
        yield return Path.Combine(Path.GetTempPath(), "kscan-clip");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kscan", "clip");
        yield return Path.Combine(PlatformPaths.ConfigDirectory, "clip");
        yield return Path.Combine(AppContext.BaseDirectory, "clip");
    }

    /// <summary>已经验证过可写的目录（进程内缓存，避免每次都从第一个试起）。</summary>
    private static string? _usableDirectory;

    /// <summary>保留最近的临时 PNG 数量，超出的旧文件在每次写入后清掉。</summary>
    private const int KeepFiles = 200;
    private static readonly ClipboardCopyGate CopyGate = new();
    private static int _pruneRunning;
    private static long _nextPruneTime;
    private static ClipboardCopyGate.Ticket BeginCopy() => CopyGate.Begin(PlatformServices.ClipboardWatcher.SequenceNumber);
    private static bool IsCurrent(ClipboardCopyGate.Ticket ticket) => CopyGate.CanPublish(ticket, PlatformServices.ClipboardWatcher.SequenceNumber);

    /// <summary>把一张像素帧写进剪贴板（位图 + 临时 PNG 文件）；返回是否成功。</summary>
    public static async Task<bool> CopyAsync(ImageFrame frame, string baseName = "qrcode")
    {
        var ticket = BeginCopy();
        var total = Stopwatch.StartNew();
        var path = await PreparePngAsync(frame, baseName);
        if (!IsCurrent(ticket)) { GuiLog.Write("clipboard: skipped stale image copy"); return false; }
        Bitmap bitmap;
        try
        {
            bitmap = ResultActions.ToBitmap(frame);
        }
        catch (Exception ex)
        {
            GuiLog.Write($"clipboard: ToBitmap failed ({ex.GetType().Name}: {ex.Message})");
            return false;
        }

        var success = await SetAsync(bitmap, path, ticket);
        GuiLog.Write($"perf: clipboard image {frame.Width}x{frame.Height} total={total.ElapsedMilliseconds}ms success={success}");
        return success;
    }

    /// <summary>编码和文件 I/O 全在后台完成；只在文件就绪后一次性发布两种剪贴板格式。</summary>
    internal static Task<string?> PreparePngAsync(ImageFrame frame, string baseName, Func<ImageFrame, byte[]>? encoder = null) => Task.Run(async () =>
    {
        var watch = Stopwatch.StartNew();
        byte[] bytes;
        try { bytes = (encoder ?? SkiaImageCodec.EncodePngFast)(frame); }
        catch (Exception ex) { GuiLog.Write($"clipboard: png encode failed ({ex.Message})"); return null; }
        var encodeMs = watch.ElapsedMilliseconds;
        var path = await TryWriteTempPngAsync(baseName, file => File.WriteAllBytesAsync(file, bytes), frame.Width, frame.Height);
        GuiLog.Write($"perf: clipboard png {frame.Width}x{frame.Height} encode={encodeMs}ms file={watch.ElapsedMilliseconds - encodeMs}ms bytes={bytes.Length}");
        return path;
    });

    /// <summary>
    /// 把**文件本身**写进剪贴板（只放文件项，不放位图）。
    /// <para>
    /// 动图用这条：复制出来的就是那个 .gif/.webp，粘到浏览器/聊天窗口/资源管理器里都还是动图。
    /// 不能顺带放位图 —— 有些程序会优先取位图，结果粘出来只剩一帧静态图。
    /// </para>
    /// </summary>
    public static async Task<bool> CopyFileAsync(string sourcePath)
    {
        var ticket = BeginCopy();
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            GuiLog.Write($"clipboard: file copy skipped (missing) {sourcePath}");
            return false;
        }

        if (TopLevelAccessor.GetClipboard() is not { } clipboard)
        {
            GuiLog.Write("clipboard: TopLevelAccessor.GetClipboard() returned null");
            return false;
        }

        if (TopLevelAccessor.GetStorageProvider() is not { } storage)
        {
            GuiLog.Write("clipboard: file copy skipped (no storage provider)");
            return false;
        }

        try
        {
            if (await storage.TryGetFileFromPathAsync(new Uri(sourcePath)) is not { } file)
            {
                GuiLog.Write($"clipboard: file copy skipped (TryGetFile failed) {sourcePath}");
                return false;
            }

            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateFile(file));
            if (!IsCurrent(ticket)) return false;
            await clipboard.SetDataAsync(transfer);
        }
        catch (Exception ex)
        {
            GuiLog.Write($"clipboard: file copy failed ({ex.GetType().Name}: {ex.Message})");
            return false;
        }

        PlatformServices.ClipboardWatcher.MarkHandled();
        GuiLog.Write($"clipboard: copied file {sourcePath}");
        return true;
    }

    /// <summary>把一张已经在内存里的位图写进剪贴板（位图 + 临时 PNG 文件）；返回是否成功。</summary>
    public static async Task<bool> CopyAsync(Bitmap bitmap, string baseName = "image")
    {
        var ticket = BeginCopy();
        var path = await TryWriteTempPngAsync(baseName, pngPath =>
        {
            // RenderTargetBitmap 等由 UI 后端持有，保留原线程保存以保证生命周期与线程兼容。
            bitmap.Save(pngPath, new PngBitmapEncoderOptions { CompressionLevel = CompressionLevel.Fastest });
            return Task.CompletedTask;
        }, bitmap.PixelSize.Width, bitmap.PixelSize.Height);

        return await SetAsync(bitmap, path, ticket);
    }

    /// <summary>
    /// 尽力写一张临时 PNG 并返回**实际写入**的路径；失败返回 <c>null</c>（调用方继续只放位图）。
    /// <para>
    /// 路径必须只算一次再交给 <paramref name="write"/>：文件名带毫秒时间戳，算两次会得到两个名字，
    /// 于是"写到了 A、返回了 B" —— 文件项指向不存在的文件，资源管理器里粘贴就什么都没了。
    /// </para>
    /// <para>
    /// 失败原因写进 gui.log —— 只靠界面那句"剪贴板不可用"没法定位问题。
    /// </para>
    /// </summary>
    internal static async Task<string?> TryWriteTempPngAsync(string baseName, Func<string, Task> write, int width = 0, int height = 0)
    {
        var directories = _usableDirectory is { } known
            ? new[] { known }.Concat(CandidateDirectories().Where(dir => dir != known))
            : CandidateDirectories();

        foreach (var directory in directories)
        {
            var kind = baseName is "screenshot" or "scroll" or "annotation" or "barcode" ? baseName : "pin";
            using var reservation = FileNaming.ReservePath(directory, ".png", FileNaming.Context(kind, width, height, extension: "png"));
            var path = reservation.Path;

            try
            {
                Directory.CreateDirectory(directory);
                await write(path);

                _usableDirectory = directory;
                SchedulePrune(directory);
                return path;
            }
            catch (Exception ex)
            {
                // 把提权状态也记下来：这条日志是排查"写不进临时目录"的唯一线索
                GuiLog.Write(
                    $"clipboard: temp png failed in {directory} ({ex.GetType().Name}: {ex.Message}) elevated={PlatformServices.Elevation.IsElevated}");
            }
        }

        return null;
    }

    private static void SchedulePrune(string directory)
    {
        if (Environment.TickCount64 < Volatile.Read(ref _nextPruneTime) || Interlocked.CompareExchange(ref _pruneRunning, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try { PruneOldFiles(directory); }
            finally
            {
                Volatile.Write(ref _nextPruneTime, Environment.TickCount64 + 60_000);
                Volatile.Write(ref _pruneRunning, 0);
            }
        });
    }

    /// <summary>只保留最近的若干张临时 PNG，避免目录无限膨胀。</summary>
    private static void PruneOldFiles(string directory)
    {
        try
        {
            var files = new DirectoryInfo(directory).GetFiles("*.png");
            if (files.Length <= KeepFiles)
            {
                return;
            }

            foreach (var file in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepFiles))
            {
                // 快速连续截图时不要删掉仍在准备发布或刚发布的文件。
                if (file.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-5)) continue;
                try
                {
                    file.Delete();
                }
                catch (Exception)
                {
                    // 正被剪贴板引用/占用：下次再清
                }
            }
        }
        catch (Exception)
        {
            // 清理失败不影响这次复制
        }
    }

    private static async Task<bool> SetAsync(Bitmap bitmap, string? path, ClipboardCopyGate.Ticket ticket)
    {
        if (!IsCurrent(ticket)) { GuiLog.Write("clipboard: skipped stale copy before publish"); return false; }
        if (TopLevelAccessor.GetClipboard() is not { } clipboard)
        {
            GuiLog.Write("clipboard: TopLevelAccessor.GetClipboard() returned null");
            return false;
        }

        try
        {
            var watch = Stopwatch.StartNew();
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(DataFormat.Bitmap, bitmap));

            // 文件项：拿不到（平台不支持 / 路径异常）就只给位图，至少还能粘到编辑器里。
            // 先 File.Exists 再交给 StorageProvider：文件项指向不存在的路径时，剪贴板里"看着有文件"
            // 但资源管理器粘不出来，比干脆不给更让人困惑。
            if (path is null)
            {
                GuiLog.Write("clipboard: file item skipped (no temp png)");
            }
            else if (!File.Exists(path))
            {
                GuiLog.Write($"clipboard: file item skipped (temp png missing) {path}");
            }
            else if (TopLevelAccessor.GetStorageProvider() is not { } storage)
            {
                GuiLog.Write("clipboard: file item skipped (no storage provider)");
            }
            else if (await storage.TryGetFileFromPathAsync(new Uri(path)) is not { } file)
            {
                GuiLog.Write($"clipboard: file item skipped (TryGetFile failed) {path}");
            }
            else
            {
                transfer.Add(DataTransferItem.CreateFile(file));
                GuiLog.Write($"clipboard: file item added {path}");
            }

            if (!IsCurrent(ticket)) { GuiLog.Write("clipboard: skipped stale copy after file preparation"); return false; }
            var prepareMs = watch.ElapsedMilliseconds;
            await clipboard.SetDataAsync(transfer);
            GuiLog.Write($"perf: clipboard publish file-item={prepareMs}ms set={watch.ElapsedMilliseconds - prepareMs}ms");
        }
        catch (Exception ex)
        {
            // 最常见的是剪贴板被别的程序占着（OLE 的 CLIPBRD_E_CANT_OPEN）
            GuiLog.Write($"clipboard: SetDataAsync failed ({ex.GetType().Name}: {ex.Message})");
            return false;
        }

        // 自己写的剪贴板内容，别被剪贴板监听当成"新图片"再识别一遍
        PlatformServices.ClipboardWatcher.MarkHandled();
        return true;
    }

}
