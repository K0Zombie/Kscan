using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kscan.App.Services;
using Kscan.Core.Configuration;
using Kscan.Platform.Abstractions;

namespace Kscan.App.ViewModels;

/// <summary>
/// 「全局热键」区块里动作下拉的一项：<see cref="Key"/> 参与逻辑（就是动作 id），
/// <see cref="DisplayName"/> 只是给用户看的（随语言切换刷新）。
/// </summary>
public sealed partial class HotkeyActionOption : ObservableObject
{
    public HotkeyActionOption(string key) => Key = key;

    /// <summary>动作 id（见 <see cref="HotkeyActionIds"/>）。</summary>
    public string Key { get; }

    /// <summary>动作名（i18n 里取，会被换语言刷新）。</summary>
    public string DisplayName => I18n.T(LabelKeyFor(Key));

    public override string ToString() => DisplayName;

    internal static string LabelKeyFor(string actionId) => actionId switch
    {
        HotkeyActionIds.CaptureRegion => "Str.Hotkey.Action.CaptureRegion",
        HotkeyActionIds.CaptureRegionToClipboard => "Str.Hotkey.Action.CaptureRegionToClipboard",
        HotkeyActionIds.CaptureRegionAndPin => "Str.Hotkey.Action.CaptureRegionAndPin",
        HotkeyActionIds.CaptureRegionWithAnnotation => "Str.Hotkey.Action.CaptureRegionWithAnnotation",
        HotkeyActionIds.ScrollCapture => "Str.Hotkey.Action.ScrollCapture",
        HotkeyActionIds.RecordVideo => "Str.Hotkey.Action.RecordVideo",
        HotkeyActionIds.RecordGif => "Str.Hotkey.Action.RecordGif",
        HotkeyActionIds.RecordAudio => "Str.Hotkey.Action.RecordAudio",
        HotkeyActionIds.RecordingPanel => "Str.Hotkey.Action.RecordingPanel",
        HotkeyActionIds.StopRecordings => "Str.Hotkey.Action.StopRecordings",
        HotkeyActionIds.AnnotateClipboard => "Str.Hotkey.Action.AnnotateClipboard",
        HotkeyActionIds.AnnotateScreen => "Str.Hotkey.Action.AnnotateScreen",
        HotkeyActionIds.ToggleCaptureAcrossScreens => "Str.Hotkey.Action.ToggleCaptureAcrossScreens",
        HotkeyActionIds.ScanClipboard => "Str.Hotkey.Action.ScanClipboard",
        HotkeyActionIds.ClipboardToCode => "Str.Hotkey.Action.ClipboardToCode",
        HotkeyActionIds.SaveClipboardToFolder => "Str.Hotkey.Action.SaveClipboardToFolder",
        HotkeyActionIds.UndoLastAction => "Str.Hotkey.Action.UndoLastAction",
        HotkeyActionIds.PinClipboard => "Str.Hotkey.Action.PinClipboard",
        HotkeyActionIds.CloseAllPins => "Str.Hotkey.Action.CloseAllPins",
        HotkeyActionIds.TogglePinTopmost => "Str.Hotkey.Action.TogglePinTopmost",
        HotkeyActionIds.OpenPinManager => "Str.Hotkey.Action.OpenPinManager",
        HotkeyActionIds.ClosePinUnderCursor => "Str.Hotkey.Action.ClosePinUnderCursor",
        HotkeyActionIds.Activate => "Str.Hotkey.Action.Activate",
        HotkeyActionIds.PickExcludedApp => "Str.Hotkey.Action.PickExcludedApp",
        HotkeyActionIds.RunCommand => "Str.Hotkey.Action.RunCommand",
        _ => "Str.Hotkey.Action.Unknown",
    };

    internal void RefreshLocalizedText() => OnPropertyChanged(nameof(DisplayName));
}

/// <summary>设置页「全局热键」里的一行：选一个动作 + 填一个键（同一个动作可以出现在多行）。</summary>
public sealed partial class HotkeyRowViewModel : ObservableObject
{
    public HotkeyRowViewModel(
        HotkeyActionOption action,
        string text,
        IReadOnlyList<HotkeyActionOption> actions,
        Action<HotkeyRowViewModel> remove)
    {
        Actions = actions;
        _selectedAction = action;
        _text = text;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    /// <summary>动作下拉的候选（所有行共用同一份，换语言时一起刷新）。</summary>
    public IReadOnlyList<HotkeyActionOption> Actions { get; }

    /// <summary>本行选的动作。</summary>
    [ObservableProperty]
    private HotkeyActionOption _selectedAction;

    /// <summary>输入框内容（用户的写法，未归一化）。</summary>
    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty] private string _command = string.Empty;
    [ObservableProperty] private string _arguments = string.Empty;
    [ObservableProperty] private string _workingDirectory = string.Empty;

