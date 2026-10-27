using Kscan.Core.Imaging;
using Kscan.Core.Model;
using Kscan.Core.Pipeline;

namespace Kscan.Core.Tests;

/// <summary>结构化内容模板：payload 格式正确，且经二维码往返后内容不变。</summary>
public class BarcodeContentTests
{
    [Fact]
    public void Wifi_escapes_reserved_characters()
    {
        var payload = BarcodeContent.Wifi(new WifiCredentials("My;Net", "pa:ss,word"));

        Assert.Equal(@"WIFI:T:WPA;S:My\;Net;P:pa\:ss\,word;;", payload);
    }

    [Fact]
    public void Wifi_without_password_and_open_network()
    {
        Assert.Equal("WIFI:T:WPA;S:Open;;", BarcodeContent.Wifi(new WifiCredentials("Open")));
        Assert.Equal("WIFI:T:nopass;S:Free;H:true;;", BarcodeContent.Wifi(new WifiCredentials("Free", null, "nopass", Hidden: true)));
    }

    [Fact]
    public void VCard_uses_crlf_and_skips_empty_fields()
    {
        var payload = BarcodeContent.VCard(new VCardFields("张三", "某公司", "工程师", "+8613800138000", "z@example.com"));

        Assert.StartsWith("BEGIN:VCARD\r\nVERSION:3.0\r\n", payload, StringComparison.Ordinal);
        Assert.Contains("FN:张三\r\n", payload, StringComparison.Ordinal);
        Assert.Contains("ORG:某公司\r\n", payload, StringComparison.Ordinal);
        Assert.Contains("TEL:+8613800138000\r\n", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("NOTE:", payload, StringComparison.Ordinal);
        Assert.EndsWith("END:VCARD\r\n", payload, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData("http://example.com", "http://example.com")]
    [InlineData("  https://例子.测试/路径  ", "https://例子.测试/路径")]
    public void Url_adds_https_when_scheme_is_missing(string input, string expected)
    {
        Assert.Equal(expected, BarcodeContent.Url(input));
    }

    [Fact]
    public void Email_encodes_subject_and_body()
    {
        Assert.Equal(
            "mailto:a@b.com?subject=%E4%B8%BB%E9%A2%98&body=x%20y",
            BarcodeContent.Email(new EmailFields("a@b.com", "主题", "x y")));
        Assert.Equal("mailto:a@b.com", BarcodeContent.Email(new EmailFields("a@b.com")));
    }

    [Fact]
    public void Phone_and_sms_normalize_numbers()
    {
        Assert.Equal("tel:+8613800138000", BarcodeContent.Phone("+86 138-0013-8000"));
        Assert.Equal("SMSTO:+8613800138000:你好", BarcodeContent.Sms(new SmsFields("+86 138-0013-8000", "你好")));
    }

    [Fact]
    public void Geo_formats_coordinates_and_label()
    {
        Assert.Equal("geo:39.9042,116.4074", BarcodeContent.Geo(new GeoPoint(39.9042, 116.4074)));
        Assert.Equal(
            "geo:39.9042,116.4074?q=39.9042,116.4074(%E5%A4%A9%E5%AE%89%E9%97%A8)",
            BarcodeContent.Geo(new GeoPoint(39.9042, 116.4074, "天安门")));
    }

    [Fact]
    public void Geo_rejects_out_of_range_coordinates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BarcodeContent.Geo(new GeoPoint(91, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => BarcodeContent.Geo(new GeoPoint(0, 181)));
    }

    public static TheoryData<string, string> RoundTripPayloads => new()
    {
        { "wifi", BarcodeContent.Wifi(new WifiCredentials("办公室 WiFi", "p@ss;word")) },
        { "vcard", BarcodeContent.VCard(new VCardFields("李四", "某公司", "经理", "+8613900139000", "l@example.com", "https://example.com")) },
        { "url", BarcodeContent.Url("例子.测试/扫码") },
        { "email", BarcodeContent.Email(new EmailFields("a@b.com", "主题", "正文")) },
        { "tel", BarcodeContent.Phone("+8613800138000") },
        { "sms", BarcodeContent.Sms(new SmsFields("+8613800138000", "短信内容")) },
        { "geo", BarcodeContent.Geo(new GeoPoint(39.9042, 116.4074, "天安门")) },
    };

    [Theory]
    [MemberData(nameof(RoundTripPayloads))]
    public void Template_payload_survives_qr_round_trip(string kind, string payload)
    {
        var request = new BarcodeGenerationRequest
        {
            Text = payload,
            Format = "QR_CODE",
            Width = 480,
            Height = 480,
            ErrorCorrection = "M",
        };

        var decoded = ScanPipeline.CreateDefault()
            .Scan(SkiaImageCodec.FromEncoded(BarcodeImageFactory.CreatePng(request)));

        Assert.True(decoded.Success, $"{kind} 模板生成的二维码读不回来");
        Assert.Equal(payload, decoded.FirstText);
    }
}
