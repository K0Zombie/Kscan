using Avalonia;
using Kscan.Core.Imaging;
using Kscan.Platform;

namespace Kscan.App.Services;

/// <summary>
/// 记住"最近一次截图"落在屏幕上的哪块区域，外加当时剪贴板的版本号。
/// <para>
/// 贴图位置靠它：剪贴板里若还是那张截图，就把图贴回原来在屏幕上的位置（大小也一样）；
/// 一旦剪贴板被别人改过（序号变了）或平台读不到序号，就退回"屏幕中央" ——
/// 免得把一张来路不明的图贴到上次截图的地方。
/// </para>
/// <para>
/// 另外还留着**最近一次截图的内存帧**：剪贴板里明明有图却读不回来时（只剩文件项、
/// 或平台格式不匹配），F3 直接用它，避免"F1 截了图、F3 却贴不出来"。
/// </para>
/// </summary>
internal static class CaptureMemory
{
    private static PixelRect? _region;

    private static long? _sequence;

    private static ImageFrame? _frame;

    /// <summary>记下这次截图的屏幕区域与"刚写进剪贴板之后"的剪贴板序号。</summary>
    public static void Remember(PixelRect region, long? clipboardSequence)
    {
        _region = region;
        _sequence = clipboardSequence;
    }

    /// <summary>记下这次截图的像素帧（剪贴板读不回来时的兜底来源）。</summary>
    public static void RememberFrame(ImageFrame frame) => _frame = frame;

    /// <summary>剪贴板里若仍是那次截图，返回它的屏幕区域；否则返回 <c>null</c>（调用方自己居中）。</summary>
    public static PixelRect? RegionIfCurrent()
    {
        if (_region is not { } region || _sequence is not { } recorded)
        {
            return null;
        }

        return PlatformServices.ClipboardWatcher.SequenceNumber == recorded ? region : null;
    }

    /// <summary>
    /// 剪贴板里若仍是那次截图，返回它的像素帧；否则 <c>null</c>。
    /// </summary>
    public static ImageFrame? FrameIfCurrent()
    {
        if (_frame is null)
        {
            return null;
        }

        // 序号读不到（平台不支持）时也允许兜底：此时 RegionIfCurrent() 会返回 null，位置退化为居中
        return _sequence is { } recorded && PlatformServices.ClipboardWatcher.SequenceNumber != recorded
            ? null
            : _frame;
    }
}
