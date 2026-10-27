using Avalonia.Headless.XUnit;
using Kscan.Core.Configuration;

namespace Kscan.App.Tests;

/// <summary>
/// 「切换跨屏框选」热键的默认值与版本迁移。
/// <para>
/// 只有版本 &lt; 10 才补这条默认键：用户已经把版本存成 10（或自己改过这个动作的键）之后，
/// 不能再被"补默认"覆盖掉。
/// </para>
/// </summary>
public sealed class ToggleCaptureAcrossScreensTests
{
    [Fact]
    public void Version_9_migration_adds_the_default_toggle_hotkey()
    {
        var migrated = new AppSettings { Version = 9 }.Migrate();

        Assert.Equal(AppSettings.CurrentVersion, migrated.Version);
        Assert.Contains(
            migrated.Hotkeys,
            binding => binding.ActionId == HotkeyActionIds.ToggleCaptureAcrossScreens
                       && binding.Keys == AppSettings.DefaultToggleCaptureAcrossScreensHotkey);
    }

    [Fact]
    public void Migration_does_not_override_a_user_chosen_key()
    {
        var custom = new AppSettings
        {
            Version = 9,
            Hotkeys = new HotkeyBindings(
            [
                new HotkeyBinding(HotkeyActionIds.ToggleCaptureAcrossScreens, "Ctrl+Alt+Z"),
            ]),
        };

        var migrated = custom.Migrate();

        var binding = Assert.Single(
            migrated.Hotkeys.Where(item => item.ActionId == HotkeyActionIds.ToggleCaptureAcrossScreens));
        Assert.Equal("Ctrl+Alt+Z", binding.Keys);
    }

    [Fact]
    public void Already_current_settings_are_left_alone()
    {
        var settings = new AppSettings { Version = AppSettings.CurrentVersion };
        var migrated = settings.Migrate();

        Assert.DoesNotContain(migrated.Hotkeys, item => item.ActionId == HotkeyActionIds.ToggleCaptureAcrossScreens);
    }
}
