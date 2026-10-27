using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Kscan.App.Services;

namespace Kscan.App.Views;

// Quick actions only; configuration lives in Settings / Recording.
internal sealed class RecordingWindow : Window
{
    public RecordingWindow()
    {
        Title = "录制"; Width = 360; SizeToContent = SizeToContent.Height;
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Bind(BackgroundProperty, new DynamicResourceExtension("KscanBackgroundBrush"));
        Bind(ForegroundProperty, new DynamicResourceExtension("KscanForegroundBrush"));
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        foreach (var kind in Enum.GetValues<RecordingKind>())
        {
            var button = new Button { Content = kind == RecordingKind.Video ? "框选录视频" : kind == RecordingKind.Gif ? "框选录 GIF" : "单独录音", HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try { await RecordingManager.StartAsync(kind); }
                catch (Exception ex) { status.Text = ex.Message; Show(); }
                finally { button.IsEnabled = true; }
            };
            panel.Children.Add(button);
        }
        var stop = new Button { Content = "停止全部", HorizontalAlignment = HorizontalAlignment.Stretch };
        stop.Click += async (_, _) => await RecordingManager.StopAllAsync(); panel.Children.Add(stop);
        var settings = new Button { Content = "录制设置…", HorizontalAlignment = HorizontalAlignment.Stretch };
        settings.Click += (_, _) => { Hide(); RecordingManager.ShowSettings(); }; panel.Children.Add(settings);
        panel.Children.Add(status); Content = panel;
    }
}
