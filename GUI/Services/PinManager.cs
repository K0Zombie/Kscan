using Avalonia;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Kscan.App.Views;

namespace Kscan.App.Services;

/// <summary>
/// 进程内的贴图管理：谁开着、有几张、关掉全部、以及把穿透状态收回来。
/// 贴图窗口自己关闭时也会从这里摘掉。
/// </summary>
public sealed class PinManager
{
    /// <summary>与 <c>PinWindow.MinSize</c> 一致：小于等于它的"尺寸记录"视为无效（历史 bug 的产物）。</summary>
    private const int MinPinSize = 40;

    private readonly List<PinWindow> _windows = [];

    public static PinManager Current { get; } = new();

    /// <summary>当前贴图数量。</summary>
    public int Count => _windows.Count;

    /// <summary>当前屏幕上的全部贴图（按贴上顺序，后贴的在后面）。</summary>
    public IReadOnlyList<PinWindow> Windows => _windows;

    /// <summary>数量变化（新贴一张 / 关掉一张 / 关掉全部）。</summary>
    public event EventHandler? Changed;

    /// <summary>贴一张图到屏幕上。</summary>
    /// <param name="ownsImage">
    /// 这张位图是否由贴图窗口负责释放。剪贴板读出来的位图是"我们的"（要释放），
    /// 识别页的预览图是别人（ViewModel）持有的（不能释放）。
    /// </param>
    /// <param name="targetRegion">
    /// 贴到屏幕的哪块区域（物理像素）；<c>null</c> = 屏幕中央。用于"截图贴回原位"。
    /// </param>
    public PinWindow Pin(
        Bitmap image,
        string? sourceName = null,
        bool ownsImage = false,
        PixelRect? targetRegion = null,
        double? scaling = null)
    {
        var window = new PinWindow();
        window.SetImage(image, sourceName, ownsImage);
        window.SetPlacement(targetRegion, scaling);

        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            RaiseChanged();
        };

        _windows.Add(window);

        // 位置/尺寸/旋转/透明度/置顶/穿透变化后防抖落盘（只对进了库的贴图有效）
        window.StateChanged += (_, _) => PinLibraryService.SaveWindowState(window);

        // Show() 之后才有屏幕信息与平台句柄，窗口自己在 OnOpened 里把尺寸/位置摆正
        window.Show();

        // 设置里关掉「新贴图默认置顶」时，贴出来就落到普通层级（单张仍可右键/热键再改）
        if (!AppSettingsService.Current.Settings.PinTopmostByDefault)
        {
            window.SetTopmost(false);
        }

        RaiseChanged();

