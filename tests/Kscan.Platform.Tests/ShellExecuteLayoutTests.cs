using Kscan.Platform.Windows;

namespace Kscan.Platform.Tests;

/// <summary>
/// ShellExecuteEx 的结构体布局护栏：少写 union 字段时 cbSize 会偏小，
/// ShellExecuteEx 直接返回失败（UAC 都不弹）—— 表现为「点以管理员身份重启没反应」。
/// </summary>
public class ShellExecuteLayoutTests
{
    [Fact]
    public void Shell_execute_info_matches_the_win32_layout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var expected = IntPtr.Size == 8 ? 112 : 60;
        Assert.Equal(expected, WindowsElevation.ShellExecuteInfoSize);
    }
}
