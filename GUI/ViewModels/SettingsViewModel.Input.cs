using CommunityToolkit.Mvvm.Input;
using Kscan.App.Services;

namespace Kscan.App.ViewModels;

public sealed partial class SettingsViewModel
{
    public IReadOnlyList<string> WindowMaterials => [I18n.T("Str.Settings.MaterialSolid"), "Mica", "Acrylic"];
    public int WindowMaterialIndex
    {
        get => WindowMaterial.Selected(AppSettingsService.Current.Settings) switch { "Mica" => 1, "Acrylic" => 2, _ => 0 };
        set
        {
            if (value is < 0 or > 2 || value == WindowMaterialIndex) return;
            var material = value == 1 ? "Mica" : value == 2 ? "Acrylic" : "None";
            _ = AppSettingsService.Current.UpdateAsync(s => s with { WindowMaterial = material, MicaEnabled = value == 1 });
            OnPropertyChanged();
        }
    }
    public bool MouseGestureEnabled
    {
        get => AppSettingsService.Current.Settings.MouseGestureEnabled;
        set { _ = AppSettingsService.Current.UpdateAsync(s => s with { MouseGestureEnabled = value }); OnPropertyChanged(); }
    }
    public string MouseGestureModifiers
    {
        get => AppSettingsService.Current.Settings.MouseGestureModifiers;
        set { _ = AppSettingsService.Current.UpdateAsync(s => s with { MouseGestureModifiers = value }); OnPropertyChanged(); }
    }
    public IReadOnlyList<string> MouseGestureButtons { get; } = ["Left", "Middle", "Right"];
    public string MouseGestureButton
    {
        get => AppSettingsService.Current.Settings.MouseGestureButton;
        set { _ = AppSettingsService.Current.UpdateAsync(s => s with { MouseGestureButton = value }); OnPropertyChanged(); }
    }

    [RelayCommand]
    private async Task RecordMouseGestureAsync()
    {
        if (await Views.HotkeyCaptureWindow.CaptureAsync(MouseGestureModifiers, modifierOnly: true) is { } keys)
            MouseGestureModifiers = keys;
    }

    [RelayCommand]
    private async Task RecordCommandHotkeyAsync()
    {
        if (await Views.HotkeyCaptureWindow.CaptureAsync(CommandHotkey,
            describeConflict: keys => HotkeyConflict.Describe(keys, null)) is { } keys)
            CommandHotkey = keys;
    }

    [RelayCommand]
    private async Task RecordHeldModifierAsync(string target)
    {
        if (await Views.HotkeyCaptureWindow.CaptureAsync(null, modifierOnly: true) is not { } keys) return;
        if (keys is not ("Ctrl" or "Alt" or "Shift"))
        {
            SetStatus("Str.Settings.SingleModifierOnly");
            return;
        }
        switch (target)
        {
            case "element": ElementIgnoreModifier = keys; break;
            case "cursor": CaptureCursorKey = keys; break;
            case "freeze": AnimatedFreezeKey = AnimatedFreezeKeyOptions[keys == "Ctrl" ? 1 : keys == "Alt" ? 2 : 0]; break;
        }
    }
}
