using Kscan.Core.Configuration;
using Kscan.Platform;

namespace Kscan.App.Services;

/// <summary>
/// 用户命令仓库：读写 <c>user/cmd/*.json</c>（一条命令一个文件，便于手工编辑与分享）。
/// 加载 / 保存 / 删除后触发 <see cref="Changed"/>，由热键服务重新注册。
/// </summary>
public sealed class UserCommandStore
{
    private readonly List<UserCommand> _commands = [];

    public static UserCommandStore Current { get; } = new();

    public IReadOnlyList<UserCommand> Commands => _commands;

    public string Directory => PlatformPaths.CommandsDirectory;

    public event EventHandler? Changed;

    /// <summary>扫描命令目录（坏文件跳过，不影响其它命令）。</summary>
    public void Reload()
    {
        _commands.Clear();

        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            foreach (var file in System.IO.Directory.GetFiles(Directory, "*.json"))
            {
                var command = UserCommand.TryParse(File.ReadAllText(file));
                if (command is null)
                {
                    continue;
                }

                var id = string.IsNullOrWhiteSpace(command.Id)
                    ? Path.GetFileNameWithoutExtension(file)
                    : command.Id;

                _commands.Add(command with { Id = SanitizeFileId(id) });
            }
        }
        catch (Exception)
        {
            // 目录读不到就当没有用户命令
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>保存一条命令（id 为空时生成）。返回保存后的命令。</summary>
    public UserCommand Save(UserCommand command)
    {
        var id = SanitizeFileId(command.Id);
        var saved = command with { Id = id, Name = string.IsNullOrWhiteSpace(command.Name) ? "新命令" : command.Name };

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Path.Combine(Directory, id + ".json"), saved.ToJson());
        }
        catch (Exception)
        {
            return saved;
        }

        var index = _commands.FindIndex(existing => string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _commands[index] = saved;
        }
        else
        {
            _commands.Add(saved);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    public void Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        try
        {
            var path = Path.Combine(Directory, SanitizeFileId(id) + ".json");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 删不掉就保留，下一次 Reload 会重新出现
        }

        _commands.RemoveAll(command => string.Equals(command.Id, id, StringComparison.OrdinalIgnoreCase));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// id 会直接当文件名用（<c>user/cmd/&lt;id&gt;.json</c>），所以只留 ASCII 字母 / 数字 / <c>-</c> / <c>_</c>：
    /// 手写 <c>"id": "../../evil"</c> 或带控制字符时，不会把文件写出命令目录、也不会撞上 <c>CON</c> 这类保留名。
    /// </summary>
    internal static string SanitizeFileId(string? id)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var ch in (id ?? string.Empty).Trim())
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var safe = builder.ToString().Trim('-');
        if (safe.Length > 48)
        {
            safe = safe[..48].TrimEnd('-');
        }

        if (safe.Length == 0 || IsReservedDeviceName(safe))
        {
            return Guid.NewGuid().ToString("N")[..12];
        }

        return safe;
    }

    /// <summary>Windows 保留设备名（<c>CON</c> / <c>NUL</c> / <c>COM1</c> …）不能当文件名。</summary>
    private static bool IsReservedDeviceName(string name) =>
        name.ToUpperInvariant() switch
        {
            "CON" or "PRN" or "AUX" or "NUL" => true,
            _ => name.Length == 4
                 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                     || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                 && char.IsAsciiDigit(name[3]),
        };

    /// <summary>把仓库里的命令转成热键绑定（只保留启用了且写了热键的）。</summary>
    public IEnumerable<HotkeyBinding> ToHotkeyBindings() =>
        _commands
            .Where(command => command.Enabled && !string.IsNullOrWhiteSpace(command.Hotkey))
            .Select(command => new HotkeyBinding(HotkeyActionIds.RunCommand, command.Hotkey!.Trim())
            {
                Command = command.Program,
                Arguments = command.Arguments,
                WorkingDirectory = command.WorkingDirectory,
                RunAsAdmin = command.RunAsAdmin,
            });
}
