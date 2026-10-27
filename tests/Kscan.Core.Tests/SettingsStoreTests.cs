using Kscan.Core.Configuration;

namespace Kscan.Core.Tests;

/// <summary>配置持久化：往返、缺省、损坏、未知字段、原子写。</summary>
public class SettingsStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "kscan-settings-tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录清理失败不影响测试结论
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Save_then_load_round_trips_every_field()
    {
        var store = new JsonSettingsStore(FilePath);
        var original = new AppSettings
        {
            Version = AppSettings.CurrentVersion,
            Language = "en-US",
            StartSilently = true,
            MinimizeToTray = true,
            CloseToTray = false,
            AutoCopySingleResult = false,
            ShowPlatformSummary = false,
        };

        await store.SaveAsync(original);
        var loaded = new JsonSettingsStore(FilePath).Load();

        Assert.Equal(original, loaded);
        Assert.Null(store.LastError);
    }

    [Fact]
    public async Task Whole_window_capture_switches_default_to_on_and_round_trip()
    {
        // 旧配置里没有这两个字段：读出来必须是"开"（不改变用户已有行为）
        var defaults = new JsonSettingsStore(FilePath).Load();
        Assert.True(defaults.CaptureWholeWindowOffScreen);
        Assert.True(defaults.CaptureWholeWindowOccluded);

        var store = new JsonSettingsStore(FilePath);
        await store.SaveAsync(AppSettings.Default with
        {
            CaptureWholeWindowOffScreen = false,
            CaptureWholeWindowOccluded = false,
        });

        var loaded = new JsonSettingsStore(FilePath).Load();
        Assert.False(loaded.CaptureWholeWindowOffScreen);
        Assert.False(loaded.CaptureWholeWindowOccluded);
    }

    [Fact]
    public async Task Hotkey_bindings_round_trip_and_default_to_unbound()
    {
        // 默认一个热键都不绑（全局热键会占住系统级按键，得用户自己选）
        Assert.Empty(AppSettings.Default.Hotkeys);

        var store = new JsonSettingsStore(FilePath);
        var original = AppSettings.Default with
        {
            Version = AppSettings.CurrentVersion,
            Hotkeys = new HotkeyBindings(
            [
                new HotkeyBinding(HotkeyActionIds.CaptureRegion, "F1"),           // 不带修饰键也要能存
                new HotkeyBinding(HotkeyActionIds.CaptureRegion, "Ctrl+Alt+S"),   // 同一个动作可以绑多个键
                new HotkeyBinding(HotkeyActionIds.PinClipboard, "Ctrl+Alt+Shift+Q"),
                new HotkeyBinding(HotkeyActionIds.RunCommand, "Ctrl+Alt+F8")
                {
                    Command = "C:\\Program Files\\工具\\kscan.exe",
                    Arguments = "decode \"C:\\my images\\码.png\"",
                    WorkingDirectory = "C:\\my images",
                },
            ]),
        };

        await store.SaveAsync(original);
        var loaded = new JsonSettingsStore(FilePath).Load();

        // 顺序与重复都要原样保住：一条被占用不该影响同一动作的另一条
        Assert.Equal(4, loaded.Hotkeys.Count);
        Assert.Equal(HotkeyActionIds.CaptureRegion, loaded.Hotkeys[0].ActionId);
        Assert.Equal("F1", loaded.Hotkeys[0].Keys);
        Assert.Equal("Ctrl+Alt+S", loaded.Hotkeys[1].Keys);
        Assert.Equal(HotkeyActionIds.PinClipboard, loaded.Hotkeys[2].ActionId);

        // record 的值相等必须成立：AppSettingsService 靠 `next == Settings` 跳过无谓的写盘。
        // 若 Hotkeys 换成 List / 数组 / 字典，这一条会挂在**引用相等**上 —— 这正是 HotkeyBindings 存在的理由。
        Assert.Equal(original, loaded);
        Assert.Null(store.LastError);
    }

    [Fact]
    public void Legacy_scalar_hotkey_fields_migrate_into_the_list()
    {
        // 旧版（一个动作一个键）写的配置：读出来要变成列表，而不是把用户的热键丢掉。
        // 版本 1 → 2 时还会补上「框选排除应用」的默认热键。
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """
            {
              "version": 1,
              "hotkeyCaptureRegion": "Ctrl+Alt+F1",
              "hotkeyPinClipboard": "F4",
              "hotkeyScanClipboard": ""
            }
            """);

        var loaded = new JsonSettingsStore(FilePath).Load();

        Assert.Equal(
            [
                new HotkeyBinding(HotkeyActionIds.CaptureRegion, "Ctrl+Alt+F1"),
                new HotkeyBinding(HotkeyActionIds.PinClipboard, "F4"),   // 空串的那条不迁移
                new HotkeyBinding(HotkeyActionIds.PickExcludedApp, AppSettings.DefaultExcludeAppHotkey),
                new HotkeyBinding(HotkeyActionIds.CloseAllPins, AppSettings.DefaultCloseAllPinsHotkey),
                new HotkeyBinding(HotkeyActionIds.CaptureRegionAndPin, AppSettings.DefaultCaptureAndPinHotkey),
            ],
            loaded.Hotkeys.ToArray());

        Assert.Equal(AppSettings.CurrentVersion, loaded.Version);

        // 迁移后旧字段被清掉：再存一次，文件里就不该有它们了
        Assert.Null(loaded.HotkeyCaptureRegion);
        Assert.Null(loaded.HotkeyPinClipboard);
        Assert.Null(loaded.HotkeyScanClipboard);
    }

    [Fact]
    public void Version_two_does_not_get_the_default_exclude_hotkey_back()
    {
        // 用户删掉那条默认热键后版本已是 2，再读不能又被加回来
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """
            {
              "version": 2,
              "hotkeys": [{ "actionId": "captureRegion", "keys": "F2" }]
            }
            """);

        var loaded = new JsonSettingsStore(FilePath).Load();

        // 「框选排除应用」不该被加回来（版本已是 2）；但之后新增的「关闭所有贴图」和「截取并贴图」会各补一次
        Assert.Equal(3, loaded.Hotkeys.Count);
        Assert.Equal(HotkeyActionIds.CaptureRegion, loaded.Hotkeys[0].ActionId);
        Assert.Equal(HotkeyActionIds.CloseAllPins, loaded.Hotkeys[1].ActionId);
        Assert.Equal(HotkeyActionIds.CaptureRegionAndPin, loaded.Hotkeys[2].ActionId);
        Assert.DoesNotContain(loaded.Hotkeys, item => item.ActionId == HotkeyActionIds.PickExcludedApp);
    }

    [Fact]
    public async Task Ignored_apps_and_force_modifiers_round_trip()
    {
        var store = new JsonSettingsStore(FilePath);
        var original = AppSettings.Default with
        {
            Version = AppSettings.CurrentVersion,
            IgnoredApps = new AppList(["taskmgr.exe", @"C:\Games\SomeGame\game.exe"]),
            ForceHotkeyModifiers = "Ctrl+Shift",
        };

        await store.SaveAsync(original);
        var loaded = new JsonSettingsStore(FilePath).Load();

        Assert.Equal(2, loaded.IgnoredApps.Count);
        Assert.Equal("taskmgr.exe", loaded.IgnoredApps[0]);
        Assert.Equal("Ctrl+Shift", loaded.ForceHotkeyModifiers);
        Assert.Equal(original, loaded);
    }

    [Fact]
    public void Missing_ignored_apps_falls_back_to_an_empty_list()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """
            {
              "version": 2,
              "hotkeys": []
            }
            """);

        var loaded = new JsonSettingsStore(FilePath).Load();

        Assert.Empty(loaded.IgnoredApps);
        Assert.Equal("Ctrl+Alt", loaded.ForceHotkeyModifiers);
    }

    [Fact]
    public void Missing_file_yields_defaults()
    {
        var store = new JsonSettingsStore(FilePath);

        var loaded = store.Load();

        Assert.Equal(AppSettings.Default, loaded);
        Assert.Null(store.LastError);
    }

    [Fact]
    public void Empty_file_yields_defaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "   \n");

        var loaded = new JsonSettingsStore(FilePath).Load();

        Assert.Equal(AppSettings.Default, loaded);
    }

    [Fact]
    public void Corrupt_json_yields_defaults_and_records_the_reason()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ this is not json");

        var store = new JsonSettingsStore(FilePath);
        var loaded = store.Load();

        Assert.Equal(AppSettings.Default, loaded);
        Assert.False(string.IsNullOrEmpty(store.LastError));
    }

    [Fact]
    public void Unknown_fields_and_missing_fields_are_tolerated()
    {
        Directory.CreateDirectory(_directory);
        // 多一个将来才会有的字段、少一个当前字段 —— 都要能读
        File.WriteAllText(FilePath, """
            {
              "language": "zh-CN",
              "somethingFromTheFuture": [1, 2, 3]
            }
            """);

        var loaded = new JsonSettingsStore(FilePath).Load();

        Assert.Equal("zh-CN", loaded.Language);
        Assert.True(loaded.AutoCopySingleResult);
    }

    [Fact]
    public async Task Save_creates_the_directory_and_leaves_no_temp_file()
    {
        var store = new JsonSettingsStore(FilePath);

        await store.SaveAsync(AppSettings.Default with { Language = "zh-CN" });

        Assert.True(File.Exists(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void Defaults_are_auto_copy_on_language_following_system_summary_shown_and_auto_actions_off()
    {
        Assert.True(AppSettings.Default.AutoCopySingleResult);
        Assert.True(AppSettings.Default.ShowPlatformSummary);
        Assert.False(AppSettings.Default.OpenUrlAutomatically);   // 自动打开外部程序必须默认关闭
        Assert.False(AppSettings.Default.WatchClipboard);          // 剪贴板内容不默认被读取
        Assert.Null(AppSettings.Default.Language);
        Assert.Equal(1, AppSettings.Default.Version);
    }

    [Fact]
    public void Missing_new_field_falls_back_to_its_default()
    {
        // 老配置文件里没有 showPlatformSummary —— 读出来要当"显示"（默认值），而不是 false
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """
            {
              "version": 1,
              "language": "zh-CN",
              "autoCopySingleResult": false
            }
            """);

        var loaded = new JsonSettingsStore(FilePath).Load();

        Assert.True(loaded.ShowPlatformSummary);
        Assert.False(loaded.AutoCopySingleResult);
    }

    [Fact]
    public async Task Saving_twice_overwrites_cleanly()
    {
        var store = new JsonSettingsStore(FilePath);

        await store.SaveAsync(new AppSettings { Language = "zh-CN" });
        await store.SaveAsync(new AppSettings { Language = "en-US", AutoCopySingleResult = false });

        var loaded = store.Load();
        Assert.Equal("en-US", loaded.Language);
        Assert.False(loaded.AutoCopySingleResult);
    }
}
