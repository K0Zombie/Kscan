using System.Text;
using System.Text.Json;

namespace Kscan.Core.Model;

/// <summary>识别出来的内容属于哪一类（决定界面展示方式与可用的一键动作）。</summary>
public enum ResultContentKind
{
    /// <summary>普通文本。</summary>
    Text,

    /// <summary>http / https 链接。</summary>
    Url,

    /// <summary>WiFi 配置（<c>WIFI:</c> 语法）。</summary>
    Wifi,

    /// <summary>vCard 联系人。</summary>
    VCard,

    /// <summary>邮件（<c>mailto:</c>）。</summary>
    Email,

    /// <summary>电话（<c>tel:</c>）。</summary>
    Phone,

    /// <summary>短信（<c>SMSTO:</c> / <c>sms:</c>）。</summary>
    Sms,

    /// <summary>地理坐标（<c>geo:</c>）。</summary>
    Geo,

    /// <summary>JSON payload（很多小程序码里是这个）。</summary>
    Json,

    /// <summary>看起来是 Base64，且能解出可读文本。</summary>
    Base64,
}

/// <summary>
/// 对识别结果做内容识别，供界面显示"这是什么"并提供对应的一键动作。
/// 与生成侧的 <see cref="BarcodeContent"/> 相对。
/// </summary>
public sealed record ResultContent(
    ResultContentKind Kind,
    string Raw,
    string? Title = null,
    string? ActionLabel = null,
    string? ActionTarget = null)
{
    /// <summary>结果窗口的标题文案。</summary>
    public string KindLabel => Kind switch
    {
        ResultContentKind.Url => "链接",
        ResultContentKind.Wifi => "WiFi 配置",
        ResultContentKind.VCard => "联系人",
        ResultContentKind.Email => "邮件地址",
        ResultContentKind.Phone => "电话号码",
        ResultContentKind.Sms => "短信",
        ResultContentKind.Geo => "位置坐标",
        ResultContentKind.Json => "JSON",
        ResultContentKind.Base64 => "Base64 文本",
        _ => "文本",
    };

    /// <summary>是否有可执行的一键动作。</summary>
    public bool HasAction => !string.IsNullOrEmpty(ActionTarget);

    /// <summary>识别一段文本属于哪一类。</summary>
    public static ResultContent Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ResultContent(ResultContentKind.Text, text ?? string.Empty);
        }

        var trimmed = text.Trim();

        if (StartsWith(trimmed, "WIFI:"))
        {
            return ParseWifi(text, trimmed);
        }

        if (StartsWith(trimmed, "BEGIN:VCARD"))
        {
            return ParseVCard(text, trimmed);
        }

        if (StartsWith(trimmed, "mailto:"))
        {
            var address = trimmed[7..].Split('?')[0];
            return new ResultContent(ResultContentKind.Email, text, address, "写邮件", trimmed);
        }

        if (StartsWith(trimmed, "tel:"))
        {
            return new ResultContent(ResultContentKind.Phone, text, trimmed[4..], "拨打", trimmed);
        }

        if (StartsWith(trimmed, "SMSTO:") || StartsWith(trimmed, "sms:"))
        {
            return ParseSms(text, trimmed);
        }

        if (StartsWith(trimmed, "geo:"))
        {
            var body = trimmed[4..].Split('?')[0];
            return new ResultContent(ResultContentKind.Geo, text, body, "在地图中查看", trimmed);
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return new ResultContent(ResultContentKind.Url, text, uri.Host, "打开链接", trimmed);
        }

        if (LooksLikeJson(trimmed))
        {
            return new ResultContent(ResultContentKind.Json, text, "JSON 数据", null, null);
        }

        if (TryDecodeBase64(trimmed, out var decoded))
        {
            var preview = decoded.Length > 60 ? decoded[..60] + "…" : decoded;
            return new ResultContent(ResultContentKind.Base64, text, preview, "查看解码结果", decoded);
        }

        return new ResultContent(ResultContentKind.Text, text);
    }

    private static ResultContent ParseWifi(string raw, string trimmed)
    {
        var fields = ParseKeyValuePairs(trimmed[5..]);

        var ssid = fields.GetValueOrDefault("S");
        var security = fields.GetValueOrDefault("T") ?? "WPA";
        var hidden = string.Equals(fields.GetValueOrDefault("H"), "true", StringComparison.OrdinalIgnoreCase);

        var summary = ssid is null
            ? security
            : $"{ssid}（{security}{(hidden ? "，隐藏" : "")}）";

        return new ResultContent(ResultContentKind.Wifi, raw, summary, null, null);
    }

    private static ResultContent ParseVCard(string raw, string trimmed)
    {
        string? name = null;
        string? phone = null;

        foreach (var line in trimmed.Split('\n'))
        {
            var value = line.Trim();
            if (name is null && value.StartsWith("FN:", StringComparison.OrdinalIgnoreCase))
            {
                name = value[3..].Trim();
            }
            else if (phone is null && value.StartsWith("TEL", StringComparison.OrdinalIgnoreCase))
            {
                var colon = value.IndexOf(':');
                if (colon > 0)
                {
                    phone = value[(colon + 1)..].Trim();
                }
            }
        }

        var title = name is null ? "联系人" : phone is null ? name : $"{name}（{phone}）";
        return new ResultContent(ResultContentKind.VCard, raw, title, null, null);
    }

    private static ResultContent ParseSms(string raw, string trimmed)
    {
        var body = StartsWith(trimmed, "SMSTO:") ? trimmed[6..] : trimmed[4..];
        var parts = body.Split(':', 2);

        var number = parts[0].Split('?')[0];
        var message = parts.Length > 1 ? parts[1] : null;
        var title = message is null ? number : $"{number}：{Trim(message, 40)}";

        return new ResultContent(ResultContentKind.Sms, raw, title, "发短信", $"SMSTO:{number}");
    }

    /// <summary>
    /// 解析 WiFi 语法的键值对：字段之间用未转义的 <c>;</c> 分隔，
    /// 值里的 <c>\; \, \: \" \\</c> 是转义序列。
    /// </summary>
    private static Dictionary<string, string> ParseKeyValuePairs(string body)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var key = new StringBuilder();
        var value = new StringBuilder();
        var inValue = false;
        var escaped = false;

        foreach (var c in body)
        {
            if (escaped)
            {
                (inValue ? value : key).Append(c);
                escaped = false;
                continue;
            }

            switch (c)
            {
                case '\\':
                    escaped = true;
                    break;
                case ':' when !inValue:
                    inValue = true;
                    break;
                case ';':
                    if (key.Length > 0)
                    {
                        fields[key.ToString()] = value.ToString();
                    }

                    key.Clear();
                    value.Clear();
                    inValue = false;
                    break;
                default:
                    (inValue ? value : key).Append(c);
                    break;
            }
        }

        return fields;
    }

    private static bool LooksLikeJson(string text)
    {
        if (text.Length < 2 || (text[0] != '{' && text[0] != '['))
        {
            return false;
        }

        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 宽松的 Base64 判定：长度够、字符集合法、能解码，且解出来的内容是"看起来像文本"的。
    /// 这样能避免把普通长文本误判成 Base64。
    /// </summary>
    private static bool TryDecodeBase64(string text, out string decoded)
    {
        decoded = string.Empty;

        if (text.Length < 8 || text.Length % 4 != 0)
        {
            return false;
        }

        foreach (var c in text)
        {
            var ok = char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=';
            if (!ok)
            {
                return false;
            }
        }

        // 必须同时含大小写或数字/符号，纯字母单词（如 password）不该被当成 Base64
        if (!(text.Any(char.IsAsciiLetterUpper) || text.Any(char.IsAsciiDigit)) || !text.Contains('=') && !text.Any(c => c is '+' or '/'))
        {
            // 没有 '=' 也没有 '+'/'/' 时，要求长度至少 16 且含大写（降低误判）
            if (text.Length < 16 || !text.Any(char.IsAsciiLetterUpper))
            {
                return false;
            }
        }

        try
        {
            var bytes = Convert.FromBase64String(text);
            if (bytes.Length == 0)
            {
                return false;
            }

            var candidate = new UTF8Encoding(false, true).GetString(bytes);
            if (candidate.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            {
                return false;
            }

            decoded = candidate;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool StartsWith(string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static string Trim(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
