using Kscan.Core.Imaging;
using Kscan.Core.Pipeline;

namespace Kscan.Core.Tests;

/// <summary>
/// 识别页的预处理会话：切层 / 跳端 / 调参 / 增删步骤 / 调顺序。
/// 这些是键位背后真正的语义，界面只负责把按键翻译成方法调用。
/// </summary>
public class PreprocessSessionTests
{
    [Fact]
    public void Starts_on_the_original_layer()
    {
        var session = new PreprocessSession(LowContrastGradient(64, 64));

        Assert.Equal(0, session.Index);
        Assert.True(session.IsAtOriginal);
        Assert.Equal(PreprocessRunner.OriginalLabel, session.Current.Label);
        Assert.Same(session.Source, session.Current.Frame);
        Assert.True(session.Levels.Count > 1, "默认方法应该产出多层");
    }

    [Fact]
    public void MoveLevel_walks_and_clamps()
    {
        // 显式两步中值 + 斑点图：保证两步都真的改了图（否则"没变化不占层"会让层数不可预期）
        var session = new PreprocessSession(
            Speckle(48, 48),
            new PreprocessRecipe
            {
                Steps =
                [
                    new PreprocessStep(PreprocessKind.Median) { MedianRadius = 1 },
                    new PreprocessStep(PreprocessKind.Median) { MedianRadius = 3 },
                ],
            });

        Assert.Equal(3, session.Levels.Count);   // 原图 + 两层

        Assert.True(session.MoveLevel(1));
        Assert.Equal(1, session.Index);

        Assert.True(session.MoveLevel(-1));
        Assert.Equal(0, session.Index);
        Assert.False(session.MoveLevel(-1));   // 已在首层

        Assert.True(session.MoveLevel(5));      // 越界夹到末层
        Assert.Equal(2, session.Index);
        Assert.False(session.MoveLevel(1));
    }

    [Fact]
    public void JumpToEdge_goes_to_first_and_last()
    {
        var session = new PreprocessSession(LowContrastGradient(64, 64));

        Assert.True(session.JumpToEdge(last: true));
        Assert.Equal(session.Levels.Count - 1, session.Index);
        Assert.False(session.JumpToEdge(last: true));   // 已在末层

        Assert.True(session.JumpToEdge(last: false));
        Assert.Equal(0, session.Index);
        Assert.False(session.JumpToEdge(last: false));
    }

    [Fact]
    public void AdjustParameter_is_ignored_on_the_original_layer()
    {
        var session = new PreprocessSession(LowContrastGradient(64, 64));

        Assert.False(session.AdjustParameter(1));
        Assert.Equal(0, session.Index);
    }

    [Fact]
    public void AdjustParameter_changes_the_current_step_and_rebuilds()
    {
        // 只放一步中值：切到它，L 一次 → 半径 +1
        var session = new PreprocessSession(
            Speckle(48, 48),
            new PreprocessRecipe { Steps = [new PreprocessStep(PreprocessKind.Median) { MedianRadius = 1 }] });

        Assert.True(session.MoveLevel(1));
        var before = session.Recipe.Steps[0].MedianRadius;

        Assert.True(session.AdjustParameter(1));

        Assert.Equal(before + 1, session.Recipe.Steps[0].MedianRadius);
        // 重建后仍停在同一「步骤」上（层序号可能因该步不再生效而变化）
        Assert.Equal(0, session.Current.StepIndex);
    }

    [Fact]
    public void AdjustParameter_stops_at_the_upper_bound()
    {
        var session = new PreprocessSession(
            Speckle(48, 48),
            new PreprocessRecipe { Steps = [new PreprocessStep(PreprocessKind.Median) { MedianRadius = 6 }] });

        session.MoveLevel(1);

        Assert.False(session.AdjustParameter(1));   // 已到 6
        Assert.Equal(6, session.Recipe.Steps[0].MedianRadius);
    }

    [Fact]
    public void AddStep_inserts_after_the_current_one()
    {
        var session = new PreprocessSession(
            LowContrastGradient(64, 64),
            new PreprocessRecipe { Steps = [new PreprocessStep(PreprocessKind.Contrast)] });

        session.MoveLevel(1);
        session.AddStep(PreprocessKind.Median);

        Assert.Equal(2, session.Recipe.Steps.Count);
        Assert.Equal(PreprocessKind.Median, session.Recipe.Steps[1].Kind);
    }

