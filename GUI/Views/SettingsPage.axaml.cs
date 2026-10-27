using Avalonia.Controls;

namespace Kscan.App.Views;

public partial class SettingsPage : UserControl
{
    private ViewModels.SettingsViewModel? _settings;
    public SettingsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_settings is not null) _settings.PropertyChanged -= SectionChanged;
            _settings = DataContext as ViewModels.SettingsViewModel;
            if (_settings is not null) _settings.PropertyChanged += SectionChanged;
            EnsureRecordingPage();
        };
    }
    private void SectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.SettingsViewModel.ActiveSection)) EnsureRecordingPage();
    }
    private void EnsureRecordingPage()
    {
        if (_settings?.SectionRecording == true && RecordingSectionHost.Content is null)
            RecordingSectionHost.Content = new RecordingSettingsPanel();
    }
}
