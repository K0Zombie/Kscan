using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kscan.App.Services;
using Kscan.Core.Configuration;
using Kscan.Platform;
using Kscan.Platform.Abstractions;

namespace Kscan.App.ViewModels;

/// <summary>主窗口：三个页签 + 贴图管理 + 底部平台信息。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        // 底栏摘要是算好的字符串，换语言要手动通知刷新
        I18n.Current.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(PlatformSummary));
            RefreshPins();
        };

        // 底栏显隐由配置决定（设置页「界面」里可关掉）
        AppSettingsService.Current.Changed += (_, _) => OnPropertyChanged(nameof(ShowPlatformSummary));

        // 贴图数量变了，底栏的贴图条要跟着变
        PinManager.Current.Changed += (_, _) => RefreshPins();

        // 识别出结果时切回「识别」页（只在真的识别完时 —— 单纯按截图键不跳页，用户要求）
        Scan.RecognitionPresented += (_, _) => ActiveTabIndex = 0;

        // 全局热键：每个动作都走与按钮/菜单同一条代码路径
        GlobalHotkeyService.Current.Triggered += (_, e) => RunHotkeyAction(e.ActionId);
        GlobalHotkeyService.Current.CommandTriggered += binding => _ = RunCommandAsync(binding);
        GlobalHotkeyService.Current.ApplyFromSettings();
    }

    public ScanViewModel Scan { get; } = new();

    public GenerateViewModel Generate { get; } = new();

    public SettingsViewModel Settings { get; } = new();

    /// <summary>当前页签（0 = 识别，1 = 生成，2 = 设置）。热键触发的截图/识别会自动切回识别页。</summary>
    [ObservableProperty]
    private int _activeTabIndex;

    /// <summary>当前平台能做什么、不能做什么，直接写在界面底部，省得用户猜。</summary>
    public string PlatformSummary => PlatformText.Summary;

    /// <summary>是否显示底部平台信息（配置项，默认显示）。</summary>
    public bool ShowPlatformSummary => AppSettingsService.Current.Settings.ShowPlatformSummary;

    /// <summary>屏幕上贴了几张图。</summary>
    public int PinCount => PinManager.Current.Count;

    public bool HasPins => PinCount > 0;

    public string PinSummary => I18n.F("Str.Pin.CountLabel", PinCount);

    /// <summary>操作反馈（贴图 / 截图开关等）：主窗口底部那行状态。</summary>
    [ObservableProperty]
    private string _pinStatus = string.Empty;

    /// <summary>关掉屏幕上所有贴图。</summary>
    [RelayCommand]
    private void CloseAllPins() => PinManager.Current.CloseAll();

    /// <summary>
    /// 把贴图的鼠标穿透收回来：开了穿透的贴图自己收不到点击，
    /// 没有这条"从外面"的通道，用户就只能重启程序。
    /// </summary>
    [RelayCommand]
    private void RestorePins()
    {
        var restored = PinManager.Current.RestoreInteraction();

        PinStatus = restored > 0
            ? I18n.F("Str.Pin.Status.Restored", restored)
            : I18n.T("Str.Pin.Status.NoneRestored");
    }

    /// <summary>
    /// 热键「切换跨屏框选」：直接把设置翻面（写盘由 <see cref="AppSettingsService"/> 负责），
    /// 并在状态条上说明现在是"跨屏"还是"单屏"—— 不然按一下什么反馈都没有。
    /// </summary>
    private void ToggleCaptureAcrossScreens()
    {
        var enabled = !AppSettingsService.Current.Settings.CaptureAcrossScreens;

        _ = AppSettingsService.Current.UpdateAsync(settings => settings with { CaptureAcrossScreens = enabled });

        PinStatus = I18n.T(enabled
            ? "Str.Capture.Status.AcrossScreensOn"
            : "Str.Capture.Status.AcrossScreensOff");
    }

    /// <summary>某个动作的全局热键被按下（已在 UI 线程）。</summary>
    private void RunHotkeyAction(string actionId)
    {
        switch (actionId)
        {
            case HotkeyActionIds.CaptureRegion:
                Scan.CaptureRegionCommand.Execute(null);
                break;

            case HotkeyActionIds.CaptureRegionToClipboard:
                Scan.CaptureRegionToClipboardCommand.Execute(null);
                break;

            case HotkeyActionIds.CaptureRegionAndPin:
                Scan.CaptureRegionAndPinCommand.Execute(null);
                break;

            case HotkeyActionIds.CaptureRegionWithAnnotation:
                Scan.CaptureRegionWithAnnotationCommand.Execute(null);
                break;

            case HotkeyActionIds.AnnotateClipboard:
                Scan.AnnotateClipboardCommand.Execute(null);
                break;

            case HotkeyActionIds.AnnotateScreen:
                Scan.AnnotateScreenCommand.Execute(null);
                break;

            case HotkeyActionIds.ScrollCapture:
                Scan.ScrollCaptureCommand.Execute(null);
                break;

            case HotkeyActionIds.ToggleCaptureAcrossScreens:
                ToggleCaptureAcrossScreens();
                break;

            case HotkeyActionIds.ScanClipboard:
                Scan.FromClipboardCommand.Execute(null);
                break;

            case HotkeyActionIds.ClipboardToCode:
                Scan.ClipboardToCodeCommand.Execute(null);
                break;

            case HotkeyActionIds.SaveClipboardToFolder:
                _ = SaveClipboardToFolderAsync();
                break;

            case HotkeyActionIds.UndoLastAction:
                UndoLastAction();
                break;

            case HotkeyActionIds.PinClipboard:
                _ = PinFromClipboardAsync();
                break;

            case HotkeyActionIds.CloseAllPins:
            {
                var pinned = PinManager.Current.Count;
                CloseAllPins();
                PinStatus = pinned > 0
                    ? I18n.F("Str.Pin.Status.ClosedAll", pinned)
                    : I18n.T("Str.Pin.Status.NoPins");
                break;
            }

            case HotkeyActionIds.Activate:
                BringToFront();
                break;

            case HotkeyActionIds.PickExcludedApp:
                _ = PickExcludedAppAsync();
                break;

            case HotkeyActionIds.TogglePinTopmost:
                TogglePinTopmost();
                break;

            case HotkeyActionIds.OpenPinManager:
                Views.PinManagerWindow.ShowOrActivate();
                break;

            case HotkeyActionIds.ClosePinUnderCursor:
                ClosePinUnderCursor();
                break;
        }
    }

    /// <summary>
    /// 热键「关闭光标下的贴图」：关掉鼠标指着的这一张。
    /// <para>
    /// 这是关掉<b>鼠标穿透</b>贴图的主要方式：穿透窗口收不到鼠标，也就永远拿不到键盘焦点，
    /// 所以 ESC 对它们无效；本热键走的是全局注册，不依赖任何窗口的焦点。
    /// </para>
    /// </summary>
    private void ClosePinUnderCursor()
    {
        if (PlatformServices.ForegroundApp.GetCursorPosition() is not { } point)
        {
            PinStatus = I18n.T("Str.Pin.Status.NoPinUnderCursor");
            return;
        }

        // 必须把穿透的也算进来，否则正好关不掉它们
        var pin = PinManager.Current.FindAt(point.X, point.Y, includeClickThrough: true);

        if (pin is null)
        {
            PinStatus = I18n.T("Str.Pin.Status.NoPinUnderCursor");
            return;
        }

        // Alt+Q = 软关闭（和 ESC 一样：只收起窗口，图还留在分组库里）
        PinManager.Current.CloseSoft(pin);
        PinStatus = I18n.T("Str.Pin.Status.ClosedUnderCursor");
    }

    /// <summary>
    /// 热键「贴图置顶开关」：切换光标下那张贴图的置顶状态
    /// （光标不在任何贴图上时取最后贴的那张）。取消置顶后它不再压住别的窗口。
    /// </summary>
    private void TogglePinTopmost()
    {
        var pin = PlatformServices.ForegroundApp.GetCursorPosition() is { } point
            ? PinManager.Current.ResolveForHotkey(point.X, point.Y)
            : PinManager.Current.Last;

        if (pin is null)
        {
            PinStatus = I18n.T("Str.Pin.Status.NoPins");
            return;
        }

        var enabled = !pin.IsTopmost;
        pin.SetTopmost(enabled);
        PinStatus = I18n.T(enabled ? "Str.Pin.Status.TopmostOn" : "Str.Pin.Status.TopmostOff");
    }

    /// <summary>
    /// 热键「保存剪贴板内容到文件夹」：把剪贴板里最近的一项（图片 → PNG / 文本 → TXT）
    /// 存进设置里指定的文件夹。没设置文件夹、剪贴板为空、写文件失败都在底栏给一句说明。
    /// </summary>
    private async Task SaveClipboardToFolderAsync()
    {
        var folder = AppSettingsService.Current.Settings.ClipboardSaveFolder;

        if (string.IsNullOrWhiteSpace(folder))
        {
            PinStatus = I18n.T("Str.ClipSave.NoFolder");
            return;
        }

        if (TopLevelAccessor.GetClipboard() is not { } clipboard)
        {
            PinStatus = I18n.T("Str.ClipSave.NoClipboard");
            return;
        }

        try
        {
            var path = await ClipboardSaver.SaveLatestAsync(folder.Trim(), clipboard);

            if (path is not null)
            {
                // 记下来供「撤销上一次操作」使用：只记本次运行的最后一次，不写配置
                _lastSavedFile = path;
            }

            PinStatus = path is null
                ? I18n.T("Str.ClipSave.Nothing")
                : I18n.F("Str.ClipSave.Saved", path);
        }
        catch (Exception ex)
        {
            PinStatus = I18n.F("Str.ClipSave.Failed", ex.Message);
        }
    }

    /// <summary>最近一次「保存剪贴板内容到文件夹」写出的文件；「撤销上一次操作」删它。</summary>
    private string? _lastSavedFile;

    /// <summary>
    /// 热键「撤销上一次操作」：目前撤销的是「保存剪贴板内容到文件夹」刚写出的那个文件
    /// （误触保存时不必再开资源管理器手动删）。撤销一次后记录清空，再按只会提示"没有可撤销的"。
    /// </summary>
    private void UndoLastAction()
    {
        if (string.IsNullOrWhiteSpace(_lastSavedFile))
        {
            PinStatus = I18n.T("Str.Undo.None");
            return;
        }

        var path = _lastSavedFile;
        _lastSavedFile = null;

        try
        {
            if (!File.Exists(path))
            {
                PinStatus = I18n.F("Str.Undo.Missing", path);
                return;
            }

            File.Delete(path);
            PinStatus = I18n.F("Str.Undo.Done", path);
        }
        catch (Exception ex)
        {
            PinStatus = I18n.F("Str.Undo.Failed", ex.Message);
        }
    }

    /// <summary>
    /// 热键「框选排除应用」：弹一层全屏遮罩，用户点选一个窗口，
    /// 把该窗口所属的程序加进热键排除列表（已在列表里就只提示一下）。
    /// </summary>
    private async Task PickExcludedAppAsync()
    {
        ForegroundAppInfo? info;

        try
        {
            info = await ExcludedAppPicker.PickAsync();
        }
        catch (Exception ex)
        {
            PinStatus = I18n.F("Str.Settings.ExcludedPickFailed", ex.Message);
            return;
        }

        if (info is null)
        {
            PinStatus = I18n.T("Str.Settings.ExcludedPickCancelled");
            return;
        }

        var alreadyExcluded = ExcludedAppMatcher.IsExcluded(
            AppSettingsService.Current.Settings.IgnoredApps,
            info.ProcessName,
            info.ExecutablePath);

        if (!alreadyExcluded)
        {
            // 有完整路径就存路径（换目录后仍然精确）；拿不到路径时退回进程名
            var entry = string.IsNullOrWhiteSpace(info.ExecutablePath) ? info.ProcessName : info.ExecutablePath;

            await AppSettingsService.Current.UpdateAsync(settings =>
            {
                var list = settings.IgnoredApps.ToList();
                list.Add(entry);
                return settings with { IgnoredApps = new AppList(list) };
            });
        }

        PinStatus = I18n.F(
            alreadyExcluded ? "Str.Settings.ExcludedAlready" : "Str.Settings.ExcludedAdded",
            info.ProcessName);
    }

    /// <summary>
    /// 热键「把剪贴板图片 / Markdown 文本贴到屏幕」。失败时把主窗口叫到前台。
    /// 不叫出来用户看到的就是"按了没反应"。
    /// </summary>
    private async Task PinFromClipboardAsync()
    {
        bool pinned;

        try
        {
            pinned = await PinManager.Current.PinFromClipboardAsync();
        }
        catch (Exception ex)
        {
            // 读剪贴板本身可能失败（被别的进程锁住等）。以前这里没有 catch，
            // 异常被 fire-and-forget 吞掉 —— 那正是"按了没反应"的另一个来源。
            PinStatus = I18n.F("Str.Pin.Status.PinFailed", ex.Message);
            BringToFront();
            return;
        }

        PinStatus = pinned
            ? I18n.T("Str.Pin.Status.Pinned")
            : I18n.T("Str.Pin.Status.NothingInClipboard");

        if (!pinned)
        {
            BringToFront();
        }
    }

    private async Task RunCommandAsync(HotkeyBinding binding)
    {
        try
        {
            await Task.Run(() => Kscan.Platform.CommandLauncher.Start(binding));
            PinStatus = I18n.F("Str.Hotkey.CommandStarted", binding.Command);
        }
        catch (Exception ex)
        {
            PinStatus = I18n.F("Str.Hotkey.CommandFailed", binding.Command, ex.Message);
            BringToFront();
        }
    }

    /// <summary>把主窗口叫到前台（全局热键的「唤起主界面」动作）。</summary>
    private static void BringToFront()
    {
        if (Avalonia.Application.Current is App application)
        {
            application.ShowMainWindow();
            return;
        }
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } window)
        {
            return;
        }

        window.Show();

        if (window.WindowState == Avalonia.Controls.WindowState.Minimized)
        {
            window.WindowState = Avalonia.Controls.WindowState.Normal;
        }

        window.Activate();
    }

    private void RefreshPins()
    {
        OnPropertyChanged(nameof(PinCount));
        OnPropertyChanged(nameof(HasPins));
        OnPropertyChanged(nameof(PinSummary));
    }
}
