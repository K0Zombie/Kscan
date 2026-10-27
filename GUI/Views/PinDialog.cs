using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Kscan.App.Views;

/// <summary>
/// 极简确认对话框（纯代码，不引 XAML）：用来问"要不要连图片一起删"这类一次性的决定。
/// <para>
/// 必须带 <c>Topmost</c>：贴图窗口本身就是置顶的，普通对话框会被压在下面看不见。
/// </para>
/// </summary>
internal static class PinDialog
{
    /// <summary>两选一：返回 <c>true</c> = 点了确认。</summary>
    public static async Task<bool> ConfirmAsync(
        Window? owner,
        string title,
        string message,
        string confirmText,
        string cancelText)
    {
        var index = await ChooseAsync(owner, title, message, confirmText, cancelText);
        return index == 0;
    }

    /// <summary>多选一：返回按钮下标；关窗/ESC 返回 <c>-1</c>。</summary>
    public static async Task<int> ChooseAsync(Window? owner, string title, string message, params string[] options)
    {
        var result = -1;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            Topmost = true,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
        };

        var root = new StackPanel { Margin = new Thickness(18), Spacing = 14 };

        root.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        });

        for (var i = 0; i < options.Length; i++)
        {
            var index = i;

            var button = new Button
            {
                Content = options[i],
                Padding = new Thickness(12, 4, 12, 4),
                MinWidth = 88,
            };

            button.Click += (_, _) =>
            {
                result = index;
                dialog.Close();
            };

            buttons.Children.Add(button);
        }

        root.Children.Add(buttons);
        dialog.Content = root;

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                result = -1;
                dialog.Close();
                e.Handled = true;
            }
        };

        if (owner is { IsVisible: true })
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
            await WaitClosedAsync(dialog);
        }

        return result;
    }

    private static Task WaitClosedAsync(Window window)
    {
        var closed = new TaskCompletionSource();

        window.Closed += (_, _) => closed.TrySetResult();

        if (!window.IsVisible)
        {
            closed.TrySetResult();
        }

        return closed.Task;
    }
}
