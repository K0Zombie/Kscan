using Kscan.Core.Configuration;

namespace Kscan.Core.Tests;

public sealed class MaskInputSettingsTests
{
    [Theory]
    [InlineData("Ctrl", HeldModifier.Control)]
    [InlineData("control", HeldModifier.Control)]
    [InlineData(" Alt ", HeldModifier.Alt)]
    [InlineData("SHIFT", HeldModifier.Shift)]
    [InlineData("none", HeldModifier.None)]
    [InlineData("", HeldModifier.None)]
    [InlineData(null, HeldModifier.None)]
    [InlineData("win", HeldModifier.None)]
    public void Held_modifier_parses_known_values_and_falls_back_to_none(string? text, HeldModifier expected) =>
        Assert.Equal(expected, HeldModifierParser.Parse(text));

    [Theory]
    [InlineData("wheel", ElementLevelMode.Wheel)]
    [InlineData("Tab", ElementLevelMode.Tab)]
    [InlineData("arrows", ElementLevelMode.Arrows)]
    [InlineData("updown", ElementLevelMode.Arrows)]
    [InlineData("none", ElementLevelMode.None)]
    [InlineData(null, ElementLevelMode.Wheel)]
    [InlineData("garbage", ElementLevelMode.Wheel)]
    public void Element_level_mode_parses_known_values_and_falls_back_to_wheel(string? text, ElementLevelMode expected) =>
        Assert.Equal(expected, ElementLevelModeParser.Parse(text));

    [Fact]
    public void New_mask_keys_default_to_wheel_and_ctrl()
    {
        var settings = AppSettings.Default;

        Assert.Equal("wheel", settings.ElementLevelKey);

        // 只剩一个"截整个窗口"的修饰键：Ctrl（"忽略子元素"与"单击截窗口"合并成一个）
        Assert.Equal("Ctrl", settings.ElementIgnoreModifier);
    }

    [Fact]
    public void Mask_keys_round_trip_through_record_with()
    {
        var customized = AppSettings.Default with
        {
            ElementLevelKey = "tab",
            ElementIgnoreModifier = "none",
        };

        Assert.NotEqual(AppSettings.Default, customized);
        Assert.Equal(ElementLevelMode.Tab, ElementLevelModeParser.Parse(customized.ElementLevelKey));
        Assert.Equal(HeldModifier.None, HeldModifierParser.Parse(customized.ElementIgnoreModifier));
    }
}
