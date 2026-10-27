using Kscan.Core.Pipeline;

namespace Kscan.Core.Tests;

/// <summary>
/// <c>user/method/</c> 的读写：往返不丢参数、重复不写两次、坏文件不影响其它文件。
/// </summary>
public class MethodStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "kscan-method-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 测试清理失败不影响结论
        }
    }

    [Fact]
    public void Save_then_load_round_trips_every_parameter()
    {
        var recipe = new PreprocessRecipe
        {
            Name = "救低对比度",
            Steps =
            [
                new PreprocessStep(PreprocessKind.Decolor) { SaturationThreshold = 96 },
                new PreprocessStep(PreprocessKind.Contrast) { ContrastTailPermille = 25 },
                new PreprocessStep(PreprocessKind.Median) { MedianRadius = 3 },
                new PreprocessStep(PreprocessKind.Adaptive) { AdaptiveRadiusDivisor = 20, AdaptiveBias = 5 },
            ],
        };

        Assert.Equal(1, MethodStore.Save(_directory, recipe));

        var loaded = Assert.Single(MethodStore.LoadAll(_directory));
        Assert.Equal("救低对比度", loaded.Name);
        Assert.True(recipe.HasSameStepsAs(loaded));
        Assert.Equal(96, loaded.Steps[0].SaturationThreshold);
        Assert.Equal(25, loaded.Steps[1].ContrastTailPermille);
        Assert.Equal(3, loaded.Steps[2].MedianRadius);
        Assert.Equal(20, loaded.Steps[3].AdaptiveRadiusDivisor);
        Assert.Equal(5, loaded.Steps[3].AdaptiveBias);
    }

    [Fact]
    public void Save_does_not_duplicate_an_identical_method()
    {
        var recipe = new PreprocessRecipe { Steps = [new(PreprocessKind.Median)] };

        Assert.Equal(1, MethodStore.Save(_directory, recipe));
        Assert.Equal(1, MethodStore.Save(_directory, recipe));   // 同内容不再写
        Assert.Single(MethodStore.LoadAll(_directory));
    }

    [Fact]
    public void Different_parameters_are_different_methods()
    {
        MethodStore.Save(_directory, new PreprocessRecipe { Steps = [new PreprocessStep(PreprocessKind.Median) { MedianRadius = 1 }] });
        MethodStore.Save(_directory, new PreprocessRecipe { Steps = [new PreprocessStep(PreprocessKind.Median) { MedianRadius = 2 }] });

        Assert.Equal(2, MethodStore.LoadAll(_directory).Count);
    }

    [Fact]
    public void LoadAll_reads_bare_array_files_too()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, "from-friend.json"),
            """
            [
              { "name": "朋友的", "steps": [ { "kind": "Median", "medianRadius": 2 } ] }
            ]
            """);

        var loaded = Assert.Single(MethodStore.LoadAll(_directory));
        Assert.Equal("朋友的", loaded.Name);
        Assert.Equal(2, loaded.Steps[0].MedianRadius);
    }

    [Fact]
    public void Broken_file_is_skipped_without_breaking_the_rest()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "broken.json"), "{ 这不是 json");
        MethodStore.Save(_directory, new PreprocessRecipe { Steps = [new(PreprocessKind.Contrast)] });

        var loaded = Assert.Single(MethodStore.LoadAll(_directory));
        Assert.Equal(PreprocessKind.Contrast, loaded.Steps[0].Kind);
    }

    [Fact]
    public void Empty_recipe_files_are_ignored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, "empty-steps.json"),
            """{ "version": 1, "methods": [ { "name": "空方法", "steps": [] } ] }""");

        Assert.Empty(MethodStore.LoadAll(_directory));
    }

    [Fact]
    public void Missing_directory_yields_empty_list()
    {
        Assert.Empty(MethodStore.LoadAll(Path.Combine(_directory, "nope")));
    }

    [Fact]
    public void SuggestName_counts_existing_methods()
    {
        Assert.Equal("方法 1", MethodStore.SuggestName(_directory));

        MethodStore.Save(_directory, new PreprocessRecipe { Steps = [new(PreprocessKind.Median)] });

        Assert.Equal("方法 2", MethodStore.SuggestName(_directory));
    }
}
