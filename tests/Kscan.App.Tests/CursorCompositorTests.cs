using Kscan.App.Services;
using Kscan.Core.Imaging;

namespace Kscan.App.Tests;

/// <summary>
/// "截图带光标"的像素合成：光标按 alpha 混合到截图里，越界部分忽略，原帧不能被改。
/// </summary>
public sealed class CursorCompositorTests
{
    [Fact]
    public void Draw_blends_cursor_and_leaves_the_rest_untouched()
    {
        // 2x2 全黑不透明背景；左上 (index 0) 与右下 (index 3) 用来对比
        var target = new ImageFrame(2, 2,
        [
            0, 0, 0, 255,   0, 0, 0, 255,
            0, 0, 0, 255,   0, 0, 0, 255,
        ]);

        // 1x1 纯红、半透明（alpha=128），画到右下角 (1,1)
        var cursor = new ImageFrame(1, 1, [0, 0, 255, 128]);

        var result = CursorCompositor.Draw(target, cursor, 1, 1);

        // BGRA：R 在 +2。混合后红色应介于 0 与 255 之间（128/255 ≈ 一半）
        var red = result.Pixels[((1 * 2) + 1) * 4 + 2];
        Assert.InRange(red, 120, 136);

        // 未覆盖的左上角保持纯黑
        Assert.Equal(0, result.Pixels[2]);

        // 入参原帧不能被改（Crop 可能返回同一实例，直接写会污染整张桌面截图）
        Assert.Equal(0, target.Pixels[((1 * 2) + 1) * 4 + 2]);
    }

    [Fact]
    public void Draw_completely_outside_returns_the_original_frame()
    {
        var target = new ImageFrame(1, 1, [0, 0, 0, 255]);
        var cursor = new ImageFrame(1, 1, [0, 0, 255, 255]);

        var result = CursorCompositor.Draw(target, cursor, 10, 10);

        Assert.Same(target, result);
    }

    [Fact]
    public void Fully_transparent_cursor_pixels_are_skipped()
    {
        var target = new ImageFrame(1, 1, [10, 20, 30, 255]);
        var cursor = new ImageFrame(1, 1, [255, 255, 255, 0]);

        var result = CursorCompositor.Draw(target, cursor, 0, 0);

        Assert.Same(target, result);
        Assert.Equal(10, target.Pixels[0]);
    }
}
