using Kscan.Core.Imaging;
using Kscan.Core.Pipeline;

namespace Kscan.Core.Tests;

/// <summary>
/// 预处理「方法」（recipe）与逐层图像：层数、顺序、标签、参数是否真的生效。
/// 界面上的「查看不同层级」与「按层重识别」都建立在这些不变量上。
/// </summary>
public class PreprocessRecipeTests
{
    [Fact]
    public void Default_recipe_is_the_historical_four_steps()
    {
        var kinds = PreprocessRecipe.Default.Steps.Select(s => s.Kind).ToArray();

        Assert.Equal(
            [PreprocessKind.Decolor, PreprocessKind.Contrast, PreprocessKind.Median, PreprocessKind.Adaptive],
            kinds);
    }

    [Fact]
    public void Levels_start_with_the_original_frame()
    {
        var source = Gradient(64, 64);

        var levels = PreprocessRunner.Levels(source);

        Assert.True(levels.Count >= 2, "默认链至少应该改出第二层");
        Assert.Equal(PreprocessRunner.OriginalLabel, levels[0].Label);
        Assert.Null(levels[0].Step);
        Assert.Null(levels[0].Suffix);
        Assert.Same(source, levels[0].Frame);   // 原图层必须是同一张图，不能复制
    }

    [Fact]
    public void Levels_chain_labels_and_suffixes()
    {
        var levels = PreprocessRunner.Levels(Gradient(64, 64));

        for (var index = 1; index < levels.Count; index++)
        {
            var level = levels[index];
            Assert.NotNull(level.Step);
            Assert.NotNull(level.Suffix);
            Assert.StartsWith("+", level.Suffix);
            Assert.Equal(levels[index - 1].Label + "+" + level.Step!.Label, level.Label);
            Assert.NotSame(levels[index - 1].Frame, level.Frame);
        }
    }

    [Fact]
    public void Empty_recipe_means_original_only()
    {
        var levels = PreprocessRunner.Levels(Gradient(32, 32), new PreprocessRecipe());

        Assert.Single(levels);
        Assert.Null(levels[0].Suffix);
    }

    [Fact]
    public void Custom_order_is_respected()
    {
        var recipe = new PreprocessRecipe
        {
            Steps = [new(PreprocessKind.Contrast), new(PreprocessKind.Median)],
        };

        // 用低对比度渐变：0..255 的高对比度图会被 StretchContrast 判成"不需要处理"直接返回原实例
        var levels = PreprocessRunner.Levels(Gradient(64, 64, from: 100, to: 150), recipe);

        // 低对比度渐变：第一步 contrast 一定会改图；median 可能因为渐变平滑而"无变化"不占层
        Assert.True(levels.Count >= 2);
        Assert.Equal(PreprocessKind.Contrast, levels[1].Step!.Kind);
        Assert.StartsWith("+contrast", levels[1].Suffix);
    }

    [Fact]
    public void Step_without_effect_does_not_take_a_level()
    {
        // 纯白图：直方图 range = 0 → StretchContrast 直接返回原实例
        var white = Solid(16, 16, 255);
        var recipe = new PreprocessRecipe { Steps = [new(PreprocessKind.Contrast)] };

        var levels = PreprocessRunner.Levels(white, recipe);

        Assert.Single(levels);
    }

    [Fact]
    public void Median_radius_parameter_changes_the_output()
    {
        // 棋盘图不行：7x7 窗口里中值仍等于中心色，滤完跟原图一模一样。
        // 用"白底 + 孤立黑点"：radius 0 = 不处理，radius 3 = 黑点被抹掉。
        var speckled = Speckle(48, 48);
        var step1 = new PreprocessStep(PreprocessKind.Median) { MedianRadius = 0 };
        var step2 = new PreprocessStep(PreprocessKind.Median) { MedianRadius = 3 };

        var a = PreprocessRunner.Apply(speckled, step1);
        var b = PreprocessRunner.Apply(speckled, step2);

        Assert.Same(speckled, a);          // radius 0 = 不处理，必须复用原实例
        Assert.NotEqual(a.Pixels, b.Pixels);
    }

    [Fact]
    public void Contrast_tail_parameter_changes_the_output()
    {
        var lowContrast = Gradient(64, 64, from: 100, to: 150);
        var gentle = PreprocessRunner.Apply(lowContrast, new PreprocessStep(PreprocessKind.Contrast) { ContrastTailPermille = 5 });
        var aggressive = PreprocessRunner.Apply(lowContrast, new PreprocessStep(PreprocessKind.Contrast) { ContrastTailPermille = 100 });

        Assert.NotEqual(gentle.Pixels, aggressive.Pixels);
    }

    [Fact]
    public void Signature_ignores_name_but_not_parameters()
    {
        var a = new PreprocessRecipe
        {
            Name = "A",
            Steps = [new PreprocessStep(PreprocessKind.Median) { MedianRadius = 2 }],
        };
        var b = new PreprocessRecipe
        {
            Name = "B",
            Steps = [new PreprocessStep(PreprocessKind.Median) { MedianRadius = 2 }],
        };
        var c = new PreprocessRecipe
        {
            Name = "A",
            Steps = [new PreprocessStep(PreprocessKind.Median) { MedianRadius = 3 }],
        };

        Assert.True(a.HasSameStepsAs(b));
        Assert.False(a.HasSameStepsAs(c));
        Assert.False(a.HasSameStepsAs(null!));
    }

    [Fact]
    public void Pipeline_accepts_a_custom_recipe()
    {
        // 空 recipe → 只跑原图那一层，attempts 里不应该出现预处理后缀
        var pipeline = ScanPipeline.CreateDefault();
        var outcome = pipeline.Scan(
            Gradient(96, 96),
            new Kscan.Core.Model.ScanOptions { TryDenoise = true },
            new PreprocessRecipe());

        Assert.NotEmpty(outcome.Attempts);
        Assert.All(outcome.Attempts, a => Assert.Equal("zxing", a.EngineId));
    }

    // ---- 造图辅助 ----

    private static ImageFrame Solid(int width, int height, byte value)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
            pixels[i + 3] = 255;
        }

        return new ImageFrame(width, height, pixels);
    }

    private static ImageFrame Gradient(int width, int height, int from = 0, int to = 255)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = (byte)(from + (to - from) * x / Math.Max(1, width - 1));
            var p = (y * width + x) * 4;
            pixels[p] = pixels[p + 1] = pixels[p + 2] = value;
            pixels[p + 3] = 255;
        }

        return new ImageFrame(width, height, pixels);
    }

    /// <summary>白底 + 每隔 6 像素一个孤立黑点：中值滤波一定能把它抹掉。</summary>
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
