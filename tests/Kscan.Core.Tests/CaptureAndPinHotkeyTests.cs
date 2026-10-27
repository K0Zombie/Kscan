using Kscan.Core.Configuration;

namespace Kscan.Core.Tests;

/// <summary>
/// 「截取并贴图」热键（默认 <c>Ctrl+F3</c>）：动作 id 必须在列表里，老配置要能自动补上。
/// </summary>
public sealed class CaptureAndPinHotkeyTests
{
    [Fact]
    public void Action_id_is_registered_in_the_known_list()
    {
        Assert.Equal("captureRegionAndPin", HotkeyActionIds.CaptureRegionAndPin);
        Assert.Contains(HotkeyActionIds.CaptureRegionAndPin, HotkeyActionIds.All);
    }

    /// <summary>v5 的老配置（没有这条）升级后会自动补一条 Ctrl+F3。</summary>
    [Fact]
    public void Version_5_settings_get_the_default_binding()
    {
        var old = AppSettings.Default with
        {
            Version = 5,
            Hotkeys = new HotkeyBindings([new HotkeyBinding(HotkeyActionIds.Activate, "Ctrl+Alt+F5")]),
        };

        var migrated = old.Migrate();

        Assert.Equal(AppSettings.CurrentVersion, migrated.Version);
        var binding = Assert.Single(migrated.Hotkeys, item => item.ActionId == HotkeyActionIds.CaptureRegionAndPin);
        Assert.Equal("Ctrl+F3", binding.Keys);
    }

    /// <summary>已经是当前版本的配置不会再被塞一遍（用户可能主动删掉了它）。</summary>
    [Fact]
    public void Current_version_settings_are_left_alone()
    {
        var current = AppSettings.Default with
        {
            Version = AppSettings.CurrentVersion,
            Hotkeys = new HotkeyBindings([new HotkeyBinding(HotkeyActionIds.Activate, "Ctrl+Alt+F5")]),
        };

        var migrated = current.Migrate();

        Assert.DoesNotContain(migrated.Hotkeys, item => item.ActionId == HotkeyActionIds.CaptureRegionAndPin);
    }

    /// <summary>用户自己已经绑了别的键时，不覆盖。</summary>
    [Fact]
    public void Existing_custom_binding_is_kept()
    {
        var old = AppSettings.Default with
        {
            Version = 5,
            Hotkeys = new HotkeyBindings([new HotkeyBinding(HotkeyActionIds.CaptureRegionAndPin, "Alt+F3")]),
        };

        var migrated = old.Migrate();

        var binding = Assert.Single(migrated.Hotkeys, item => item.ActionId == HotkeyActionIds.CaptureRegionAndPin);
        Assert.Equal("Alt+F3", binding.Keys);
    }
}
