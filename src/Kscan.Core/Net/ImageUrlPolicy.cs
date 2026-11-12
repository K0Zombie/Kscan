using System.Net;
using System.Net.Sockets;

namespace Kscan.Core.Net;

/// <summary>
/// 图片地址的判定规则（纯逻辑，不联网、不依赖 UI）。
/// <para>
/// 单独抽出来有两个原因：一是能单测（网络地址该不该放行是最容易写错的地方），
/// 二是"要不要放行"这种安全判断不该散落在 HTTP 代码里。
/// </para>
/// </summary>
public static class ImageUrlPolicy
{
    /// <summary>认作图片的扩展名。</summary>
    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

    /// <summary>
    /// 这段文本像不像"一个图片地址"：<b>单个</b> http(s) 地址，且路径扩展名是图片格式。
    /// 保守判断 —— 宁可退回去贴文本，也别把任意链接都拿去下载。
    /// </summary>
    public static bool LooksLikeImageUrl(string? text, out Uri? uri)
    {
        uri = null;

        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (!IsPublicHttpUrl(parsed))
        {
            return false;
        }

        var extension = Path.GetExtension(parsed.AbsolutePath);
        if (!ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>
    /// 只放行公网 http/https：回环、私网、链路本地、局域网名一律拒绝。
    /// <para>
    /// 剪贴板里的地址可能指向路由器管理页、本机服务或内网设备（SSRF），
    /// 而这段文本是<b>外部来源</b>写进剪贴板的，不能信任。
    /// </para>
    /// <para>
    /// 只对<b>字面量 IP</b> 做判断，不做 DNS 解析：先解析再连接会留下 rebinding 窗口
    /// （解析时是公网、连接时变内网），解析这一步交给 HTTP 栈自己完成。
    /// </para>
    /// </summary>
    public static bool IsPublicHttpUrl(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = uri.DnsSafeHost;
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        var isIpLiteral = IPAddress.TryParse(host, out var address);

        // 内网/本机的常见写法：localhost 家族、mDNS、内部域名后缀、以及**单标签主机名**
        //（http://router/a.png 这种一定是局域网名，公网域名不会没有点）
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("localhost.", StringComparison.OrdinalIgnoreCase)
            || HasAnySuffix(host, ".local", ".localdomain", ".localhost", ".lan", ".home.arpa", ".internal", ".intranet", ".corp"))
        {
            return false;
        }

        if (!isIpLiteral && !host.Contains('.'))
        {
            return false;
        }

        if (!IPAddress.TryParse(host, out address))
        {
            return true;   // 普通域名
        }

        // IPv4-mapped IPv6（::ffff:192.168.1.1）按 IPv4 规则判断，否则会绕过上面的私网检查。
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                0 => false,                                            // 0.0.0.0/8  "本网络"
                10 => false,                                           // 10.0.0.0/8
                100 when bytes[1] >= 64 && bytes[1] <= 127 => false,   // 100.64.0.0/10 CGNAT
                127 => false,                                          // 环回
                169 when bytes[1] == 254 => false,                      // 169.254.0.0/16 链路本地
                172 when bytes[1] >= 16 && bytes[1] <= 31 => false,    // 172.16.0.0/12
                192 when bytes[1] == 168 => false,                      // 192.168.0.0/16
                _ => true,
            };
        }

        // IPv6 唯一本地地址 fc00::/7
        return (bytes[0] & 0xFE) != 0xFC;
    }

    /// <summary>响应类型算不算图片（也接受"没给 Content-Type 但扩展名像图片"）。</summary>
    public static bool IsImageContentType(string? mediaType, Uri uri) =>
        (!string.IsNullOrEmpty(mediaType) && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        || ImageExtensions.Contains(Path.GetExtension(uri.AbsolutePath), StringComparer.OrdinalIgnoreCase);

    private static bool HasAnySuffix(string host, params string[] suffixes) =>
        suffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}
