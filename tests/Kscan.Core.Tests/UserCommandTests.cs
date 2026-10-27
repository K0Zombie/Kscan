using Kscan.Core.Configuration;

namespace Kscan.Core.Tests;

/// <summary>
/// 「执行命令」条目：JSON 往返（含 <c>runAsAdmin</c>）与配置相等性。
/// <para>
/// 相等性不是学术问题：<c>AppSettings</c> 靠 <c>next == Settings</c> 判断要不要写盘，
/// 而 <c>HotkeyBinding</c> 是 record —— 新加的字段必须一起进相等性，否则"改了勾选框却不保存"。
/// </para>
/// </summary>
public sealed class UserCommandTests
{
    [Fact]
    public void Defaults_run_with_normal_rights()
    {
        var command = new UserCommand();

        Assert.False(command.RunAsAdmin);
        Assert.True(command.Enabled);
    }

    [Fact]
    public void Json_round_trip_keeps_run_as_admin()
    {
        var original = new UserCommand
        {
            Id = "wechat-archive",
            Name = "微信文件归档",
            Program = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -File D:\\tools\\wechat-archive.ps1",
            WorkingDirectory = "D:\\tools",
            Hotkey = "Ctrl+Alt+W",
            RunAsAdmin = true,
            Enabled = false,
        };

        var parsed = UserCommand.TryParse(original.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal(original, parsed);
        Assert.True(parsed!.RunAsAdmin);
        Assert.False(parsed.Enabled);
    }

    /// <summary>老文件里没有 <c>runAsAdmin</c>：读出来必须是 false（保持原来的普通权限行为）。</summary>
    [Fact]
    public void Missing_run_as_admin_means_normal_rights()
    {
        var parsed = UserCommand.TryParse("""{ "name": "老命令", "program": "notepad.exe" }""");

        Assert.NotNull(parsed);
        Assert.False(parsed!.RunAsAdmin);
    }

    /// <summary>热键绑定要能把「以管理员身份运行」带到启动器。</summary>
    [Fact]
    public void Hotkey_binding_carries_the_elevation_flag()
    {
        var normal = new HotkeyBinding(HotkeyActionIds.RunCommand, "F8") { Command = "cmd.exe" };
        var elevated = new HotkeyBinding(HotkeyActionIds.RunCommand, "F8") { Command = "cmd.exe", RunAsAdmin = true };

        Assert.False(normal.RunAsAdmin);
        Assert.True(elevated.RunAsAdmin);
        Assert.NotEqual(normal, elevated);   // 相等性必须区分，否则配置变更检测不到
    }

    /// <summary>迁移/比较时整张热键表也要能看出这个差异。</summary>
    [Fact]
    public void Hotkey_lists_differ_when_only_elevation_differs()
    {
        var normal = new HotkeyBindings([new HotkeyBinding(HotkeyActionIds.RunCommand, "F8") { Command = "cmd.exe" }]);
        var elevated = new HotkeyBindings([new HotkeyBinding(HotkeyActionIds.RunCommand, "F8") { Command = "cmd.exe", RunAsAdmin = true }]);

        Assert.NotEqual(normal, elevated);
        Assert.Equal(normal, new HotkeyBindings([new HotkeyBinding(HotkeyActionIds.RunCommand, "F8") { Command = "cmd.exe" }]));
    }

    /// <summary>已命名的命令在下拉里只显示「名称 (热键)」，细节留给表单。</summary>
    [Fact]
    public void Display_text_is_name_plus_hotkey()
    {
        var withHotkey = new UserCommand { Name = "微信文件归档", Hotkey = "Ctrl+Alt+W" };
        var withoutHotkey = new UserCommand { Name = "没有热键", Hotkey = "  " };

        Assert.Equal("微信文件归档  (Ctrl+Alt+W)", withHotkey.ToString());
        Assert.Equal("没有热键", withoutHotkey.ToString());
    }
}
