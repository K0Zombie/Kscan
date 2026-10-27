using Kscan.Core.Model;

namespace Kscan.Core.Tests;

/// <summary>识别结果分类：界面靠它决定显示方式与一键动作。</summary>
public class ResultContentTests
{
    [Theory]
    [InlineData("https://example.com/x?y=1", ResultContentKind.Url)]
    [InlineData("http://例子.测试", ResultContentKind.Url)]
    [InlineData("WIFI:T:WPA;S:MyNet;P:pass;;", ResultContentKind.Wifi)]
    [InlineData("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:张三\r\nEND:VCARD\r\n", ResultContentKind.VCard)]
    [InlineData("mailto:a@b.com?subject=x", ResultContentKind.Email)]
    [InlineData("tel:+8613800138000", ResultContentKind.Phone)]
    [InlineData("SMSTO:+8613800138000:你好", ResultContentKind.Sms)]
    [InlineData("geo:39.9042,116.4074", ResultContentKind.Geo)]
    [InlineData("""{"type":"mini","id":1}""", ResultContentKind.Json)]
    [InlineData("[1,2,3]", ResultContentKind.Json)]
    [InlineData("就是一段普通文字", ResultContentKind.Text)]
    [InlineData("hello world", ResultContentKind.Text)]
    public void Kind_is_detected(string text, ResultContentKind expected)
    {
        Assert.Equal(expected, ResultContent.Parse(text).Kind);
    }

    [Fact]
    public void Url_exposes_host_and_open_action()
    {
        var content = ResultContent.Parse("https://example.com/path?q=1");

        Assert.Equal("example.com", content.Title);
        Assert.Equal("打开链接", content.ActionLabel);
        Assert.Equal("https://example.com/path?q=1", content.ActionTarget);
        Assert.True(content.HasAction);
    }

    [Fact]
    public void Wifi_summary_hides_password()
    {
        var content = ResultContent.Parse(@"WIFI:T:WPA;S:办公室;P:s3cr3t;;");

        Assert.Equal("办公室（WPA）", content.Title);
        Assert.DoesNotContain("s3cr3t", content.Title);
        Assert.False(content.HasAction);
    }

    [Fact]
    public void Wifi_hidden_flag_is_reported()
    {
        var content = ResultContent.Parse("WIFI:T:WPA;S:Net;H:true;;");

        Assert.Equal("Net（WPA，隐藏）", content.Title);
    }

    [Fact]
    public void Wifi_parser_understands_escaped_separators()
    {
        // SSID 里带分号（生成时被转义成 \;），解析后应还原
        var content = ResultContent.Parse(@"WIFI:T:WPA;S:My\;Net;P:p\:w;;");

        Assert.Equal("My;Net（WPA）", content.Title);
    }

    [Fact]
    public void VCard_title_prefers_name_and_phone()
    {
        var content = ResultContent.Parse("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:李四\r\nTEL:+8613900139000\r\nEND:VCARD\r\n");

        Assert.Equal("李四（+8613900139000）", content.Title);
    }

    [Fact]
    public void Base64_is_decoded_when_it_looks_like_text()
    {
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("解码后的内容 hello"));
        var content = ResultContent.Parse(payload);

        Assert.Equal(ResultContentKind.Base64, content.Kind);
        Assert.Equal("解码后的内容 hello", content.ActionTarget);
    }

    [Fact]
    public void Plain_long_words_are_not_mistaken_for_base64()
    {
        Assert.Equal(ResultContentKind.Text, ResultContent.Parse("password").Kind);
        Assert.Equal(ResultContentKind.Text, ResultContent.Parse("hello world 这是一段说明文字").Kind);
    }

    [Fact]
    public void Sms_exposes_number_action()
    {
        var content = ResultContent.Parse("SMSTO:+8613800138000:你好");

        Assert.Equal("+8613800138000：你好", content.Title);
        Assert.Equal("SMSTO:+8613800138000", content.ActionTarget);
    }

    [Fact]
    public void Geo_action_target_keeps_original_uri()
    {
        var content = ResultContent.Parse("geo:39.9042,116.4074?q=39.9042,116.4074(天安门)");

        Assert.Equal("39.9042,116.4074", content.Title);
        Assert.Equal("在地图中查看", content.ActionLabel);
    }

    [Fact]
    public void Empty_input_is_plain_text()
    {
        Assert.Equal(ResultContentKind.Text, ResultContent.Parse("").Kind);
        Assert.Equal(ResultContentKind.Text, ResultContent.Parse(null).Kind);
    }
}
