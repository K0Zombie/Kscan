using Kscan.App.Services;
using Kscan.Core.Imaging;

namespace Kscan.App.Tests;

/// <summary>
/// 「这整张图是不是没内容」的判据：PrintWindow 对桌面 / UWP 等窗口常返回**几乎全黑**的图，
/// 旧判据要求 100% 同色才认，结果"绝大部分黑 + 几个亮点"会漏过去 → 用户点桌面就截到黑屏。
/// 现在改成 512 采样、≥95% 同色即视为空白，触发回退到屏幕裁剪。
/// </summary>
public sealed class BlankFrameTests
{
    private static ImageFrame Frame(int width, int height, Func<int, int, int> pixel)
    {
        var pixels = new byte[width * height * ImageFrame.BytesPerPixel];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = pixel(x, y);
                var offset = ((y * width) + x) * ImageFrame.BytesPerPixel;
                pixels[offset] = (byte)(value & 0xFF);
                pixels[offset + 1] = (byte)((value >> 8) & 0xFF);
                pixels[offset + 2] = (byte)((value >> 16) & 0xFF);
                pixels[offset + 3] = 255;
            }
        }

        return new ImageFrame(width, height, pixels);
    }

    [Fact]
    public void Pure_black_is_blank() =>
        Assert.True(RegionSelectorWindow.IsNearlyBlank(Frame(64, 64, (_, _) => 0)));

    [Fact]
    public void Single_colour_is_blank() =>
        Assert.True(RegionSelectorWindow.IsNearlyBlank(Frame(64, 64, (_, _) => 0x00FF8040)));

    /// <summary>桌面黑屏的典型样子：绝大部分黑，只有少量亮点。</summary>
    [Fact]
    public void Mostly_black_with_a_few_specks_is_blank()
    {
        var frame = Frame(200, 200, (x, y) => x < 6 && y < 6 ? 0x00FFFFFF : 0);

        Assert.True(RegionSelectorWindow.IsNearlyBlank(frame));
    }

    /// <summary>真正的桌面画面 / 窗口内容：颜色多样，不能判成空白（否则会白白放弃整窗渲染）。</summary>
    [Fact]
    public void Varied_content_is_not_blank()
    {
        var frame = Frame(200, 200, (x, y) => ((x * 37) ^ (y * 91)) & 0xFFFFFF);

        Assert.False(RegionSelectorWindow.IsNearlyBlank(frame));
    }

    /// <summary>一半黑一半亮：也不是"没内容"。</summary>
    [Fact]
    public void Half_dark_half_bright_is_not_blank()
    {
        var frame = Frame(200, 200, (x, _) => x < 100 ? 0 : 0x00FFFFFF);

        Assert.False(RegionSelectorWindow.IsNearlyBlank(frame));
    }
}
