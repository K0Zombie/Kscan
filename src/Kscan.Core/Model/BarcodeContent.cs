using System.Globalization;
using System.Text;

namespace Kscan.Core.Model;

/// <summary>WiFi 凭据。</summary>
public sealed record WifiCredentials(
    string Ssid,
    string? Password = null,
    string Security = "WPA",
    bool Hidden = false);

/// <summary>vCard 字段（姓名必填，其余可选）。</summary>
public sealed record VCardFields(
    string Name,
    string? Organization = null,
    string? Title = null,
    string? Phone = null,
    string? Email = null,
    string? Url = null,
    string? Note = null);

public sealed record EmailFields(string To, string? Subject = null, string? Body = null);

public sealed record SmsFields(string To, string? Message = null);

public sealed record GeoPoint(double Latitude, double Longitude, string? Label = null);

/// <summary>
/// 结构化内容模板：把字段编成扫码器认得的标准 payload
/// （WiFi / vCard / mailto / tel / SMSTO / geo），省得用户手拼转义。
/// 生成的字符串直接交给 <see cref="Imaging.BarcodeEncoder"/> 编码即可。
/// </summary>
public static class BarcodeContent
{
    /// <summary>
    /// WiFi 配置。语法为 <c>WIFI:T:WPA;S:&lt;ssid&gt;;P:&lt;password&gt;;</c>，
    /// 值内的 <c>\ ; , : "</c> 会被转义（否则密码里有分号就会破坏结构）。
    /// </summary>
    public static string Wifi(WifiCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentials.Ssid);

        var security = string.IsNullOrWhiteSpace(credentials.Security)
            ? "WPA"
            : credentials.Security.Trim().ToUpperInvariant();

        var builder = new StringBuilder("WIFI:");
        builder.Append("T:").Append(security is "NONE" or "NOPASS" or "OPEN" ? "nopass" : security).Append(';');
        builder.Append("S:").Append(EscapeWifi(credentials.Ssid)).Append(';');

        if (!string.IsNullOrEmpty(credentials.Password))
        {
            builder.Append("P:").Append(EscapeWifi(credentials.Password)).Append(';');
        }

        if (credentials.Hidden)
        {
            builder.Append("H:true;");
        }

        builder.Append(';');
        return builder.ToString();
    }

    /// <summary>vCard 3.0（行尾按规范用 CRLF）。</summary>
    public static string VCard(VCardFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentException.ThrowIfNullOrWhiteSpace(fields.Name);

        var builder = new StringBuilder();
        builder.Append("BEGIN:VCARD\r\n");
        builder.Append("VERSION:3.0\r\n");
        builder.Append("N:").Append(EscapeVCard(fields.Name)).Append("\r\n");
        builder.Append("FN:").Append(EscapeVCard(fields.Name)).Append("\r\n");
        Append("ORG:", fields.Organization);
        Append("TITLE:", fields.Title);
        Append("TEL:", fields.Phone);
        Append("EMAIL:", fields.Email);
        Append("URL:", fields.Url);
        Append("NOTE:", fields.Note);
        builder.Append("END:VCARD\r\n");
        return builder.ToString();

        void Append(string prefix, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                builder.Append(prefix).Append(EscapeVCard(value)).Append("\r\n");
            }
        }
    }

    /// <summary>URL；缺少协议时补 <c>https://</c>。</summary>
    public static string Url(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var trimmed = url.Trim();
        return HasScheme(trimmed) ? trimmed : "https://" + trimmed;
    }

    /// <summary>邮件（<c>mailto:</c>，主题与正文做了 URL 编码）。</summary>
    public static string Email(EmailFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentException.ThrowIfNullOrWhiteSpace(fields.To);

        var builder = new StringBuilder("mailto:").Append(fields.To.Trim());
        var query = new List<string>();

        if (!string.IsNullOrEmpty(fields.Subject))
        {
            query.Add("subject=" + Uri.EscapeDataString(fields.Subject));
        }

        if (!string.IsNullOrEmpty(fields.Body))
        {
            query.Add("body=" + Uri.EscapeDataString(fields.Body));
        }

        if (query.Count > 0)
        {
            builder.Append('?').Append(string.Join('&', query));
        }

        return builder.ToString();
    }

    /// <summary>电话（<c>tel:</c>）。</summary>
    public static string Phone(string number)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);

        // 只保留数字与加号，去掉空格、连字符、括号这类视觉分隔符
        var cleaned = new string(number.Where(c => char.IsAsciiDigit(c) || c == '+').ToArray());
        ArgumentException.ThrowIfNullOrWhiteSpace(cleaned);
        return "tel:" + cleaned;
    }

    /// <summary>短信（<c>SMSTO:号码:内容</c>，这是扫码器普遍支持的写法）。</summary>
    public static string Sms(SmsFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentException.ThrowIfNullOrWhiteSpace(fields.To);

        var number = new string(fields.To.Where(c => char.IsAsciiDigit(c) || c == '+').ToArray());
        ArgumentException.ThrowIfNullOrWhiteSpace(number);

        return string.IsNullOrEmpty(fields.Message)
            ? $"SMSTO:{number}"
            : $"SMSTO:{number}:{fields.Message}";
    }

    /// <summary>地理坐标（<c>geo:lat,lng</c>，带标签时另加 <c>?q=</c>）。</summary>
    public static string Geo(GeoPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);

        if (point.Latitude is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(point), "纬度必须在 -90 到 90 之间。");
        }

        if (point.Longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(point), "经度必须在 -180 到 180 之间。");
        }

        var lat = point.Latitude.ToString("0.######", CultureInfo.InvariantCulture);
        var lng = point.Longitude.ToString("0.######", CultureInfo.InvariantCulture);

        return string.IsNullOrWhiteSpace(point.Label)
            ? $"geo:{lat},{lng}"
            : $"geo:{lat},{lng}?q={lat},{lng}({Uri.EscapeDataString(point.Label)})";
    }

    private static bool HasScheme(string url)
    {
        var colon = url.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        for (var i = 0; i < colon; i++)
        {
            var c = url[i];
            var valid = char.IsAsciiLetter(c) || (i > 0 && (char.IsAsciiDigit(c) || c is '+' or '-' or '.'));
            if (!valid)
            {
                return false;
            }
        }

        return url.Length > colon + 2 && url[colon + 1] == '/' && url[colon + 2] == '/';
    }

    /// <summary>WiFi 语法要求转义 <c>\ ; , : "</c>。</summary>
    private static string EscapeWifi(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '\\' or ';' or ',' or ':' or '"')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>vCard 文本值转义：换行、<c>;</c>、<c>,</c>、<c>\</c>。</summary>
    private static string EscapeVCard(string value) => value
        .Replace("\\", "\\\\")
        .Replace("\r\n", "\\n")
        .Replace("\n", "\\n")
        .Replace(";", "\\;")
        .Replace(",", "\\,");
}
