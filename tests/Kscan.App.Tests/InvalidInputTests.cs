using Kscan.App.Services;
using Kscan.Core.Configuration;

namespace Kscan.App.Tests;

/// <summary>
/// 非法字符的鲁棒性：id 会当文件名用（<c>user/cmd/&lt;id&gt;.json</c>），
/// 手写 JSON / 用户输入里的奇怪字符不能把文件写出目录、也不能撞上 Windows 保留名。
/// </summary>
public sealed class InvalidInputTests
{
    [Theory]
    [InlineData("wechat-archive", "wechat-archive")]
    [InlineData("WeChat_Archive", "wechat_archive")]
    [InlineData("../../evil", "evil")]
    [InlineData("..\\..\\evil", "evil")]
    [InlineData("a/b\\c", "a-b-c")]
    [InlineData("a b", "a-b")]
    [InlineData("a:b*c?d", "a-b-c-d")]
    public void Command_id_becomes_a_safe_file_name(string id, string expected) =>
        Assert.Equal(expected, UserCommandStore.SanitizeFileId(id));

    [Fact]
    public void Empty_or_all_invalid_ids_get_a_generated_one()
    {
        foreach (var id in new[] { "", null, "   ", "///", "\u0000\u0001", "😀😀", "测试命令" })
        {
            var safe = UserCommandStore.SanitizeFileId(id);
            Assert.False(string.IsNullOrWhiteSpace(safe));
            Assert.Matches("^[a-z0-9]+$", safe);
        }
    }

    /// <summary>Windows 保留设备名不能当文件名，否则写文件会失败。</summary>
    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public void Reserved_device_names_are_replaced(string id)
    {
        var safe = UserCommandStore.SanitizeFileId(id);
        Assert.NotEqual(id.ToLowerInvariant(), safe);
        Assert.Matches("^[a-z0-9]+$", safe);
    }

    /// <summary>id 里的控制字符 / 换行不能让路径出问题。</summary>
    [Fact]
    public void Control_characters_are_dropped()
    {
        Assert.Equal("a-b", UserCommandStore.SanitizeFileId("a\u0001\u0002b"));
        Assert.Equal("a-b", UserCommandStore.SanitizeFileId("a\nb"));
    }

    /// <summary>坏 JSON（控制字符 / 孤立代理项 / 超长串）只能被跳过，不能抛异常。</summary>
    [Theory]
    [InlineData("{ \"name\": \"\\u0000\\u0001\" }")]
    [InlineData("{ \"name\": \"\\ud800\" }")]
    [InlineData("{ \"name\": \"😀\" }")]
    [InlineData("{ \"program\": \"cmd.exe\", \"arguments\": \"\\u0007\" }")]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{")]
    public void Broken_command_json_is_skipped_without_throwing(string json)
    {
        var command = UserCommand.TryParse(json);

        // 关键：不抛异常。解析成功时字段也可能为空，这里只断言"拿到的是一个对象或 null"
        Assert.True(command is null || command.Name is not null);
    }

    [Fact]
    public void Huge_command_json_is_skipped_without_throwing()
    {
        var big = "{ \"name\": \"" + new string('x', 200_000) + "\" }";
        var command = UserCommand.TryParse(big);

        Assert.True(command is null || command.Name.Length > 0);
    }
}
