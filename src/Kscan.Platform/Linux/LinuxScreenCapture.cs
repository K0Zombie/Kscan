using Kscan.Core.Imaging;
using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Linux;

/// <summary>
/// Linux/Wayland 屏幕采集：**不能**直接读帧缓冲，改为委托外部截图工具。
/// <para>
/// 默认 <c>grim</c> + <c>slurp</c>：niri 默认配置就是这条管线，且 niri 不支持 portal 的
/// <c>InteractiveScreenshot</c>，所以委托命令是唯一靠谱的路线。
/// 命令可配置（设置页里换 flameshot / spectacle / gnome-screenshot 都行）。
/// </para>
/// </summary>
internal sealed class LinuxScreenCapture : IScreenCapture
{
    /// <summary>默认框选命令：slurp 选区 → grim 抓该区域 → PNG 到 stdout。</summary>
    public const string DefaultRegionCommand = """region=$(slurp) || exit $?; [ -n "$region" ] || exit 1; exec grim -g "$region" -""";

    /// <summary>默认全屏命令。</summary>
    public const string DefaultDesktopCommand = "grim -";

    private readonly string _regionCommand;
    private readonly string _desktopCommand;

    public LinuxScreenCapture(string? regionCommand = null, string? desktopCommand = null)
    {
        _regionCommand = string.IsNullOrWhiteSpace(regionCommand) ? DefaultRegionCommand : regionCommand;
        _desktopCommand = string.IsNullOrWhiteSpace(desktopCommand) ? DefaultDesktopCommand : desktopCommand;
    }

    public bool IsSupported => OperatingSystem.IsLinux() && ShellRunner.Exists("sh")
        && (_regionCommand != DefaultRegionCommand || _desktopCommand != DefaultDesktopCommand || ShellRunner.Exists("grim"));

    public string Description => IsSupported ? $"外部命令：{_regionCommand}" : "截图不可用：需要 /bin/sh 和 grim（框选还需 slurp）。";

    public string? DescriptionKey => IsSupported ? "Platform.Capture.LinuxCommand" : null;

    public object?[] DescriptionArguments => [_regionCommand];

    /// <summary>最近一次失败的原因（界面上用来提示"缺少 grim/slurp"之类）。</summary>
    public string? LastError { get; private set; }

    public async Task<ImageFrame?> CaptureRegionAsync(CancellationToken cancellationToken = default) =>
        await RunAsync(_regionCommand, cancellationToken).ConfigureAwait(false);

    public async Task<ImageFrame?> CaptureDesktopAsync(CancellationToken cancellationToken = default) =>
        await RunAsync(_desktopCommand, cancellationToken).ConfigureAwait(false);

    /// <summary>当前环境是否具备默认命令所需的工具。</summary>
    public static bool HasDefaultTools() => ShellRunner.Exists("grim") && ShellRunner.Exists("slurp");

    private async Task<ImageFrame?> RunAsync(string command, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            LastError = "当前不是 Linux。";
            return null;
        }

        if (command == DefaultRegionCommand && !HasDefaultTools())
        {
            LastError = "框选截图需要可执行的 grim 和 slurp，请先安装这两个工具。";
            return null;
        }
        if (command == DefaultDesktopCommand && !ShellRunner.Exists("grim"))
        {
            LastError = "全屏截图需要可执行的 grim，请先安装 grim。";
            return null;
        }

        var result = await ShellRunner.RunAsync(command, cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0 || result.StandardOutput.Length == 0)
        {
            // 用户在 slurp 里按 Esc 取消会得到非 0 退出码，属于正常操作，不算错误
            LastError = result.StandardError.Trim() is { Length: > 0 } message
                ? message
                : result.ExitCode == 0
                    ? "截图工具没有输出图像。"
                    : $"命令以退出码 {result.ExitCode} 结束（若是手动取消可忽略）。";
            return null;
        }

        try
        {
            LastError = null;
            return SkiaImageCodec.FromEncoded(result.StandardOutput);
        }
        catch (InvalidDataException ex)
        {
            LastError = $"截图工具的输出不是有效图片：{ex.Message}";
            return null;
        }
    }
}