    public bool IsCommand => SelectedAction.Key == HotkeyActionIds.RunCommand;

    partial void OnSelectedActionChanged(HotkeyActionOption value) => OnPropertyChanged(nameof(IsCommand));

    /// <summary>删掉这一行（删完还要按「应用」才写进配置）。</summary>
    public IRelayCommand RemoveCommand { get; }

    /// <summary>
    /// 录制键：弹一个窗口让用户<b>直接按</b>组合键，松开后要求确认才写入。
    /// 手输仍然保留（输入框照旧可编辑），录制只是把"手误"这条路堵住。
    /// </summary>
    [RelayCommand]
    private async Task CaptureAsync()
    {
        if (await Views.HotkeyCaptureWindow.CaptureAsync(
                Text,
                describeConflict: captured => HotkeyConflict.Describe(captured, SelectedAction.Key)) is { } value)
        {
            Text = value;
        }
    }

    /// <summary>本行的动作 id（写配置用）。</summary>
    public string ActionId => SelectedAction.Key;

    /// <summary>动作名（失败提示里要用）。</summary>
    public string Label => SelectedAction.DisplayName;

    /// <summary>
    /// 没修饰键、主键又是字母/数字 —— 这种全局热键会把那个键从<b>所有程序</b>手里抢走（打字就没了）。
    /// 界面据此给提醒，但不拦着不让用。
    /// </summary>
    public bool IsRisky =>
        HotkeyDefinition.TryParse(Text, out var definition, out _) && definition!.IsRiskyGlobally;

    internal void RefreshLocalizedText()
    {
        SelectedAction.RefreshLocalizedText();
        OnPropertyChanged(nameof(Label));
    }

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(IsRisky));
}

/// <summary>
/// 设置页的「全局热键」区块：一行 = 一条「动作 + 键」绑定，可增可删，同一个动作可以绑多个键。
/// 填好按下「应用」一起写进配置；真正的注册由 <see cref="GlobalHotkeyService"/> 订阅配置变化完成，
/// 这里只负责界面与结果反馈。
/// </summary>
public sealed partial class HotkeySettingsViewModel : ObservableObject
{
    private string? _statusKey;

    private object?[] _statusArgs = [];

    public HotkeySettingsViewModel()
    {
        Actions = [.. HotkeyActionIds.All.Select(actionId => new HotkeyActionOption(actionId))];

        SyncFromSettings();

        AppSettingsService.Current.Changed += (_, _) => SyncFromSettings();
        I18n.Current.Changed += (_, _) => RefreshLocalizedText();
        GlobalHotkeyService.Current.RegistrationChanged += (_, _) => OnPropertyChanged(nameof(ActiveText));
    }

    /// <summary>动作下拉的候选，含自定义命令。</summary>
    public IReadOnlyList<HotkeyActionOption> Actions { get; }

    /// <summary>当前配置里的绑定，一条一行（配置是唯一来源）。</summary>
    public ObservableCollection<HotkeyRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>当前平台是否支持全局热键（不支持时输入框与按钮都禁用）。</summary>
    public bool IsSupported => GlobalHotkeyService.Current.IsSupported;

    /// <summary>平台能力描述（带中英切换）。</summary>
    public string Description => PlatformText.Hotkey;

