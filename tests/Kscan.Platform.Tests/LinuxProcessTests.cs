using Kscan.Platform.Linux;

namespace Kscan.Platform.Tests;

public class LinuxProcessTests
{
    [Theory]
    [InlineData("return 1")]
    [InlineData("return 0")]
    public async Task Cancelled_or_empty_selection_never_invokes_grim(string selector)
    {
        if (!OperatingSystem.IsLinux()) return;
        var command = "slurp() { " + selector + "; }; grim() { printf 'UNEXPECTED_SCREENSHOT'; }; "
            + LinuxScreenCapture.DefaultRegionCommand;
        var result = await ShellRunner.RunAsync(command);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
    }

    [Fact]
    public async Task Cancelling_shell_command_terminates_its_children_and_pipes()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ShellRunner.RunAsync("sleep 30 & wait", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Invalid_custom_screenshot_returns_a_readable_error()
    {
        if (!OperatingSystem.IsLinux()) return;
        var capture = new LinuxScreenCapture("printf 'invalid-png'");
        Assert.Null(await capture.CaptureRegionAsync());
        Assert.Contains("不是有效图片", capture.LastError);
    }
}
