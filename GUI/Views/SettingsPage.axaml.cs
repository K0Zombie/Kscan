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
            EnsureSectionPages();
        };
    }
    private void SectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.SettingsViewModel.ActiveSection)) EnsureSectionPages();
    }
    private void EnsureSectionPages()
    {
        if (_settings?.SectionRecording == true && RecordingSectionHost.Content is null)
            RecordingSectionHost.Content = new RecordingSettingsPanel();
        if (_settings?.SectionNaming == true && NamingSectionHost.Content is null)
            NamingSectionHost.Content = new NamingSettingsPanel();
        if (_settings?.SectionAnnotation == true && StampSectionHost.Content is null)
            StampSectionHost.Content = new StampSettingsPanel();
    }
    private async void OpenManual(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var result = await Services.ResultActions.OpenAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "帮助文档", "index.html"));
        ManualStatus.Text = result.Success ? "" : result.Error;
    }
}
