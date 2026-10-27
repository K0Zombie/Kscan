using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Kscan.App.Services;

/// <summary>
/// 简单色板：点一个色块就把它的 <c>#AARRGGBB</c> 返回给调用方。
/// 不依赖额外 NuGet 包，离线也能用；需要精确数值时仍然可以直接在输入框里手填。
/// </summary>
internal static class ColorPalette
{
    /// <summary>弹出色板；用户关掉窗口（没有选色）时返回 <c>null</c>。</summary>
    public static Task<string?> PickAsync(string? current)
    {
        var window = new ColorPaletteWindow(current);
        window.Show();
        return window.Result;
    }
}

internal sealed class ColorPaletteWindow : Window
{
    private static readonly string[] Swatches =
    [
        // 灰阶
        "#000000", "#1E1E1E", "#2D2D30", "#3C3C3C", "#4A4A4A", "#5A5A5A", "#6E6E6E", "#808080",
        "#9E9E9E", "#B0B0B0", "#C8C8C8", "#DCDCDC", "#EDEDED", "#F5F5F5", "#FAFAFA", "#FFFFFF",
        // 红 / 橙
        "#7F1D1D", "#B91C1C", "#DC2626", "#EF4444", "#F87171", "#FCA5A5", "#FECACA", "#FEE2E2",
        "#7C2D12", "#C2410C", "#EA580C", "#F97316", "#FB923C", "#FDBA74", "#FED7AA", "#FFEDD5",
        // 黄 / 绿
        "#713F12", "#A16207", "#CA8A04", "#EAB308", "#FACC15", "#FDE047", "#FEF08A", "#FEF9C3",
        "#14532D", "#15803D", "#16A34A", "#22C55E", "#4ADE80", "#86EFAC", "#BBF7D0", "#DCFCE7",
        // 青 / 蓝
        "#0F766E", "#0D9488", "#14B8A6", "#2DD4BF", "#5EEAD4", "#99F6E4", "#CCFBF1", "#F0FDFA",
        "#0C4A6E", "#0369A1", "#0284C7", "#0EA5E9", "#38BDF8", "#7DD3FC", "#BAE6FD", "#E0F2FE",
        "#1E3A8A", "#1D4ED8", "#2563EB", "#3B82F6", "#60A5FA", "#93C5FD", "#BFDBFE", "#DBEAFE",
        // 紫 / 粉
        "#4C1D95", "#6D28D9", "#7C3AED", "#8B5CF6", "#A78BFA", "#C4B5FD", "#DDD6FE", "#EDE9FE",
        "#831843", "#BE185D", "#DB2777", "#EC4899", "#F472B6", "#F9A8D4", "#FBCFE8", "#FCE7F3",
        // 半透明黑 / 白（遮罩、面板、边框常用）
        "#00000000", "#22000000", "#44000000", "#66000000", "#88000000", "#AA000000", "#CC000000", "#EE000000",
        "#11FFFFFF", "#22FFFFFF", "#33FFFFFF", "#55FFFFFF", "#88FFFFFF", "#AAFFFFFF", "#CCFFFFFF", "#EEFFFFFF",
    ];

    private readonly TaskCompletionSource<string?> _completion = new();

    public ColorPaletteWindow(string? current)
    {
        Title = string.IsNullOrWhiteSpace(current) ? "选择颜色" : $"选择颜色（当前 {current}）";
        Width = 560;
        Height = 420;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new WrapPanel { Margin = new Thickness(10) };

        foreach (var hex in Swatches)
        {
            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(4),
                Background = Parse(hex),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = hex,
            };

            ToolTip.SetTip(swatch, hex);

            swatch.PointerPressed += (_, _) =>
            {
                _completion.TrySetResult(hex);
                Close();
            };

            panel.Children.Add(swatch);
        }

        Content = new ScrollViewer { Content = panel };
        Closed += (_, _) => _completion.TrySetResult(null);
    }

    public Task<string?> Result => _completion.Task;

    private static IBrush Parse(string hex) =>
        Color.TryParse(hex, out var color) ? new SolidColorBrush(color) : Brushes.Transparent;
}
