using Kscan.Core.Net;

namespace Kscan.Core.Tests;

public class ImageUrlPolicyTests
{
    [Theory]
    [InlineData("https://example.com/a.png", true)]
    [InlineData("http://images.example.com/a.jpg", true)]
    [InlineData("http://127.0.0.1/a.png", false)]
    [InlineData("http://10.1.2.3/a.png", false)]
    [InlineData("http://172.16.0.1/a.png", false)]
    [InlineData("http://192.168.1.1/a.png", false)]
    [InlineData("http://169.254.1.1/a.png", false)]
    [InlineData("http://localhost/a.png", false)]
    [InlineData("http://router/a.png", false)]
    [InlineData("ftp://example.com/a.png", false)]
    [InlineData("http://[::1]/a.png", false)]
    [InlineData("http://[fc00::1]/a.png", false)]
    [InlineData("http://[::ffff:127.0.0.1]/a.png", false)]
    [InlineData("http://[::ffff:192.168.1.1]/a.png", false)]
    public void Public_http_urls_only(string text, bool expected)
    {
        var ok = ImageUrlPolicy.LooksLikeImageUrl(text, out _);
        Assert.Equal(expected, ok);
    }

    [Theory]
    [InlineData("https://example.com/a.png", true)]
    [InlineData("https://example.com/a.txt", false)]
    [InlineData("https://example.com/a.png?x=1", true)]
    [InlineData("not a url", false)]
    [InlineData("https://example.com/a.png extra", false)]
    public void Only_single_image_urls_are_accepted(string text, bool expected)
    {
        Assert.Equal(expected, ImageUrlPolicy.LooksLikeImageUrl(text, out _));
    }
}
