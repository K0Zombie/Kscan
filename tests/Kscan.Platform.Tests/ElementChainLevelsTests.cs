using Kscan.Platform.Abstractions;

namespace Kscan.Platform.Tests;

public sealed class ElementChainLevelsTests
{
    // 链从外到内：外层子元素 → 中层 → 最内层
    private static readonly ScreenRect[] Chain =
    [
        new(100, 100, 400, 300),
        new(150, 150, 200, 100),
        new(180, 170, 60, 30),
    ];

    [Fact]
    public void Offset_zero_is_the_innermost_element() =>
        Assert.Equal(Chain[^1], ElementChainLevels.At(Chain, 0));

    [Fact]
    public void Parent_walks_outwards_and_stops_at_window_level()
    {
        Assert.Equal(Chain[1], ElementChainLevels.At(Chain, 1));
        Assert.Equal(Chain[0], ElementChainLevels.At(Chain, 2));

        // 再往外就是窗口层：没有元素矩形，单击 = 截整个窗口
        Assert.Null(ElementChainLevels.At(Chain, 3));
        Assert.Null(ElementChainLevels.At(Chain, 99));
        Assert.True(ElementChainLevels.IsWindowLevel(Chain, 3));
        Assert.Equal(3, ElementChainLevels.Parent(Chain, 2));
        Assert.Equal(3, ElementChainLevels.Parent(Chain, 3));
    }

    [Fact]
    public void Child_walks_inwards_and_stops_at_innermost()
    {
        Assert.Equal(1, ElementChainLevels.Child(Chain, 2));
        Assert.Equal(0, ElementChainLevels.Child(Chain, 0));
        Assert.Equal(0, ElementChainLevels.Child(Chain, -5));
    }

    [Fact]
    public void Empty_chain_is_always_window_level()
    {
        Assert.Null(ElementChainLevels.At([], 0));
        Assert.True(ElementChainLevels.IsWindowLevel([], 0));
        Assert.Equal(0, ElementChainLevels.Parent([], 0));
    }

    [Fact]
    public void Clamp_keeps_offset_inside_the_chain()
    {
        Assert.Equal(0, ElementChainLevels.Clamp(Chain, -3));
        Assert.Equal(Chain.Length, ElementChainLevels.Clamp(Chain, 10));
        Assert.Equal(2, ElementChainLevels.Clamp(Chain, 2));
    }
}