    /// <summary>
    /// 状态摘要：全部生效时只说个数；有失败时**逐条列出失败原因**（那才是要看的）。
    /// 用注册结果而不是输入框内容，免得用户被输入框里的字骗过去。
    /// </summary>
    public string ActiveText
    {
        get
        {
            var service = GlobalHotkeyService.Current;

            var lines = new List<string>
            {
                I18n.F("Str.Settings.RegistrationSummary", service.Active.Count, service.RegisteredKeys.Count, service.Failures.Count),
                string.Join(" · ", service.RegisteredKeys),
            };

            foreach (var failure in service.Failures)
            {
                lines.Add(I18n.F("Str.Settings.HotkeyFailedOne", LabelFor(failure.ActionId), failure.Keys, failure.Reason));
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>加一行：默认选第一个动作 —— 用户多半会改，但至少不是空下拉。</summary>
    [RelayCommand]
    private void AddRow() => Rows.Add(NewRow(Actions[0], string.Empty));

    /// <summary>删掉一行（只在界面上删；要生效还得按「应用」）。</summary>
    [RelayCommand]
    private void RemoveRow(HotkeyRowViewModel row) => Rows.Remove(row);

    [RelayCommand]
    private async Task ApplyAsync()
    {
        var bindings = new List<HotkeyBinding>();

        foreach (var row in Rows)
        {
            var text = row.Text?.Trim() ?? string.Empty;

            if (text.Length == 0)
            {
                continue;   // 空行 = 不绑（"清掉某一行再应用"就是这么生效的）
            }

            if (!HotkeyDefinition.TryParse(text, out var definition, out var error))
            {
                // 有一行写错就先不写配置，免得把一个坏值存进去
                SetStatus("Str.Settings.HotkeyInvalid", row.Label, error);
                return;
            }

            if (row.IsCommand && string.IsNullOrWhiteSpace(row.Command))
            {
                SetStatus("Str.Settings.CommandRequired");
                return;
            }
            bindings.Add(new HotkeyBinding(row.ActionId, definition!.ToString())
            {
                Command = string.IsNullOrWhiteSpace(row.Command) ? null : row.Command.Trim(),
                Arguments = string.IsNullOrEmpty(row.Arguments) ? null : row.Arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(row.WorkingDirectory) ? null : row.WorkingDirectory.Trim(),
            });
        }

        await AppSettingsService.Current.UpdateAsync(settings => settings with
        {
            Hotkeys = new HotkeyBindings(bindings),
        });

        // 归一化后的写法回填到输入框（配置是唯一来源）
        SyncFromSettings();

        var failures = GlobalHotkeyService.Current.Failures;

        if (bindings.Count == 0)
        {
            SetStatus("Str.Settings.HotkeyCleared");
        }
        else if (failures.Count == 0)
        {
            SetStatus("Str.Settings.HotkeyApplied", bindings.Count);
        }
        else
        {
            SetStatus("Str.Settings.HotkeyApplyPartial", failures.Count);
        }
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        await AppSettingsService.Current.UpdateAsync(settings => settings with
        {
            Hotkeys = HotkeyBindings.Empty,
        });

        SyncFromSettings();

        SetStatus("Str.Settings.HotkeyCleared");
    }

    private HotkeyRowViewModel NewRow(HotkeyActionOption action, string text) =>
        new(action, text, Actions, RemoveRow);

    /// <summary>按配置重建行（配置是唯一来源：换语言、别处改配置、自己写配置后都走这里）。</summary>
    private void SyncFromSettings()
    {
        Rows.Clear();

        foreach (var binding in AppSettingsService.Current.Settings.Hotkeys)
        {
            var row = NewRow(ActionFor(binding.ActionId), binding.Keys);
            row.Command = binding.Command ?? string.Empty;
            row.Arguments = binding.Arguments ?? string.Empty;
            row.WorkingDirectory = binding.WorkingDirectory ?? string.Empty;
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(ActiveText));
    }

    /// <summary>动作 id → 下拉项；认不出的 id（配置被手工改过 / 旧版本遗留）单独造一个"未命名动作"项，不悄悄改掉配置。</summary>
    private HotkeyActionOption ActionFor(string actionId) =>
        Actions.FirstOrDefault(option => option.Key == actionId) ?? new HotkeyActionOption(actionId);

    private string LabelFor(string actionId) =>
        Actions.FirstOrDefault(option => option.Key == actionId)?.DisplayName
        ?? I18n.T("Str.Hotkey.Action.Unknown");

    private void RefreshLocalizedText()
    {
        if (_statusKey is not null)
        {
            Status = I18n.F(_statusKey, _statusArgs);
        }

        foreach (var option in Actions)
        {
            option.RefreshLocalizedText();
        }

        foreach (var row in Rows)
        {
            row.RefreshLocalizedText();
        }

        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(ActiveText));
    }

    private void SetStatus(string key, params object?[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        Status = I18n.F(key, args);
    }
}
