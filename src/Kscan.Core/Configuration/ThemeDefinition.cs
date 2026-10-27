using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kscan.Core.Configuration;

/// <summary>主题颜色。值用 <c>#RRGGBB</c> 或 <c>#AARRGGBB</c>；解析失败时 GUI 用兜底色。</summary>
public sealed record ThemeColors
{
    public string Background { get; init; } = "#1E1E1E";

    public string Foreground { get; init; } = "#F0F0F0";

    public string MutedForeground { get; init; } = "#99FFFFFF";

    public string Accent { get; init; } = "#0078D4";

    public string Panel { get; init; } = "#1F808080";

    public string Border { get; init; } = "#33FFFFFF";

    public string Warning { get; init; } = "#E6A23C";

    public string Success { get; init; } = "#67C23A";

    public string Danger { get; init; } = "#F56C6C";

    public string Selection { get; init; } = "#330078D4";
}

/// <summary>
/// 截图遮罩的样式取值（写进主题 JSON 的 <c>mask.style</c>）。
/// </summary>
public static class ThemeMaskStyles
{
    /// <summary>整屏铺一层 <c>mask.color</c>：框选时桌面变暗（<b>不是默认</b>）。</summary>
    public const string Dim = "dim";

    /// <summary>不铺遮罩（<b>默认</b>）：直接看原图，只画选框与高亮框。</summary>
    public const string None = "none";

    /// <summary>下拉与文档共用的取值清单（默认项在前）。</summary>
    public static IReadOnlyList<string> All { get; } = [None, Dim];

    /// <summary>把任意输入收敛到已知取值：只认 <c>dim</c>，其余（含缺省）一律当 <c>none</c>。</summary>
    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Dim, StringComparison.OrdinalIgnoreCase) ? Dim : None;

    public static bool IsNone(string? value) => Normalize(value) == None;
}

/// <summary>截图遮罩（框选时铺在屏幕上的那层）：颜色 + 样式。</summary>
public sealed record ThemeMask
{
    public string Color { get; init; } = "#A0000000";

    /// <summary>见 <see cref="ThemeMaskStyles"/>：<c>none</c>（默认）/ <c>dim</c>。</summary>
    public string Style { get; init; } = ThemeMaskStyles.None;
}

/// <summary>
/// 一套主题。JSON 存在 <c>user/themes/&lt;id&gt;.json</c>，字段全部可缺省（缺省 = 深色默认值）。
/// <para>
/// 只描述"外观"，不含行为设置 —— 按键、语言这类是 <see cref="AppSettings"/> 的事。
/// </para>
/// </summary>
public sealed record ThemeDefinition
{
    /// <summary>
    /// 主题 id（文件名即 id；内置为 <c>dark</c> / <c>light</c>）。文件里可以省略，
    /// 省略时由主题目录用**文件名**补上（见 <see cref="TryParse(string, string?)"/>）。
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>界面上显示的名字；文件里省略时用 id 兜底。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>基础明暗：<c>dark</c> / <c>light</c>（决定 Fluent 控件的基础主题）。</summary>
    public string Base { get; init; } = "dark";

    public ThemeColors Colors { get; init; } = new();

    public ThemeMask Mask { get; init; } = new();

    /// <summary>面板 / 卡片的圆角。</summary>
    public double CornerRadius { get; init; } = 8;

    /// <summary>预览框圆角。</summary>
    public double PreviewCornerRadius { get; init; } = 4;

    /// <summary>自定义字体家族（留空 = 跟随系统）。</summary>
    public string? FontFamily { get; init; }

    public static ThemeDefinition Dark { get; } = new()
    {
        Id = "dark",
        Name = "深色",
        Base = "dark",
    };

    public static ThemeDefinition Light { get; } = new()
    {
        Id = "light",
        Name = "浅色",
        Base = "light",
        Colors = new ThemeColors
        {
            Background = "#FAFAFA",
            Foreground = "#1E1E1E",
            MutedForeground = "#99000000",
            Accent = "#0078D4",
            Panel = "#14000000",
            Border = "#22000000",
            Warning = "#B7791F",
            Success = "#2F855A",
            Danger = "#C53030",
            Selection = "#220078D4",
        },
    };

    public static IReadOnlyList<ThemeDefinition> BuiltIn { get; } = [Dark, Light];

    /// <summary>设置里表示"跟随系统"的 id（不写进主题文件）。</summary>
    public const string SystemThemeId = "system";

    /// <summary>内置主题改名另存时用的默认 id（覆盖它不会污染内置主题）。</summary>
    public const string CustomThemeId = "custom";

    /// <summary>圆角上限：再大就变成圆形，卡片会互相挤。</summary>
    public const double MaxCornerRadius = 32;

