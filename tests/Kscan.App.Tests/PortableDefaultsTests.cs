using Kscan.App.Services;
using Kscan.Core.Configuration;

namespace Kscan.App.Tests;

public sealed class PortableDefaultsTests
{
    [Fact]
    public void Portable_seed_is_used_only_for_a_missing_user_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kscan-portable-default-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var userFile = Path.Combine(directory, "settings.json");
            File.WriteAllText(Path.Combine(directory, "portable-defaults.json"),
                """{"hotkeys":[{"actionId":"captureRegion","keys":"F2"}]}""");

            // 便携默认只有一条 F2；版本迁移再补上缺省热键：
            // 框选排除应用 / 关闭所有贴图 / 截取并贴图 / 切换贴图置顶 / 打开贴图管理 / 关闭光标下的贴图 / 切换跨屏框选
            var seeded = AppSettingsService.SelectStartupStore(userFile, directory, true).Load();
            Assert.Equal(12, seeded.Hotkeys.Count);
            Assert.Contains(seeded.Hotkeys, binding => binding.ActionId == HotkeyActionIds.CaptureRegion);
            Assert.Contains(seeded.Hotkeys,
                binding => binding.ActionId == HotkeyActionIds.PickExcludedApp
                           && binding.Keys == AppSettings.DefaultExcludeAppHotkey);

            Assert.Empty(AppSettingsService.SelectStartupStore(userFile, directory, false).Load().Hotkeys);

            File.WriteAllText(userFile, """{"hotkeys":[],"closeToTray":false}""");
            var existing = AppSettingsService.SelectStartupStore(userFile, directory, true).Load();
            Assert.Equal(11, existing.Hotkeys.Count);
            Assert.Equal(HotkeyActionIds.PickExcludedApp, existing.Hotkeys[0].ActionId);
            Assert.Equal(HotkeyActionIds.CloseAllPins, existing.Hotkeys[1].ActionId);
            Assert.Equal(HotkeyActionIds.CaptureRegionAndPin, existing.Hotkeys[2].ActionId);
            Assert.Equal(HotkeyActionIds.TogglePinTopmost, existing.Hotkeys[3].ActionId);
            Assert.Equal(HotkeyActionIds.OpenPinManager, existing.Hotkeys[4].ActionId);
            Assert.Equal(HotkeyActionIds.ClosePinUnderCursor, existing.Hotkeys[5].ActionId);
            Assert.Equal(HotkeyActionIds.ToggleCaptureAcrossScreens, existing.Hotkeys[6].ActionId);
            Assert.Equal(HotkeyActionIds.CaptureRegionWithAnnotation, existing.Hotkeys[7].ActionId);
            Assert.Equal(HotkeyActionIds.AnnotateClipboard, existing.Hotkeys[8].ActionId);
            Assert.Equal(HotkeyActionIds.AnnotateScreen, existing.Hotkeys[9].ActionId);
            Assert.False(existing.CloseToTray);

            File.WriteAllText(userFile, "invalid JSON");
            var damaged = AppSettingsService.SelectStartupStore(userFile, directory, true);
            Assert.Empty(damaged.Load().Hotkeys);
            Assert.NotNull(damaged.LastError);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
