namespace Kscan.Platform;

/// <summary>
/// 各平台的配置目录。只做路径计算，不创建目录、不碰文件 —— 创建由写入方负责。
/// </summary>
public static class PlatformPaths
{
    public static bool IsPortable { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable"));

    /// <summary>
    /// 单实例通道名：<b>同一用户全局唯一</b>，不按配置目录区分。
    /// <para>
    /// 曾经按配置目录哈希隔离（便携副本各跑各的），但那会让用户在两个目录里各开一个 kscan：
    /// 全局热键互相抢、贴图与剪贴板交错，体验很乱。现在第二份启动后只把命令转发给已运行的实例，
    /// 然后自己退出（见 <c>Program.Main</c>），从系统层面保证"同时只有一个 kscan"。
    /// </para>
    /// </summary>
    public static string InstanceName => "kscan-" + Environment.UserName;
    /// <summary>
    /// 配置目录：
    /// Windows <c>%APPDATA%\kscan</c>；Linux <c>$XDG_CONFIG_HOME/kscan</c>（默认 <c>~/.config/kscan</c>）；
    /// macOS <c>~/Library/Application Support/kscan</c>。
    /// </summary>
    public static string ConfigDirectory { get; } = BuildConfigDirectory();

    /// <summary>配置文件：<c>&lt;配置目录&gt;/settings.json</c>。</summary>
    public static string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    /// <summary>用户数据目录：<c>&lt;配置目录&gt;/user</c>（主题 / 命令 / 方法都放这里）。</summary>
    public static string UserDirectory => Path.Combine(ConfigDirectory, "user");

    /// <summary>用户主题目录：<c>user/themes/*.json</c>。</summary>
    public static string ThemesDirectory => Path.Combine(UserDirectory, "themes");

    /// <summary>用户命令目录：<c>user/cmd/*.json</c>。</summary>
    public static string CommandsDirectory => Path.Combine(UserDirectory, "cmd");

    /// <summary>用户处理方法目录：<c>user/method/*.json</c>。</summary>
    public static string MethodsDirectory => Path.Combine(UserDirectory, "method");

    private static string BuildConfigDirectory()
    {
        if (IsPortable) return Path.Combine(AppContext.BaseDirectory, "data");
        // .NET 自己就把 ApplicationData 映射成各平台惯例：
        //   Windows → %APPDATA%；Unix → $XDG_CONFIG_HOME（未设则为 ~/.config）
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(root))
            {
                root = Directory.GetCurrentDirectory();
            }
        }

        var current = Path.Combine(root, "kscan");
        MigrateLegacyDirectory(Path.Combine(root, "qscan"), current);
        return current;
    }

    /// <summary>
    /// 从旧版 qscan 配置目录做一次性迁移（只在 kscan 目录还不存在时）：复制 settings.json
    /// 与将来的 user 子目录（themes/cmd/method），不覆盖已有文件。失败不影响启动。
    /// </summary>
    private static void MigrateLegacyDirectory(string legacy, string current)
    {
        try
        {
            if (Directory.Exists(current) || !Directory.Exists(legacy))
            {
                return;
            }

            Directory.CreateDirectory(current);

            foreach (var file in Directory.GetFiles(legacy))
            {
                File.Copy(file, Path.Combine(current, Path.GetFileName(file)), overwrite: false);
            }

            foreach (var directory in Directory.GetDirectories(legacy))
            {
                CopyDirectory(directory, Path.Combine(current, Path.GetFileName(directory)));
            }
        }
        catch (Exception)
        {
            // 迁移失败不阻塞启动：用户仍会拿到默认配置
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