    /// <summary>内置主题的 id（<c>dark</c> / <c>light</c>）：没有对应文件，只能另存。</summary>
    public static bool IsBuiltInId(string? id) =>
        id is not null && BuiltIn.Any(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 保留 id：设置里的"跟随系统"（用户文件里写它会被忽略）+ 内置主题的 id
    /// （用户文件可以用同名 id **覆盖**内置那套；<see cref="SuggestId"/> 不会生成这些 id）。
    /// </summary>
    public static bool IsReservedId(string? id) =>
        string.Equals(id, SystemThemeId, StringComparison.OrdinalIgnoreCase) || IsBuiltInId(id);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// 宽松解析：损坏的 JSON / 未知字段 / 缺字段都不抛异常（返回 null 表示文件坏了）。
    /// <para>
    /// <paramref name="fallbackId"/> 是文件里没写 <c>id</c> 时的兜底 —— 主题目录传文件名，
    /// 这样"文件名去掉后缀就是 id"这条约定才成立。
    /// </para>
    /// </summary>
    public static ThemeDefinition? TryParse(string json, string? fallbackId = null)
    {
        try
        {
            var theme = JsonSerializer.Deserialize<ThemeDefinition>(json, JsonOptions);
            if (theme is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(theme.Id) && !string.IsNullOrWhiteSpace(fallbackId))
            {
                theme = theme with { Id = fallbackId.Trim() };
            }

            return theme.Normalized();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// 收敛到可用范围：空 id / 空名字有兜底，圆角非负有限并夹到
    /// <see cref="MaxCornerRadius"/>，<c>base</c> 只认 <c>light</c>，遮罩样式只认已知取值。
    /// <para>
    /// 手写 / 手改的主题文件什么都可能写进来（<c>-5</c>、<c>NaN</c>、<c>"style": "darken"</c>），
    /// 一律在这里收敛，免得渲染层拿到非法值直接抛异常、整个界面起不来。
    /// </para>
    /// </summary>
    public ThemeDefinition Normalized()
    {
        var id = SanitizeId(string.IsNullOrWhiteSpace(Id) ? CustomThemeId : Id);
        var name = string.IsNullOrWhiteSpace(Name) ? id : Name.Trim();

        return this with
        {
            Id = id,
            Name = name,
            Base = string.Equals(Base?.Trim(), "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark",
            CornerRadius = ClampRadius(CornerRadius, Dark.CornerRadius),
            PreviewCornerRadius = ClampRadius(PreviewCornerRadius, Dark.PreviewCornerRadius),
            FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? null : FontFamily.Trim(),
            Mask = (Mask ?? new ThemeMask()) with { Style = ThemeMaskStyles.Normalize(Mask?.Style) },
        };
    }

    /// <summary>
    /// 主题 id 会直接当**文件名**用（<c>user/themes/&lt;id&gt;.json</c>），所以要挡住"非法字符"：
    /// 路径分隔符、控制字符、Windows 不允许的字符一律换成 <c>-</c>；<c>.</c> / <c>..</c> / 全空则退回
    /// <see cref="CustomThemeId"/>。否则一个手写的 <c>"id": "../../evil"</c> 就能把文件写出主题目录。
    /// </summary>
    public static string SanitizeId(string? id)
    {
        var raw = id?.Trim() ?? string.Empty;
        var builder = new System.Text.StringBuilder(raw.Length);

        foreach (var ch in raw)
        {
            if (char.IsControl(ch) || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                builder.Append('-');
            }
            else
            {
                builder.Append(ch);
            }
        }

        var safe = builder.ToString().Trim(' ', '.', '-');
        if (safe.Length == 0)
        {
            return CustomThemeId;
        }

        return safe.Length > 64 ? safe[..64].TrimEnd(' ', '.', '-') : safe;
    }

    /// <summary>复制出一套新主题：id / 名字换掉，其余外观原样带走。</summary>
    public ThemeDefinition CopyAs(string id, string name) =>
        (this with { Id = id, Name = name }).Normalized();

    /// <summary>
    /// 由名字生成一个安全的 id（文件名去掉后缀就是 id）：只保留 ASCII 字母数字与 <c>-</c>，
    /// 其余（中文、空格、符号）转成 <c>-</c>；全被吃光或撞上保留字时退化成 <c>theme</c>，
    /// 再与 <paramref name="taken"/> 去重（<c>theme</c> / <c>theme-2</c> / <c>theme-3</c>）。
    /// </summary>
    public static string SuggestId(string? name, IEnumerable<string>? taken = null)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var ch in (name ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var id = builder.ToString().Trim('-');
        if (id.Length > 32)
        {
            id = id[..32].TrimEnd('-');
        }

        if (id.Length == 0 || IsReservedId(id))
        {
            id = "theme";
        }

        var existing = new HashSet<string>(
            (taken ?? []).Where(item => !string.IsNullOrWhiteSpace(item)),
            StringComparer.OrdinalIgnoreCase);

        if (!existing.Contains(id))
        {
            return id;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{id}-{suffix}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static double ClampRadius(double value, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, MaxCornerRadius) : fallback;
}
