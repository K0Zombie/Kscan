using Kscan.Core.Configuration;

namespace Kscan.Platform.Tests;

public sealed class CommandLauncherTests
{
    [Fact]
    public async Task Starts_a_program_with_quoted_arguments_and_working_directory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kscan command " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "command output.txt");
        try
        {
            var binding = new HotkeyBinding(HotkeyActionIds.RunCommand, "F8")
            {
                Command = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ComSpec")! : "/bin/sh",
                Arguments = OperatingSystem.IsWindows()
                    ? "/d /c \"cd > \"command output.txt\"\""
                    : "-c \"pwd > 'command output.txt'\"",
                WorkingDirectory = directory,
            };
            CommandLauncher.Start(binding);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(output) || new FileInfo(output).Length == 0)
                await Task.Delay(25, timeout.Token);
            Assert.Equal(directory, (await File.ReadAllTextAsync(output)).Trim());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Missing_program_reports_failure()
    {
        var binding = new HotkeyBinding(HotkeyActionIds.RunCommand, "F8") { Command = "kscan-missing-" + Guid.NewGuid().ToString("N") };
        Assert.Throws<System.ComponentModel.Win32Exception>(() => CommandLauncher.Start(binding));
    }

    /// <summary>
    /// 「以管理员身份运行」找不到程序时要给一句人话（而不是 Win32 错误码）。
    /// <para>
    /// 这里刻意用**不存在的程序**：ShellExecute 在弹 UAC 之前就会因为找不到文件失败，
    /// 所以这条用例不会真的弹出授权框，可以在无人值守下跑。
    /// </para>
    /// </summary>
    [Fact]
    public void Elevated_missing_program_reports_a_clear_error()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;   // Linux 走 pkexec，行为不同（这里不测）
        }

        var binding = new HotkeyBinding(HotkeyActionIds.RunCommand, "F8")
        {
            Command = "kscan-missing-" + Guid.NewGuid().ToString("N") + ".exe",
            RunAsAdmin = true,
        };

        var error = Assert.Throws<InvalidOperationException>(() => CommandLauncher.Start(binding));
        Assert.Contains("管理员", error.Message);
    }
}