    [Fact]
    public void AddStep_on_the_original_layer_goes_to_the_front_and_shows_it()
    {
        // 停在原图层时加到链首：插到链尾的话，前面的 adaptive 已把图二值化，
        // 再加减噪往往毫无变化 —— 用户看到的就是"点了 +median 没反应"。
        var session = new PreprocessSession(Speckle(48, 48), PreprocessRecipe.Default);

        var index = session.AddStep(PreprocessKind.Median);

        Assert.Equal(0, index);
        Assert.Equal(PreprocessKind.Median, session.Recipe.Steps[0].Kind);

        // 视图必须切到新加的那一步，否则左侧大图看着还是原图
        Assert.Equal(0, session.Current.StepIndex);
        Assert.True(session.Index > 0);
    }

    [Fact]
    public void RemoveCurrentStep_drops_the_step_and_moves_up()
    {
        // 斑点图 + 两步不同的中值：两层都真实生效，末层就是第二步
        var session = new PreprocessSession(
            Speckle(48, 48),
            new PreprocessRecipe
            {
                Steps =
                [
                    new PreprocessStep(PreprocessKind.Median) { MedianRadius = 1 },
                    new PreprocessStep(PreprocessKind.Median) { MedianRadius = 3 },
                ],
            });

        session.JumpToEdge(last: true);
        Assert.Equal(1, session.Current.StepIndex);

        Assert.True(session.RemoveCurrentStep());

        Assert.Single(session.Recipe.Steps);
        Assert.Equal(1, session.Recipe.Steps[0].MedianRadius);
    }

    [Fact]
    public void RemoveCurrentStep_is_noop_on_the_original_layer()
    {
        var session = new PreprocessSession(LowContrastGradient(64, 64));

        Assert.False(session.RemoveCurrentStep());
    }

    [Fact]
    public void MoveCurrentStep_reorders_the_recipe()
    {
        var session = new PreprocessSession(
            LowContrastGradient(64, 64),
            new PreprocessRecipe
            {
                Steps = [new PreprocessStep(PreprocessKind.Contrast), new PreprocessStep(PreprocessKind.Median)],
            });

        session.MoveLevel(1);   // 停在 contrast
        Assert.Equal(PreprocessKind.Contrast, session.Current.Step!.Kind);

        Assert.True(session.MoveCurrentStep(1));

        Assert.Equal(PreprocessKind.Median, session.Recipe.Steps[0].Kind);
        Assert.Equal(PreprocessKind.Contrast, session.Recipe.Steps[1].Kind);
    }

    [Fact]
    public void UseRecipe_swaps_the_whole_method()
    {
        var session = new PreprocessSession(LowContrastGradient(64, 64));
        var simple = new PreprocessRecipe
        {
            Name = "只用中值",
            Steps = [new PreprocessStep(PreprocessKind.Median) { MedianRadius = 2 }],
        };

        session.UseRecipe(simple);

        Assert.Same(simple, session.Recipe);
        Assert.Equal(0, session.Index);                 // 换方法后回到原图
        Assert.All(session.Levels.Skip(1), level => Assert.Equal(0, level.StepIndex));
    }

    // ---- 造图辅助 ----

    private static ImageFrame LowContrastGradient(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = (byte)(100 + 50 * x / Math.Max(1, width - 1));
            var p = (y * width + x) * 4;
            pixels[p] = pixels[p + 1] = pixels[p + 2] = value;
            pixels[p + 3] = 255;
        }

        return new ImageFrame(width, height, pixels);
    }

    private static ImageFrame Speckle(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
            pixels[i + 3] = 255;
        }

        for (var y = 3; y < height; y += 6)
        for (var x = 3; x < width; x += 6)
        {
            var p = (y * width + x) * 4;
            pixels[p] = pixels[p + 1] = pixels[p + 2] = 0;
        }

        return new ImageFrame(width, height, pixels);
    }
}
