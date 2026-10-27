using Kscan.Core.Configuration;

namespace Kscan.Core.Tests;

/// <summary>
/// 「关闭所有贴图」热键（默认 <c>Ctrl+Q</c>）：动作 id 必须在列表里，老配置要能自动补上。
/// </summary>
public sealed class CloseAllPinsHotkeyTests
{
    [Fact]
    public void Action_id_is_registered_in_the_known_list()
    {
        Assert.Equal("closeAllPins", HotkeyActionIds.CloseAllPins);
        Assert.Contains(HotkeyActionIds.CloseAllPins, HotkeyActionIds.All);
    }

    /// <summary>v4 的老配置（没有这条）升级后会自动补一条 Ctrl+Q。</summary>
    [Fact]
    public void Version_4_settings_get_the_default_binding()
    {
        var old = AppSettings.Default with
        {
            Version = 4,
            Hotkeys = new HotkeyBindings([new HotkeyBinding(HotkeyActionIds.Activate, "Ctrl+Alt+F5")]),
        };

        var migrated = old.Migrate();

        Assert.Equal(AppSettings.CurrentVersion, migrated.Version);
        var binding = Assert.Single(migrated.Hotkeys, item => item.ActionId == HotkeyActionIds.CloseAllPins);
        Assert.Equal("Ctrl+Q", binding.Keys);
    }

    /// <summary>已经是 v5 的配置不会再被塞一遍（用户可能主动删掉了它）。</summary>
    [Fact]
    public void Version_5_settings_are_left_alone()
    {
        var current = AppSettings.Default with
        {
            Version = AppSettings.CurrentVersion,
            Hotkeys = new HotkeyBindings([new HotkeyBinding(HotkeyActionIds.Activate, "Ctrl+Alt+F5")]),
        };

        var migrated = current.Migrate();

        Assert.DoesNotContain(migrated.Hotkeys, item => item.ActionId == HotkeyActionIds.CloseAllPins);
    }

    /// <summary>用户自己已经把同动作绑了别的键时，不覆盖。</summary>
    [Fact]
    public void Existing_custom_binding_is_kept()
    {
        var old = AppSettings.Default with
        {
            Version = 4,
            Hotkeys = new HotkeyBindings([new HotkeyBinding(HotkeyActionIds.CloseAllPins, "Ctrl+Shift+Q")]),
        };

        var migrated = old.Migrate();

        var binding = Assert.Single(migrated.Hotkeys, item => item.ActionId == HotkeyActionIds.CloseAllPins);
        Assert.Equal("Ctrl+Shift+Q", binding.Keys);
    }
}
