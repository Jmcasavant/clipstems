using System.IO;
using System.Windows;
using System.Windows.Controls;
using Clipstems.Core.Capture;

namespace Clipstems.App.Settings;

public sealed record PickedApp(string Title, IReadOnlyList<string> Apps);

public partial class AddAppDialog : Window
{
    private sealed record Row(string Exe, string DisplayName, bool IsActive, bool HasTrack)
    {
        public string ActiveText => (IsActive ? " · playing now" : "") + (HasTrack ? " · already has a track" : "");
    }

    private readonly IReadOnlySet<string> _tracked;

    private bool _titleEdited;
    private bool _settingTitle;

    public AddAppDialog(IReadOnlySet<string> tracked)
    {
        _tracked = tracked;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            ExeBox.Focus();
            try
            {
                // Core Audio session enumeration wants an MTA thread.
                var apps = await Task.Run(AudioSessions.List);
                AppList.ItemsSource = apps.Select(a => new Row(a.Exe, a.DisplayName, a.IsActive, _tracked.Contains(a.Exe))).ToList();
                ListHint.Text = apps.Count == 0 ? "No apps are using audio right now. Type a program name below." : "";
            }
            catch (Exception ex)
            {
                ListHint.Text = $"Couldn't list apps: {ex.Message}";
            }
        };
    }

    public PickedApp? Result { get; private set; }

    private void OnAppPicked(object sender, SelectionChangedEventArgs e)
    {
        if (AppList.SelectedItem is not Row row) return;
        ExeBox.Text = row.Exe;
        SetTitle(row.DisplayName.Length <= 24 ? row.DisplayName : Path.GetFileNameWithoutExtension(row.Exe));
    }

    private void OnVoiceChat(object sender, RoutedEventArgs e)
    {
        AppList.SelectedItem = null;
        ExeBox.Text = string.Join(", ", CaptureSource.VoiceChatApps);
        SetTitle("Voice chat");
    }

    private void OnExeChanged(object sender, TextChangedEventArgs e)
    {
        var apps = Apps();
        AddButton.IsEnabled = apps.Count > 0 && TitleBox.Text.Trim().Length > 0;
        if (!_titleEdited && apps.Count == 1 && AppList.SelectedItem is null)
            SetTitle(Path.GetFileNameWithoutExtension(apps[0]));
    }

    private void OnTitleChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingTitle) _titleEdited = TitleBox.Text.Length > 0;
        AddButton.IsEnabled = Apps().Count > 0 && TitleBox.Text.Trim().Length > 0;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var apps = Apps();
        if (apps.Count == 0 || TitleBox.Text.Trim().Length == 0) return;
        Result = new PickedApp(TitleBox.Text.Trim(), apps);
        DialogResult = true;
    }

    private void SetTitle(string title)
    {
        if (_titleEdited) return;
        _settingTitle = true;
        TitleBox.Text = title;
        _settingTitle = false;
    }

    private List<string> Apps() => ExeBox.Text
        .Split(['|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(CaptureSource.ExeName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
