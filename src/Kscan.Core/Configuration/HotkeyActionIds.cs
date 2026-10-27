namespace Kscan.Core.Configuration;

/// <summary>
/// 可绑全局热键的动作 id。
/// <para>
/// 这些字符串同时是<b>配置文件里的键名</b>和平台层的动作标识 —— 改名会废掉用户已有配置，
/// 所以只在这里定义一次，Platform 与 GUI 都引用它，不各自抄一份。
/// </para>
/// </summary>
public static class HotkeyActionIds
{
    /// <summary>框选截图并识别，单结果强制复制识别内容。</summary>
    public const string CaptureRegion = "captureRegion";

    /// <summary>框选截图 → 只把图片放进剪贴板，<b>不识别</b>（"框完即复制"）。</summary>
    public const string CaptureRegionToClipboard = "captureRegionToClipboard";

    /// <summary>读出剪贴板里的图片并识别。</summary>
    public const string ScanClipboard = "scanClipboard";

    /// <summary>把剪贴板里的<b>文本</b>转成码（码制看设置里的"快捷转码"），生成的图片进剪贴板。</summary>
    public const string ClipboardToCode = "clipboardToCode";

    /// <summary>把剪贴板里的<b>最近一项</b>（图片或文本）保存成文件，放进设置里指定的文件夹。</summary>
    public const string SaveClipboardToFolder = "saveClipboardToFolder";

    /// <summary>
    /// 撤销上一次操作：目前撤销「保存剪贴板内容到文件夹」刚写出的那个文件
    /// （误触保存时不必再开资源管理器手动删）。
    /// </summary>
    public const string UndoLastAction = "undoLastAction";

    /// <summary>把剪贴板里的图片或 Markdown 文本贴到屏幕上。</summary>
    public const string PinClipboard = "pinClipboard";

    /// <summary>关掉屏幕上所有贴图（默认 Ctrl+Q）。</summary>
    public const string CloseAllPins = "closeAllPins";

    /// <summary>切换贴图的"始终置顶"（默认 Ctrl+Shift+F3，作用于光标下那张）。</summary>
    public const string TogglePinTopmost = "togglePinTopmost";

    /// <summary>打开贴图分组管理窗口（默认 Ctrl+Shift+F4）。</summary>
    public const string OpenPinManager = "openPinManager";

    /// <summary>
    /// 关掉光标下那张贴图（默认 Alt+Q）。
    /// 鼠标穿透的贴图点不到、也就拿不到键盘焦点，ESC 对它无效 —— 这条热键是关掉它们的顺手入口。
    /// </summary>
    public const string ClosePinUnderCursor = "closePinUnderCursor";

    /// <summary>框选截图后<b>直接贴到屏幕上</b>（不识别、不经过剪贴板；默认 Ctrl+F2）。</summary>
    public const string CaptureRegionAndPin = "captureRegionAndPin";

    /// <summary>
    /// 框选截图后先<b>进标注</b>，标完直接复制到剪贴板（默认 Ctrl+Shift+F1）。
    /// 和"截取并贴图"是两条独立的路：那条不标注、这条不贴图。
    /// </summary>
    public const string CaptureRegionWithAnnotation = "captureRegionWithAnnotation";
    public const string ScrollCapture = "scrollCapture";

    /// <summary>
    /// 直接标注<b>剪贴板里的图</b>（默认 Ctrl+Shift+E）：标注完覆盖剪贴板。
    /// 好处是"截图"和"标注"解耦 —— 先随便截，想标注时再按这个键。
    /// </summary>
    public const string AnnotateClipboard = "annotateClipboard";

    /// <summary>
    /// 屏幕批注层（默认 Ctrl+Alt+D）：抓当前屏 → 全屏标注 → Enter 复制 / Esc 丢弃。
    /// </summary>
    public const string AnnotateScreen = "annotateScreen";

    /// <summary>
    /// 切换「跨屏框选」开关（默认 Ctrl+Alt+S）。截图时想临时切成单屏/跨屏，不用去设置页翻。
    /// </summary>
    public const string ToggleCaptureAcrossScreens = "toggleCaptureAcrossScreens";

    /// <summary>唤起主窗口。</summary>
    public const string Activate = "activate";

    /// <summary>在桌面上直接点选一个窗口，把它的程序加入热键排除列表。</summary>
    public const string PickExcludedApp = "pickExcludedApp";

    public const string RunCommand = "runCommand";
    public const string RecordVideo = "recordVideo";
    public const string RecordGif = "recordGif";
    public const string RecordAudio = "recordAudio";
    public const string RecordingPanel = "recordingPanel";
    public const string StopRecordings = "stopRecordings";

    /// <summary>全部可绑动作（也是界面展示顺序）。</summary>
    public static IReadOnlyList<string> All { get; } =
        [CaptureRegion, CaptureRegionToClipboard, CaptureRegionAndPin, CaptureRegionWithAnnotation, ScrollCapture, RecordVideo, RecordGif, RecordAudio, RecordingPanel, StopRecordings, AnnotateClipboard, AnnotateScreen, ToggleCaptureAcrossScreens, ScanClipboard, ClipboardToCode, SaveClipboardToFolder, UndoLastAction, PinClipboard, CloseAllPins, TogglePinTopmost, ClosePinUnderCursor, OpenPinManager, Activate, PickExcludedApp, RunCommand];
}
