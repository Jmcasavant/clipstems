using System.Windows;
using Microsoft.Win32;
using Clipstems.App.Services;

namespace Clipstems.App.Settings;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Icon = Tray.TrayIcons.For(Tray.TrayState.Recording);
        Loaded += async (_, _) => await viewModel.ActivateAsync();
        Closed += (_, _) => viewModel.Deactivate();
    }

    private void OnAddApp(object sender, RoutedEventArgs e)
    {
        var dialog = new AddAppDialog(_viewModel.TrackedApps()) { Owner = this, Icon = Icon };
        if (dialog.ShowDialog() == true && dialog.Result is { } picked)
            _viewModel.AddApp(picked.Title, picked.Apps);
    }

    private void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Save clips with tracks to",
            InitialDirectory = System.IO.Directory.Exists(_viewModel.OutputFolder) ? _viewModel.OutputFolder : null,
        };
        if (dialog.ShowDialog(this) == true) _viewModel.OutputFolder = dialog.FolderName;
    }
}
