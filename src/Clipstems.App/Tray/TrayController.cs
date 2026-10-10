using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Clipstems.App.Services;
using Clipstems.Core;
using Clipstems.Core.Capture;
using Clipstems.Core.Engine;

namespace Clipstems.App.Tray;

/// <summary>
/// The tray icon: state badge and tooltip kept current from the engine, a
/// menu rebuilt each time it opens, and a notification per finished clip.
/// Lives on the UI thread; engine events are marshalled to it.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly ClipstemsEngine _engine;
    private readonly Action _openSettings;
    private readonly Action _quit;
    private readonly TaskbarIcon _icon;
    private TrayState? _shownState;
    private string? _shownTip;
    private string? _lastNotifiedOutput;

    public TrayController(ClipstemsEngine engine, Action openSettings, Action quit)
    {
        _engine = engine;
        _openSettings = openSettings;
        _quit = quit;
        _icon = new TaskbarIcon
        {
            ContextMenu = new ContextMenu(),
            NoLeftClickDelay = true,
        };
        _icon.ContextMenu.Opened += (_, _) => BuildMenu(_icon.ContextMenu);
        _icon.TrayMouseDoubleClick += (_, _) => _openSettings();
        _icon.TrayBalloonTipClicked += (_, _) =>
        {
            if (_lastNotifiedOutput is { } output) Shell.Reveal(output);
            else _openSettings();
        };
        Refresh();
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    /// <summary>Updates icon and tooltip; cheap when nothing changed.</summary>
    public void Refresh()
    {
        var (state, tip) = Describe();
        if (state != _shownState)
        {
            _icon.Icon = TrayIcons.TrayIcon(state);
            _shownState = state;
        }
        if (tip != _shownTip)
        {
            _icon.ToolTipText = tip.Length > 120 ? tip[..119] + "…" : tip;
            _shownTip = tip;
        }
    }

    public void NotifyClip(ClipOutcome outcome)
    {
        if (!_engine.Settings.Notifications) return;
        var (headline, detail, warning) = ClipText.Describe(outcome);
        _lastNotifiedOutput = outcome.Result.OutputPath;
        _icon.ShowNotification(headline, $"{outcome.Title ?? outcome.ClipName}\n{detail}",
            warning ? NotificationIcon.Warning : NotificationIcon.Info);
    }

    public void Dispose() => _icon.Dispose();

    private (TrayState State, string Tip) Describe()
    {
        var problem = _engine.Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Problem);
        var status = _engine.Status;
        var tracks = _engine.Tracks;
        var recording = tracks.Where(t => t.State == RecorderState.Recording).Select(t => t.Title).ToList();
        var failing = tracks.Where(t => t.Failing).Select(t => t.Title).ToList();
        var tip = status switch
        {
            EngineStatus.Recording when failing.Count > 0 =>
                $"Clipstems: {string.Join(", ", failing)} audio unavailable. Open Settings." +
                (recording.Count > 0 ? $" Recording {string.Join(", ", recording)}." : ""),
            EngineStatus.Recording when recording.Count == 0 => "Clipstems: ready, waiting for sound sources",
            EngineStatus.Recording => $"Clipstems: recording {string.Join(", ", recording)}" + (_engine.Game is { } g ? $" ({g.Name})" : ""),
            EngineStatus.Paused => "Clipstems: paused",
            EngineStatus.WaitingForGame => "Clipstems: starts recording when you launch a Steam game",
            EngineStatus.Blocked => "Clipstems: another copy is recording",
            _ => "Clipstems",
        };
        if (problem is not null) return (TrayState.Problem, $"Clipstems: {problem.Message}");
        if (status == EngineStatus.Recording && failing.Count > 0) return (TrayState.Problem, tip);
        return (status switch
        {
            EngineStatus.Recording => TrayState.Recording,
            EngineStatus.Paused => TrayState.Paused,
            _ => TrayState.Idle,
        }, tip);
    }

    private void BuildMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        menu.Items.Add(new MenuItem { Header = new TextBlock { Text = Describe().Tip.Replace("Clipstems: ", "") }, IsEnabled = false });
        foreach (var issue in _engine.Issues.Where(i => i.Severity != IssueSeverity.Tip))
            menu.Items.Add(Item((issue.Severity == IssueSeverity.Problem ? "⚠ " : "") + Shorten(issue.Message, 70), _openSettings));
        menu.Items.Add(new Separator());

        var recent = new MenuItem { Header = "Recent clips" };
        var clips = _engine.RecentClips;
        if (clips.Count == 0) recent.Items.Add(new MenuItem { Header = "None yet", IsEnabled = false });
        foreach (var clip in clips)
        {
            var name = clip.Title ?? clip.ClipName;
            var (_, detail, warning) = ClipText.Describe(clip);
            if (clip.Result.OutputPath is { } path)
            {
                if (!warning)
                {
                    var item = Item(name, () => Shell.Reveal(path));
                    item.ToolTip = detail;
                    recent.Items.Add(item);
                    continue;
                }
                // Saved, but something is off: show what, readable without hovering.
                var flagged = new MenuItem { Header = new TextBlock { Text = $"⚠ {name} (audio warning)" } };
                AutomationProperties.SetHelpText(flagged, detail);
                flagged.Items.Add(new MenuItem { Header = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 }, IsEnabled = false });
                flagged.Items.Add(Item("Show file", () => Shell.Reveal(path)));
                recent.Items.Add(flagged);
                continue;
            }
            // No file was written: say why, and offer another go while the audio may still be kept.
            var failed = new MenuItem { Header = new TextBlock { Text = "✗ " + name } };
            AutomationProperties.SetHelpText(failed, detail);
            failed.Items.Add(new MenuItem { Header = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 }, IsEnabled = false });
            failed.Items.Add(Item("Try again", () => _engine.Enqueue(clip.ClipDirectory)));
            recent.Items.Add(failed);
        }
        menu.Items.Add(recent);
        menu.Items.Add(Item("Open clips folder", () => Shell.OpenFolder(_engine.Settings.EffectiveOutputFolder)));
        menu.Items.Add(new Separator());
        var pause = Item(_engine.Paused ? "Resume recording" : "Pause recording", () =>
        {
            _engine.Paused = !_engine.Paused;
            Refresh();
        });
        // Pausing here would not pause the copy that is actually recording.
        pause.IsEnabled = _engine.Status != EngineStatus.Blocked;
        menu.Items.Add(pause);
        menu.Items.Add(Item("Settings…", _openSettings));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit Clipstems", _quit));
    }

    private static MenuItem Item(string header, Action action)
    {
        // A TextBlock header keeps underscores in clip titles from turning into access keys.
        var item = new MenuItem { Header = new TextBlock { Text = header } };
        item.Click += (_, _) => action();
        return item;
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
