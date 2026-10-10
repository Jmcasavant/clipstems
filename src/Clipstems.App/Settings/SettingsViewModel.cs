using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Clipstems.App.Services;
using Clipstems.Core;
using Clipstems.Core.Capture;
using Clipstems.Core.Engine;
using Clipstems.Core.Media;
using Clipstems.Core.Settings;
using Clipstems.Core.Steam;

namespace Clipstems.App.Settings;

public sealed record IssueItem(string Glyph, string Message);

/// <summary>One line of the Steam checklist: done, worth changing, or just information.</summary>
public sealed record SteamCheck(string Glyph, string Text, string Fix)
{
    public const string Done = "\uE73E";
    public const string Change = "\uE7BA";
    public const string Info = "\uE946";
}

/// <summary>
/// Settings, applied as they change (validated first) like Windows 11's own
/// settings, plus live status: per-track level meters, Steam's setup, issues.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly int[] RetentionPresets = [30, 60, 120, 240, 480];

    /// <summary>16-bit stereo at 48 kHz, as the buffer stores it.</summary>
    private const double BufferBytesPerSecond = 48_000 * 2 * 2;

    /// <summary>Opus at 128 kbps (OpusSegmentCodec), what closed segments shrink to.</summary>
    private const double CompressedBytesPerSecond = 128_000 / 8;

    private readonly ClipstemsEngine _engine;
    private readonly SettingsStore _store;
    private readonly ILogger _log;
    private readonly DispatcherTimer _applyTimer;
    private readonly DispatcherTimer _liveTimer;
    private readonly DispatcherTimer _deviceRefreshTimer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private DefaultDeviceWatcher? _deviceWatcher;
    private bool _loading = true;
    private IReadOnlyList<DeviceOption> _inputs = [new("Windows default", null)];
    private IReadOnlyList<DeviceOption> _outputs = [new("Windows default", null)];

    public SettingsViewModel(ClipstemsEngine engine, SettingsStore store, ILogger log, string? loadProblem)
    {
        _engine = engine;
        _store = store;
        _log = log;
        _applyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _applyTimer.Tick += (_, _) =>
        {
            _applyTimer.Stop();
            Apply();
        };
        // One plug-in raises several notifications; refresh once they settle.
        _deviceRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _deviceRefreshTimer.Tick += async (_, _) =>
        {
            _deviceRefreshTimer.Stop();
            await RefreshDevicesAsync();
        };
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _liveTimer.Tick += (_, _) => RefreshLive();

        var s = engine.Settings;
        RecordOnlyDuringGames = s.RecordWhen == RecordPolicy.WhileSteamGameRuns;
        RetentionMinutes = s.RetentionMinutes;
        OutputFolder = s.EffectiveOutputFolder;
        StartWithWindows = s.StartWithWindows;
        Notifications = s.Notifications;
        MirrorSteamPrograms = s.MirrorSteamPrograms;
        RebuildRows();
        Message = loadProblem;
        _loading = false;
        RefreshLive();
    }

    public ObservableCollection<TrackRowViewModel> Tracks { get; } = [];

    /// <summary>Problems and warnings; tips live in the Steam checklist instead.</summary>
    public ObservableCollection<IssueItem> Issues { get; } = [];

    public ObservableCollection<SteamCheck> SteamChecks { get; } = [];

    [ObservableProperty] public partial bool RecordOnlyDuringGames { get; set; }
    [ObservableProperty] public partial int RetentionMinutes { get; set; }
    [ObservableProperty] public partial string OutputFolder { get; set; } = "";
    [ObservableProperty] public partial bool StartWithWindows { get; set; }
    [ObservableProperty] public partial bool Notifications { get; set; }
    [ObservableProperty] public partial bool MirrorSteamPrograms { get; set; }

    /// <summary>Validation errors and other one-off messages.</summary>
    [ObservableProperty] public partial string? Message { get; set; }

    [ObservableProperty] public partial string EngineStatus { get; set; } = "";

    /// <summary>"Steam's mix + 3 tracks": what the next saved clip will hold.</summary>
    [ObservableProperty] public partial string ClipSummary { get; set; } = "";

    [ObservableProperty] public partial bool SteamNeedsAttention { get; set; }

    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";
    public string DataFolder => ClipstemsPaths.DataRoot;
    public string FfmpegStatus => Ffmpeg.Locate() is { } f ? $"Found at {f.FfmpegPath}" : "Not found. Install it with: winget install Gyan.FFmpeg.Essentials";
    public bool IsDevelopmentBuild => StartupRegistration.IsDevelopmentBuild;

    partial void OnRecordOnlyDuringGamesChanged(bool value) => ScheduleApply();
    partial void OnRetentionMinutesChanged(int value)
    {
        OnPropertyChanged(nameof(RetentionChoices));
        ScheduleApply();
    }

    public IReadOnlyList<int> RetentionChoices => [.. RetentionPresets.Append(RetentionMinutes).Distinct().Order()];

    /// <summary>Most disk space the buffer takes, for the tracks on now plus the hidden timing track.</summary>
    [ObservableProperty] public partial string DiskEstimate { get; set; } = "";
    partial void OnOutputFolderChanged(string value) => ScheduleApply();
    partial void OnStartWithWindowsChanged(bool value) => ScheduleApply();
    partial void OnNotificationsChanged(bool value) => ScheduleApply();

    partial void OnMirrorSteamProgramsChanged(bool value)
    {
        ScheduleApply();
        if (!_loading) SyncSteamRows();
    }

    /// <summary>Starts live updates and loads device lists (Core Audio needs an MTA thread).</summary>
    public async Task ActivateAsync()
    {
        _liveTimer.Start();
        await RefreshDevicesAsync();
        try
        {
            // Created on an MTA thread like the engine's; callbacks arrive on Core Audio threads.
            _deviceWatcher = await Task.Run(() => new DefaultDeviceWatcher());
            _deviceWatcher.DevicesChanged += () => _dispatcher.BeginInvoke(() =>
            {
                _deviceRefreshTimer.Stop();
                _deviceRefreshTimer.Start();
            });
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            _log.LogWarning("Device changes will not refresh the lists: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Re-reads the device lists and swaps them into each row in place, so a
    /// headset plugged in shows up without disturbing a name being typed.
    /// </summary>
    public async Task RefreshDevicesAsync()
    {
        var (oldInputs, oldOutputs) = (_inputs, _outputs);
        try
        {
            var (inputs, outputs) = await Task.Run(() =>
            {
                var all = AudioDevices.List();
                return (all.Where(d => d.Kind == DeviceKind.Input).ToList(), all.Where(d => d.Kind == DeviceKind.Output).ToList());
            });
            _inputs = Options(inputs);
            _outputs = Options(outputs);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            _log.LogWarning("Listing audio devices failed: {Error}", ex.Message);
        }
        if (_inputs.SequenceEqual(oldInputs) && _outputs.SequenceEqual(oldOutputs)) return;
        foreach (var row in Tracks.Where(r => r.HasDeviceChoice))
            row.UpdateDevices(DevicesFor(row.Source));
    }

    private static List<DeviceOption> Options(List<AudioDevice> devices)
    {
        var current = devices.FirstOrDefault(d => d.IsDefault);
        var options = new List<DeviceOption> { new(current is null ? "Windows default" : $"Windows default ({current.Name})", null) };
        foreach (var d in devices)
        {
            int same = devices.Count(o => o.Name == d.Name);
            int nth = devices.TakeWhile(o => o != d).Count(o => o.Name == d.Name) + 1;
            options.Add(new(same > 1 ? $"{d.Name} ({nth})" : d.Name, d.Id, d.Name));
        }
        return options;
    }

    public void Deactivate()
    {
        _liveTimer.Stop();
        _deviceRefreshTimer.Stop();
        _deviceWatcher?.Dispose();
        _deviceWatcher = null;
        if (_applyTimer.IsEnabled)
        {
            _applyTimer.Stop();
            Apply();
        }
    }

    [RelayCommand]
    private void AddMicrophone() => AddTrack("Mic", new CaptureSource(SourceKind.Microphone));

    [RelayCommand]
    private void AddGame() => AddTrack("Game", new CaptureSource(SourceKind.SteamGame));

    [RelayCommand]
    private void AddOutputDevice() => AddTrack("Speakers", new CaptureSource(SourceKind.SystemOutput));

    /// <summary>Adds an app track; the window supplies the picked title and exe names.</summary>
    public void AddApp(string title, IReadOnlyList<string> apps) =>
        AddTrack(title, new CaptureSource(SourceKind.Application) { Apps = apps.Select(CaptureSource.ExeName).ToList() });

    [RelayCommand]
    private void RemoveTrack(TrackRowViewModel row)
    {
        if (row.IsFromSteam) return;
        Tracks.Remove(row);
        // Removing an app track can hand a Steam-ticked program back to its own row.
        SyncSteamRows();
        OnPropertyChanged(nameof(HasGameTrack));
        ScheduleApply();
    }

    [RelayCommand]
    private void OpenOutputFolder() => Shell.OpenFolder(_engine.Settings.EffectiveOutputFolder);

    [RelayCommand]
    private void OpenDataFolder() => Shell.OpenFolder(ClipstemsPaths.DataRoot);

    [RelayCommand]
    private void OpenLogs() => Shell.OpenFolder(App.LogFolder);

    [RelayCommand]
    private void OpenSteamSettings()
    {
        try
        {
            Shell.OpenUri("steam://open/settings");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Message = "Couldn't open Steam. Open Steam yourself and choose Settings › Game Recording.";
        }
    }

    /// <summary>Apps that already have a track (the user's or Steam's), for the Add app list.</summary>
    public IReadOnlySet<string> TrackedApps() => Tracks
        .Where(t => t.Source.Kind == SourceKind.Application).SelectMany(t => t.Source.Apps)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool HasGameTrack => Tracks.Any(t => t.Source.Kind == SourceKind.SteamGame && !t.IsFromSteam);

    private void AddTrack(string title, CaptureSource source)
    {
        var ids = Tracks.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stem = new string(title.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (stem.Length == 0 || stem.StartsWith("steam")) stem = "track";
        var id = stem;
        for (int n = 2; ids.Contains(id); n++) id = $"{stem}-{n}";
        var row = new TrackRowViewModel(new TrackSettings(id, title, true, source), fromSteam: false, DevicesFor(source), ScheduleApply);
        Tracks.Insert(Tracks.TakeWhile(t => !t.IsFromSteam).Count(), row);
        // A Steam program the new app track now records drops its own row.
        SyncSteamRows();
        OnPropertyChanged(nameof(HasGameTrack));
        ScheduleApply();
    }

    private void ScheduleApply()
    {
        if (_loading) return;
        Renumber();
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    private ClipstemsSettings Collect() => _engine.Settings with
    {
        RecordWhen = RecordOnlyDuringGames ? RecordPolicy.WhileSteamGameRuns : RecordPolicy.Always,
        RetentionMinutes = RetentionMinutes,
        OutputFolder = !ClipstemsPaths.IsUsableOutputFolder(OutputFolder) ? _engine.Settings.OutputFolder
            : string.Equals(OutputFolder, ClipstemsPaths.DefaultOutput, StringComparison.OrdinalIgnoreCase) ? null : OutputFolder,
        StartWithWindows = StartWithWindows,
        Notifications = Notifications,
        MirrorSteamPrograms = MirrorSteamPrograms,
        Tracks = Tracks.Where(t => !t.IsFromSteam).Select(t => t.ToSettings()).ToList(),
    };

    private void Apply()
    {
        ClipstemsSettings settings;
        try
        {
            settings = SettingsStore.Validate(Collect());
        }
        catch (ArgumentException ex)
        {
            Message = $"Changes not saved: {ex.Message}";
            return;
        }
        var notes = new List<string>();
        foreach (var blank in Tracks.Where(t => !t.IsFromSteam && t.HasBlankTitle))
            notes.Add($"Enter a track name. Until then, this track keeps \"{blank.SavedTitle}\".");
        if (!ClipstemsPaths.IsUsableOutputFolder(OutputFolder))
            notes.Add($"This location can't be used. Clips will continue saving to {settings.EffectiveOutputFolder}.");

        bool startupChanged = settings.StartWithWindows != _engine.Settings.StartWithWindows;
        try
        {
            _store.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"Settings could not be saved: {ex.Message}";
            _log.LogError(ex, "Saving settings failed");
            return;
        }
        _engine.UpdateSettings(settings);
        foreach (var row in Tracks) row.MarkSaved();
        if (startupChanged && StartupRegistration.Apply(settings.StartWithWindows) is { } why) notes.Add(why);
        Message = notes.Count == 0 ? null : string.Join(" ", notes);
        OnPropertyChanged(nameof(HasGameTrack));
    }

    private void RebuildRows(ClipstemsSettings? fromSettings = null)
    {
        var settings = fromSettings ?? _engine.Settings;
        var steam = _engine.SteamSettings;
        bool wasLoading = _loading;
        _loading = true; // rebuilding is not an edit
        Tracks.Clear();
        foreach (var t in settings.Tracks)
            Tracks.Add(new TrackRowViewModel(t, fromSteam: false, DevicesFor(t.Source), ScheduleApply));
        var userIds = settings.Tracks.Select(t => t.Id).ToHashSet();
        foreach (var t in ClipstemsEngine.EffectiveTracks(settings, steam).Where(t => !userIds.Contains(t.Id)))
            Tracks.Add(new TrackRowViewModel(t, fromSteam: true, [], ScheduleApply));
        _loading = wasLoading;
        foreach (var row in Tracks) row.UpdateTimingNote(steam);
        Renumber();
        OnPropertyChanged(nameof(HasGameTrack));
    }

    /// <summary>
    /// Adds and removes only the rows for Steam's ticked programs, leaving the
    /// user's rows (and any edit in progress) alone.
    /// </summary>
    private void SyncSteamRows()
    {
        var steam = _engine.SteamSettings;
        var settings = Collect();
        var userIds = settings.Tracks.Select(t => t.Id).ToHashSet();
        var wanted = ClipstemsEngine.EffectiveTracks(settings, steam).Where(t => !userIds.Contains(t.Id)).ToList();
        var wantedIds = wanted.Select(t => t.Id).ToHashSet();
        bool changed = false;
        foreach (var stale in Tracks.Where(t => t.IsFromSteam && !wantedIds.Contains(t.Id)).ToList())
        {
            Tracks.Remove(stale);
            changed = true;
        }
        foreach (var t in wanted.Where(t => Tracks.All(r => r.Id != t.Id)))
        {
            var row = new TrackRowViewModel(t, fromSteam: true, [], ScheduleApply);
            row.UpdateTimingNote(steam);
            Tracks.Add(row);
            changed = true;
        }
        // Keep Steam's rows in the engine's order, which is the order tracks land in the clip.
        int first = Tracks.TakeWhile(t => !t.IsFromSteam).Count();
        for (int i = 0; i < wanted.Count; i++)
        {
            int at = Tracks.IndexOf(Tracks.First(r => r.Id == wanted[i].Id));
            if (at != first + i)
            {
                Tracks.Move(at, first + i);
                changed = true;
            }
        }
        if (changed) Renumber();
    }

    /// <summary>Shows where each track lands in the saved clip; Steam's own mix is always track 1.</summary>
    private void Renumber()
    {
        int n = 1;
        foreach (var row in Tracks) row.Position = row.Enabled ? $"Track {++n}" : "Off";
        int extra = n - 1;
        ClipSummary = extra == 0
            ? "Turn on at least one track below."
            : $"When you save a clip in Steam, Clipstems saves a copy with {extra} extra audio track{(extra == 1 ? "" : "s")} to {FriendlyFolder(Collect().EffectiveOutputFolder)}. " +
              "Track 1 keeps Steam's own audio; Steam's clip isn't changed.";
        // Each track keeps its current minute uncompressed; everything older is compressed when that works.
        // Opus is variable bitrate, so the compressed figure is typical, not a ceiling.
        int buffered = extra + 1; // plus the hidden timing track
        double seconds = RetentionMinutes * 60;
        double typical = buffered * (CompressedBytesPerSecond * Math.Max(0, seconds - 60) + BufferBytesPerSecond * Math.Min(60, seconds));
        double uncompressed = buffered * BufferBytesPerSecond * seconds;
        string tracks = $"{extra} track{(extra == 1 ? "" : "s")} plus one hidden track Clipstems uses to check timing";
        DiskEstimate = Ffmpeg.Locate() is not null
            ? $"Typically about {Size(typical)} of disk for {tracks}; up to {Size(uncompressed)} if the audio can't be compressed."
            : $"Up to {Size(uncompressed)} of disk for {tracks}. Installing FFmpeg makes this about 10 times smaller.";
    }

    /// <summary>A disk size rounded for reading: GB to one decimal, else MB to the next 10.</summary>
    private static string Size(double bytes) => bytes >= 1e9 ? $"{bytes / 1e9:0.#} GB" : $"{Math.Ceiling(bytes / 1e7) * 10:0} MB";

    /// <summary>"Videos\Clipstems" rather than the full path when the folder is under the user's Videos.</summary>
    private static string FriendlyFolder(string folder)
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        return folder.StartsWith(videos + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? "Videos" + folder[videos.Length..]
            : folder;
    }

    private IReadOnlyList<DeviceOption> DevicesFor(CaptureSource source) => source.Kind switch
    {
        SourceKind.Microphone => _inputs,
        SourceKind.SystemOutput => _outputs,
        _ => [],
    };

    private void RefreshLive()
    {
        EngineStatus = _engine.Status switch
        {
            Core.Engine.EngineStatus.Recording => _engine.Game is { } g ? $"Recording · {g.Name}" : "Recording",
            Core.Engine.EngineStatus.WaitingForGame => "Starts recording when you launch a Steam game",
            Core.Engine.EngineStatus.Paused => "Paused",
            Core.Engine.EngineStatus.Blocked => "Another copy of Clipstems is recording",
            _ => "Starting…",
        };

        // Steam's ticked programs can change while the window is open; follow them.
        var steam = _engine.SteamSettings;
        SyncSteamRows();

        bool recording = _engine.Status == Core.Engine.EngineStatus.Recording;
        var live = _engine.Tracks.ToDictionary(t => t.Id);
        foreach (var row in Tracks)
        {
            var status = live.GetValueOrDefault(row.Id);
            row.IsLive = recording && status?.State == RecorderState.Recording;
            row.Level = row.IsLive && status?.PeakDbfs is { } db ? Math.Clamp((db + 60) / 60, 0, 1) : 0;
            bool failing = recording && status is { Failing: true };
            row.Waiting = recording && status?.State == RecorderState.Waiting && !failing ? row.WaitingText : "";
            row.Problem = !failing ? ""
                : status!.State == RecorderState.Waiting
                    ? $"Clipstems can see {_engine.Game?.Name ?? "your game"} running but hasn't found its sound. Trying again."
                    : RetryText(row.Source.Kind);
            row.UpdateTimingNote(steam);
        }

        var issues = _engine.Issues.Where(i => i.Severity != IssueSeverity.Tip).Select(i => new IssueItem(
            i.Severity == IssueSeverity.Problem ? "\uE783" : "\uE7BA", i.Message)).ToList();
        if (!issues.SequenceEqual(Issues))
        {
            Issues.Clear();
            foreach (var i in issues) Issues.Add(i);
        }

        var checks = SteamChecklist(steam, _engine.Issues, Collect()).ToList();
        if (!checks.SequenceEqual(SteamChecks))
        {
            SteamChecks.Clear();
            foreach (var c in checks) SteamChecks.Add(c);
        }
        SteamNeedsAttention = checks.Any(c => c.Glyph == SteamCheck.Change);
    }

    private static string ProgramsNote(SteamRecordingSettings steam, ClipstemsSettings settings)
    {
        if (!settings.MirrorSteamPrograms) return "Automatic tracks for these are off in Clipstems (Tracks tab).";
        // Own track = an enabled track recording that one app alone (the user's or Steam's), as the engine decides.
        var own = ClipstemsEngine.EffectiveTracks(settings, steam)
            .Where(t => t.Source.Kind == SourceKind.Application && t.Source.Apps.Count == 1)
            .Select(t => t.Source.Apps[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shared = settings.Tracks.Where(t => t.Enabled && t.Source.Kind == SourceKind.Application && t.Source.Apps.Count > 1).ToList();
        var notes = new List<string>();
        foreach (var program in steam.Programs.Where(p => !own.Contains(p.Exe)))
        {
            var sharer = shared.FirstOrDefault(t => t.Source.Apps.Contains(program.Exe, StringComparer.OrdinalIgnoreCase));
            notes.Add(sharer is null
                ? $"{program.Label}'s separate track is off on the Tracks tab."
                : $"{program.Label}'s separate track is off. The {sharer.Title} track may still record {program.Label}.");
        }
        return notes.Count == 0 ? "Each one gets its own track." : string.Join(" ", notes);
    }

    /// <summary>Recording failures by kind; the error code itself goes to the log.</summary>
    private static string RetryText(SourceKind kind) => kind switch
    {
        SourceKind.Microphone => "Can't record this microphone. Check that it's connected and that Windows lets apps use the microphone (Settings › Privacy & security › Microphone). Trying again.",
        SourceKind.SystemOutput => "Can't record this output device. Check that it's connected. Trying again.",
        _ => "Can't record this app's sound right now. Trying again. Details are in the log (About › Open logs).",
    };

    private static IEnumerable<SteamCheck> SteamChecklist(SteamRecordingSettings steam, IReadOnlyList<EngineIssue> issues, ClipstemsSettings settings)
    {
        if (steam.AudioOption is null)
        {
            yield return new(SteamCheck.Info, "Steam's recording settings couldn't be read.",
                issues.Any(i => i.Severity == IssueSeverity.Problem) ? "See the message on the Tracks page." : "Turn on Game Recording in Steam's settings.");
            yield break;
        }

        yield return steam.RecordsSelectedPrograms
            ? new(SteamCheck.Done, "Record Audio from: Game and Selected Programs", "")
            : new(SteamCheck.Info, $"Record Audio from: {steam.AudioOptionDescription}",
                "Optional: choose \"Game and Selected Programs\" and tick apps like Discord there to give each its own track.");

        if (steam.RecordsSelectedPrograms)
            yield return steam.Programs.Count == 0
                ? new(SteamCheck.Info, "No programs ticked", "Tick an app like Discord in that list to give it its own track.")
                : new(SteamCheck.Done, $"Programs ticked: {string.Join(", ", steam.Programs.Select(p => p.Label))}",
                    ProgramsNote(steam, settings));

        bool micTrackOn = settings.Tracks.Any(t => t.Enabled && t.Source.Kind == SourceKind.Microphone);
        switch (steam.MicRecorded)
        {
            case true:
                yield return new(SteamCheck.Done, "Record Microphone: on", "");
                break;
            case false when micTrackOn:
                yield return new(SteamCheck.Change, "Record Microphone: off",
                    "Turn it on, with the same mic Clipstems records, so your mic track's timing can be double-checked against Steam's audio.");
                break;
            case null when micTrackOn:
                yield return new(SteamCheck.Info, "Steam's microphone setting couldn't be read.", "");
                break;
        }
    }
}
