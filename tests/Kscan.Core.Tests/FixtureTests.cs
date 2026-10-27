using System.Runtime.CompilerServices;
using Kscan.Core.Imaging;
using Kscan.Core.Pipeline;

namespace Kscan.Core.Tests;

/// <summary>
/// 端到端校验签入仓库的图片夹具（由 <c>kscan gen</c> 生成），
/// 覆盖"从磁盘读 PNG → 识别 → 取回原文"的完整链路。
/// </summary>
public class FixtureTests
{
    public static TheoryData<string, string> Fixtures => new()
    {
        { "qr-hello.png", "https://example.com/qrscan-fixture" },
        { "qr-chinese.png", "扫码器中文夹具 v1" },
        { "qr-filepath.png", @"C:\Users\demo\报告 2026.pdf" },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Checked_in_fixture_decodes_to_expected_text(string fileName, string expected)
    {
        var path = ResolveFixture(fileName);
        Assert.True(File.Exists(path), $"缺少测试夹具：{path}");

        var outcome = ScanPipeline.CreateDefault().Scan(SkiaImageCodec.Load(path));

        Assert.True(outcome.Success);
        Assert.Equal(expected, outcome.FirstText);
        Assert.Equal("zxing", outcome.Results[0].EngineId);
    }

    /// <summary>优先取输出目录中的副本，回退到源码目录（避免依赖 MSBuild 复制行为的差异）。</summary>
    private static string ResolveFixture(string fileName, [CallerFilePath] string callerFile = "")
    {
        var copied = Path.Combine(AppContext.BaseDirectory, "assets", fileName);
        if (File.Exists(copied))
        {
            return copied;
        }

        var projectDirectory = Path.GetDirectoryName(callerFile)!;
        return Path.Combine(projectDirectory, "assets", fileName);
    }
}
