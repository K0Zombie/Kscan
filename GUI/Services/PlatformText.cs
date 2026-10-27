using Kscan.Platform;

namespace Kscan.App.Services;

/// <summary>
/// 平台能力描述的中英切换。
/// 平台层只给出 i18n key（<c>IScreenCapture.DescriptionKey</c> 之类），由这里加上资源前缀并翻译；
/// 没有给 key 的实现退回用它自己的 <c>Description</c>（即：未本地化，但至少有内容）。
/// </summary>
internal static class PlatformText
{
    /// <summary>底部一行摘要：系统 / 会话 · 截图 · 通知 · 自启。</summary>
    public static string Summary
    {
        get
        {
            var capabilities = ResultActions.Capabilities;
            return I18n.F(
                "Str.Platform.Summary",
                capabilities.OperatingSystem,
                capabilities.SessionType,
                Capture,
                Notification,
                AutoStart);
        }
    }

    public static string Capture => Describe(
        PlatformServices.ScreenCapture.DescriptionKey,
        PlatformServices.ScreenCapture.DescriptionArguments,
        PlatformServices.ScreenCapture.Description);

    public static string Notification => Describe(
        PlatformServices.Notification.DescriptionKey,
        PlatformServices.Notification.Description);

    public static string AutoStart => Describe(
        PlatformServices.AutoStart.DescriptionKey,
        PlatformServices.AutoStart.Description);

    public static string Clipboard => Describe(
        PlatformServices.ClipboardWatcher.DescriptionKey,
        PlatformServices.ClipboardWatcher.Description);

    public static string ClickThrough => Describe(
        PlatformServices.ClickThrough.DescriptionKey,
        PlatformServices.ClickThrough.Description);

    public static string Hotkey => Describe(
        PlatformServices.GlobalHotkey.DescriptionKey,
        PlatformServices.GlobalHotkey.Description);

    private static string Describe(string? key, object?[] arguments, string fallback) =>
        key is null ? fallback : I18n.F("Str." + key, arguments);

    private static string Describe(string? key, string fallback) =>
        key is null ? fallback : I18n.F("Str." + key);
}
