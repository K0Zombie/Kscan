using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kscan.Core.Configuration;

public sealed record NamingContext(string Type, DateTime Time, int Width = 0, int Height = 0,
    long Counter = 1, string Title = "", string Extension = "");

/// <summary>Pure template engine. Custom token resolver is an explicit programmer extension point.</summary>
public static class NameTemplate
{
    public static string Render(string template, NamingContext context, bool fileName = true,
        Func<string, NamingContext, string?>? resolveToken = null)
    {
        if (template.Length > 2048) throw new FormatException("模板不能超过2048个字符。");
        var output = new StringBuilder();
        for (var i = 0; i < template.Length;)
        {
            if (template.AsSpan(i).StartsWith("{{")) { output.Append('{'); i += 2; continue; }
            if (template.AsSpan(i).StartsWith("}}")) { output.Append('}'); i += 2; continue; }
            if (template[i] == '}') throw new FormatException("多余的 }，文字花括号请写 }}。");
            if (template[i] != '{') { output.Append(template[i++]); continue; }
            var end = template.IndexOf('}', i + 1);
            if (end < 0) throw new FormatException("模板缺少 }。");
            var expression = template[(i + 1)..end];
            if (expression.Contains('{')) throw new FormatException("表达式不能嵌套花括号。");
            output.Append(Evaluate(expression, context, resolveToken)); i = end + 1;
        }
        var result = output.ToString();
        return fileName ? Sanitize(result) : result;
    }

    private static string Evaluate(string expression, NamingContext context, Func<string, NamingContext, string?>? resolver)
    {
        if (expression.StartsWith("if:", StringComparison.Ordinal))
        {
            var question = expression.IndexOf('?');
            var colon = expression.IndexOf(':', question + 1);
            var equal = expression.IndexOf('=');
            if (question < 0 || colon < 0 || equal < 4 || equal > question)
                throw new FormatException("条件写法：{if:type=gif?animation:recording}。");
            var actual = Value(expression[3..equal], null, context, resolver);
            return string.Equals(actual, expression[(equal + 1)..question], StringComparison.OrdinalIgnoreCase)
                ? expression[(question + 1)..colon] : expression[(colon + 1)..];
        }
        var parts = expression.Split('|');
        var token = parts[0].Split(':', 2);
        var value = Value(token[0], token.Length == 2 ? token[1] : null, context, resolver);
        foreach (var part in parts.Skip(1))
        {
            var operation = part.Split(':', 2);
            value = operation[0] switch
            {
                "lower" => value.ToLowerInvariant(), "upper" => value.ToUpperInvariant(), "trim" => value.Trim(),
                "slug" => Regex.Replace(value.Trim(), @"[^\p{L}\p{Nd}_-]+", "-", RegexOptions.None, TimeSpan.FromMilliseconds(100)).Trim('-'),
                "default" when operation.Length == 2 => string.IsNullOrWhiteSpace(value) ? operation[1] : value,
                "truncate" when operation.Length == 2 && int.TryParse(operation[1], out var length) && length is > 0 and <= 200
                    => value[..Math.Min(length, value.Length)],
                _ => throw new FormatException("未知处理规则：" + part)
            };
        }
        return value;
    }

    private static string Value(string token, string? format, NamingContext c, Func<string, NamingContext, string?>? resolver) => token switch
    {
        "date" => c.Time.ToString(format ?? "yyyyMMdd", CultureInfo.InvariantCulture),
        "time" => c.Time.ToString(format ?? "HHmmss", CultureInfo.InvariantCulture),
        "datetime" => c.Time.ToString(format ?? "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
        "counter" => c.Counter.ToString(format ?? "0", CultureInfo.InvariantCulture),
        "type" => c.Type, "width" => c.Width.ToString(CultureInfo.InvariantCulture),
        "height" => c.Height.ToString(CultureInfo.InvariantCulture), "title" => c.Title, "ext" => c.Extension,
        _ => resolver?.Invoke(token, c) ?? throw new FormatException("未知变量：" + token)
    };

    public static string Sanitize(string value)
    {
        var invalid = "<>:\"/\\|?*";
        var text = new string(value.Select(c => c < 32 || invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(text)) text = "untitled";
        if (Regex.IsMatch(text.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)) text = "_" + text;
        return text[..Math.Min(180, text.Length)];
    }
}
