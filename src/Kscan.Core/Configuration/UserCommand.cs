using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kscan.Core.Configuration;

/// <summary>
/// 用户命令（「执行命令」子页的一条）：存在 <c>user/cmd/&lt;id&gt;.json</c>。
/// <para>
/// 与 <see cref="AppSettings.Hotkeys"/> 里旧的 <c>runCommand</c> 行兼容：两者都能绑热键，
/// 新界面只编辑这里的文件；旧的 <c>runCommand</c> 行仍会被注册（迁移工具在设置页）。
/// </para>
/// </summary>
public sealed record UserCommand
{
    /// <summary>唯一 id（通常等于文件名）。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>界面上显示的名字。</summary>
    public string Name { get; init; } = "新命令";

    /// <summary>程序路径或名称（不加外层引号）。</summary>
    public string Program { get; init; } = string.Empty;

    public string? Arguments { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>全局热键写法（例如 <c>Ctrl+Alt+K</c>）；留空 = 不绑。</summary>
    public string? Hotkey { get; init; }

    /// <summary>
    /// 以<b>管理员身份</b>运行（默认否）。Windows 上每次触发都会弹 UAC 授权框；
    /// Linux 上走 <c>pkexec</c>（Polkit），没装 polkit 时启动会报错。
    /// </summary>
    public bool RunAsAdmin { get; init; }

    public bool Enabled { get; init; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static UserCommand? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<UserCommand>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// 下拉框（没有 ItemTemplate 时）显示的就是它：<c>名称 (热键)</c>。
    /// 已经命名的命令只需要一眼看出"叫什么、绑了哪个键"；细节（程序 / 参数 / 目录）选中后看下面的表单。
    /// "（管理员）"这类文案要跟随界面语言，属于 GUI 的事，不进 Core。
    /// </summary>
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Hotkey) ? Name : $"{Name}  ({Hotkey.Trim()})";
}