        return window;
    }

    /// <summary>
    /// 从分组库里把一张图贴回屏幕（沿用库里的位置/尺寸/旋转/透明度）。
    /// 已经贴在屏幕上时只把它提到最前，不会开第二个副本。
    /// </summary>
    public PinWindow? PinFromLibrary(PinLibraryItem item)
    {
        if (item.Window is { } existing)
        {
            existing.Activate();
            existing.EnterRaised();
            return existing;
        }

        if (PinLibraryService.LoadBitmap(item) is not { } bitmap)
        {
            return null;
        }

        var entry = item.Entry;

        // 库里新丢进来的图**没有几何记录**（宽高都是 0）：这时必须当成"没指定位置"，
        // 让窗口走"屏幕中央 + 自然尺寸"。以前这里塞了个 1x1 的目标矩形，
        // 结果贴出来是 1×1 物理像素、还卡在左上角（用户实测就是这个）。
        //
        // 另外：早期版本把这个 1x1 夹到最小尺寸后**写进了库**（40x40）。
        // 这种"两边都正好是最小尺寸"的记录也当作没摆过，否则那些图永远只能贴成 40×40。
        var hasGeometry = entry.Width > MinPinSize && entry.Height > MinPinSize;

        PixelRect? region = hasGeometry
            ? new PixelRect(entry.X, entry.Y, entry.Width, entry.Height)
            : null;

        if (!hasGeometry && (entry.Width > 0 || entry.Height > 0))
        {
            GuiLog.Write($"pin: 库里的几何记录不可用（{entry.Width}x{entry.Height}），按居中 + 自然尺寸贴出");
        }

        var window = Pin(bitmap, item.FileName, ownsImage: true, targetRegion: region);

        if (SvgPinRenderer.IsSvgFile(item.FullPath))
        {
            window.AttachToLibrary(item);
            return window;
        }

        // 动图：多帧解码成功就播起来（失败/超限/APNG 就按首帧静态显示）
        if (AnimatedImage.TryLoad(item.FullPath, out var reason) is { } animation)
        {
            var (frames, delays) = animation.TakeFrames();   // 帧的所有权转给播放器
            window.ShowAnimation(new AnimatedPinPlayer(frames, delays));
        }
        else if (reason is { Length: > 0 })
        {
            GuiLog.Write($"animated: {item.FileName} 按首帧显示（{reason}）");
        }

        window.AttachToLibrary(item);

        return window;
    }

    /// <summary>
    /// 软关闭（ESC / Alt+Q / 管理窗口的「关闭」）：只关窗口、释放位图，
    /// <b>库里的文件与参数都留着</b>，随时可以从管理窗口再贴出来。
    /// </summary>
    public void CloseSoft(PinWindow window)
    {
        // 关窗之前先把最后的状态写进库参数（关掉之后 Position 可能已经归零，读不得了）
        if (window.LibraryPath is { Length: > 0 })
        {
            PinLibraryService.SaveWindowState(window, immediate: true);
        }

        window.Close();
    }

    /// <summary>硬删除：关窗 + 释放 + 从分组移除 + 删磁盘文件。</summary>
    public void DeleteHard(PinWindow window)
    {
        PinLibraryService.DeleteWindow(window);
    }

    /// <summary>用户是否已经确认过"Delete 会彻底删除"（确认过就不再拦）。</summary>
    public bool IsDeleteConfirmed => DeleteConfirmedOverride ?? AppSettingsService.Current.Settings.PinDeleteConfirmed;

    /// <summary>测试用：绕过真实设置文件判断"是否已确认过 Delete"。</summary>
    internal static bool? DeleteConfirmedOverride { get; set; }

    /// <summary>记住"已经提示过 Delete 的后果"（只写一次，失败也不影响删除）。</summary>
    public void ConfirmDeleteOnce()
    {
        if (IsDeleteConfirmed)
        {
            return;
        }

        if (DeleteConfirmedOverride is not null)
        {
            DeleteConfirmedOverride = true;   // 测试里别去写真实设置文件
            return;
        }

        _ = AppSettingsService.Current.UpdateAsync(current => current with { PinDeleteConfirmed = true });
    }

    /// <summary>
    /// 贴图窗口上的 Delete：第一次只**提示**（胶囊说明"再按一次 Delete 就会彻底删除"），
    /// 5 秒内再按一次才真删；确认过一次之后就直删。
    /// <para>
    /// 不用模态对话框：贴图是置顶无边框窗口，弹模态框在实机上出现过"按了没反应"，改成两段式更稳。
    /// </para>
    /// </summary>
    public void RequestDelete(PinWindow window)
    {
        if (!IsDeleteConfirmed && !window.IsDeleteArmed)
        {
            window.ArmDeleteHint();
            GuiLog.Write("pin: Delete 第一次，已进入确认态");
            return;
        }

        ConfirmDeleteOnce();
        DeleteHard(window);
    }

    /// <summary>
    /// 优先贴剪贴板图片；没有图片时，若文本是一个图片地址且设置里允许联网，就下载后再贴；
    /// 否则把文本按 Markdown 渲染。两者都没有时返回 <c>false</c>。
    /// </summary>
    public async Task<bool> PinFromClipboardAsync()
    {
        if (TopLevelAccessor.GetClipboard() is not { } clipboard)
        {
            return false;
        }

        if (await SvgPinRenderer.ReadClipboardAsync(clipboard) is { } svg)
        {
            Pin(SvgPinRenderer.Render(svg), "clipboard-svg.png", ownsImage: true);
            return true;
        }

        var bitmap = await clipboard.TryGetBitmapAsync();
        if (bitmap is null)
        {
            var text = await clipboard.TryGetTextAsync();

            if (!string.IsNullOrWhiteSpace(text))
            {
                // 设置里打开了「解析图片地址」且这段文本确实是一个图片 URL：下载下来直接贴图。
                // 下载失败（网络 / 不是图片 / 被安全规则拦）就继续按 Markdown 渲染，
                // 用户至少还能看到那段文字，而不是"点了没反应"。
                if (AppSettingsService.Current.Settings.PinRemoteImages
                    && RemoteImageFetcher.LooksLikeImageUrl(text, out var uri)
                    && await RemoteImageFetcher.TryFetchAsync(uri!) is { } remote)
                {
                    Pin(ResultActions.ToBitmap(remote), "remote.png", ownsImage: true);
                    return true;
                }

                Pin(await MarkdownPinRenderer.RenderAsync(text), "clipboard-markdown.png", ownsImage: true);
                return true;
            }

            // 兜底：剪贴板里"有图但读不回来"（例如只剩文件项、或平台格式不匹配）时，
            // 用最近一次截图的内存帧 —— 否则表现就是"F1 截了图，F3 贴不出来"。
            if (CaptureMemory.FrameIfCurrent() is { } frame)
            {
                GuiLog.Write("pin: clipboard bitmap unreadable, falling back to last capture frame");
                Pin(
                    ResultActions.ToBitmap(frame),
                    "capture.png",
                    ownsImage: true,
                    targetRegion: CaptureMemory.RegionIfCurrent());
                return true;
            }

            return false;
        }

        // 这张位图是从剪贴板读出来的、归调用方所有：让贴图窗口在关闭时释放它。
        // 位置：剪贴板里若还是"刚截的那张图"就贴回截图原位，否则（外部图片等）屏幕中央。
        Pin(bitmap, "clipboard.png", ownsImage: true, targetRegion: CaptureMemory.RegionIfCurrent());
        return true;
    }

    /// <summary>最后贴上的那一张（列表末尾）；没有贴图时返回 <c>null</c>。</summary>
    public PinWindow? Last => _windows.Count > 0 ? _windows[^1] : null;

    /// <summary>
    /// 光标下最上层的贴图；光标不在任何贴图上时退回"最后贴的那张"。
    /// 热键（置顶开关这类"作用于某一张"的动作）用它挑目标 —— 比"必须先点一下贴图"顺手。
    /// </summary>
    public PinWindow? ResolveForHotkey(int screenX, int screenY) => FindAt(screenX, screenY) ?? Last;

    /// <summary>屏幕坐标下最上层的贴图（后贴的在上层；穿透的贴图点不到，默认跳过）。</summary>
    /// <param name="includeClickThrough">
    /// 为 <c>true</c> 时把"鼠标穿透"的贴图也算进来 —— 用热键关掉穿透贴图时必须这样，
    /// 否则它会因为这行判断而永远找不到。
    /// </param>
    public PinWindow? FindAt(int screenX, int screenY, bool includeClickThrough = false)
    {
        for (var i = _windows.Count - 1; i >= 0; i--)
        {
            var window = _windows[i];

            if (window.IsClickThrough && !includeClickThrough)
            {
                continue;
            }

            var rect = window.GetScreenRect();
            if (screenX >= rect.X && screenX < rect.X + rect.Width
                && screenY >= rect.Y && screenY < rect.Y + rect.Height)
            {
                return window;
            }
        }

        return null;
    }

    /// <summary>关掉所有贴图。</summary>
    public void CloseAll()
    {
        foreach (var window in _windows.ToArray())
        {
            window.Close();
        }
    }

    /// <summary>
    /// 把所有贴图的穿透收回来。开了穿透的贴图点不到自己，必须有这么一条"从外面救回来"的通道。
    /// 返回被恢复的张数。
    /// </summary>
    public int RestoreInteraction()
    {
        var restored = 0;

        foreach (var window in _windows.ToArray())
        {
            if (window.IsClickThrough && window.SetClickThrough(false))
            {
                restored++;
            }
        }

        return restored;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
