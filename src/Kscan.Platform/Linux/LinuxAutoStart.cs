using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Linux;

/// <summary>
/// Linux 开机自启：写 <c>~/.config/autostart/kscan.desktop</c>（XDG autostart 规范）。
/// niri 通过 <c>niri-session</c> 启动时会读这个目录。
/// </summary>
internal sealed class LinuxAutoStart : IAutoStart
{
    private const string FileName = "kscan.desktop";

    public bool IsSupported => OperatingSystem.IsLinux();

    public string Description => "~/.config/autostart/kscan.desktop";

    public string? DescriptionKey => "Platform.AutoStart.Linux";

    public static string DesktopFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config",
        "autostart",
        FileName);

    public bool IsEnabled()
    {
        if (!IsSupported)
        {
            return false;
        }

        try
        {
            return File.Exists(DesktopFilePath);
        }
        catch (IOException)
        {
            return false;
        }
    }

    public Task<OperationResult> SetEnabledAsync(bool enabled)
    {
        if (!IsSupported)
        {
            return Task.FromResult(OperationResult.Fail("当前平台不支持。"));
        }

        return Task.Run(() =>
        {
            try
            {
                if (!enabled)
                {
                    if (File.Exists(DesktopFilePath))
                    {
                        File.Delete(DesktopFilePath);
                    }

                    return OperationResult.Ok();
                }

                var executable = Environment.ProcessPath;
                if (string.IsNullOrEmpty(executable))
                {
                    return OperationResult.Fail("无法确定程序自身路径。");
                }

                var directory = Path.GetDirectoryName(DesktopFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(DesktopFilePath, $"""
                    [Desktop Entry]
                    Type=Application
                    Name=Kscan
                    Comment=二维码/条码扫描与生成
                    Exec="{executable}"
                    Terminal=false
                    X-GNOME-Autostart-enabled=true
                    """);

                return OperationResult.Ok();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return OperationResult.Fail(ex.Message);
            }
        });
    }
}
