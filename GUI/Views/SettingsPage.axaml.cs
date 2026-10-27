using Avalonia.Controls;

namespace Kscan.App.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();
    private void OpenRecording(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Services.RecordingManager.Show();
}
