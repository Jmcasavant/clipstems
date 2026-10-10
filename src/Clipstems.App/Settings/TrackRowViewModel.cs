using CommunityToolkit.Mvvm.ComponentModel;
using Clipstems.Core.Capture;
using Clipstems.Core.Settings;
using Clipstems.Core.Steam;

namespace Clipstems.App.Settings;

/// <summary>
/// A device choice. <paramref name="Id"/> is the Windows endpoint id that gets
/// saved; null means "follow the Windows default". <paramref name="Name"/> is
/// the device's own name, which older settings saved instead of the id.
/// </summary>
public sealed record DeviceOption(string Label, string? Id, string? Name = null)
{
    public override string ToString() => Label;
}

/// <summary>One row in the tracks list: a user track (editable) or one that comes from Steam's settings (read-only).</summary>
public sealed partial class TrackRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _ready;
    private string _savedTitle;

    public TrackRowViewModel(TrackSettings track, bool fromSteam, IReadOnlyList<DeviceOption> devices, Action changed)
    {
        Id = track.Id;
        Source = track.Source;
        IsFromSteam = fromSteam;
        _savedTitle = track.Title;
        _changed = changed;
        Title = track.Title;
        Enabled = track.Enabled;
        ShowDevices(devices);
        _ready = true;
    }

    /// <summary>Swaps in a fresh device list (a device was plugged in or removed), keeping the choice.</summary>
    public void UpdateDevices(IReadOnlyList<DeviceOption> devices)
    {
        _ready = false; // the list swap briefly clears the selection; that isn't the user's choice
        ShowDevices(devices);
        _ready = true;
    }

    private void ShowDevices(IReadOnlyList<DeviceOption> devices)
    {
        var saved = Source.Device;
        var match = saved is null
            ? devices.FirstOrDefault(d => d.Id is null)
            : devices.FirstOrDefault(d => string.Equals(d.Id, saved, StringComparison.OrdinalIgnoreCase))
              ?? devices.FirstOrDefault(d => string.Equals(d.Name, saved, StringComparison.OrdinalIgnoreCase));
        if (match is null && saved is not null && devices.Count > 0)
        {
            // Keep an unplugged choice visible (and saved) instead of silently switching devices.
            match = new DeviceOption(AudioDevices.IsEndpointId(saved) ? "Chosen device (not connected)" : $"{saved} (not connected)", saved);
            devices = [.. devices, match];
        }
        Devices = devices;
        SelectedDevice = match;
    }

    public string Id { get; }
    public CaptureSource Source { get; private set; }

    /// <summary>Added because the program is ticked in Steam; changed in Steam, not here.</summary>
    public bool IsFromSteam { get; }
    public bool IsEditable => !IsFromSteam;
    [ObservableProperty] public partial IReadOnlyList<DeviceOption> Devices { get; set; } = [];
    public bool HasDeviceChoice => IsEditable && Source.Kind is SourceKind.Microphone or SourceKind.SystemOutput;

    public string Glyph => Source.Kind switch
    {
        SourceKind.Microphone => "",
        SourceKind.SystemOutput => "",
        SourceKind.SteamGame => "",
        _ => "",
    };

    /// <summary>What the track records, in a line; empty when the device picker says it.</summary>
    public string Description => Source.Kind switch
    {
        _ when IsFromSteam => $"Ticked in Steam's recording settings. Change it there.",
        SourceKind.Application when Source.Apps.Count == 1 => AppNames,
        SourceKind.Application => $"Whichever one is running: {AppNames}",
        SourceKind.SteamGame => "The Steam game you're playing",
        _ => "",
    };

    /// <summary>People know these apps by name, not by their program files.</summary>
    private static readonly Dictionary<string, string> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Discord.exe"] = "Discord",
        ["DiscordPTB.exe"] = "Discord",
        ["DiscordCanary.exe"] = "Discord",
        ["Vesktop.exe"] = "Vesktop",
        ["TeamSpeak.exe"] = "TeamSpeak",
        ["ts3client_win64.exe"] = "TeamSpeak",
        ["mumble.exe"] = "Mumble",
    };

    private string AppNames => string.Join(", ", Source.Apps
        .Select(a => KnownApps.GetValueOrDefault(a) ?? System.IO.Path.GetFileNameWithoutExtension(a))
        .Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>What the track waits for while its app or game isn't running.</summary>
    public string WaitingText => Source.Kind switch
    {
        SourceKind.SteamGame => "Starts when a Steam game is running.",
        SourceKind.Application => Source.Apps.Count == 1 ? $"Starts when {AppNames} is running." : "Starts when one of these is running.",
        _ => "Waiting for the device.",
    };

    /// <summary>Live: set while recording is on but this track's source isn't there yet.</summary>
    [ObservableProperty] public partial string Waiting { get; set; } = "";

    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial bool Enabled { get; set; }
    [ObservableProperty] public partial DeviceOption? SelectedDevice { get; set; }

    /// <summary>"Track 2" etc.: where it lands in the saved clip; empty when off.</summary>
    [ObservableProperty] public partial string Position { get; set; } = "";

    /// <summary>Live: true while this track is being recorded, which shows the meter.</summary>
    [ObservableProperty] public partial bool IsLive { get; set; }

    /// <summary>Live: last second's peak, 0..1 on a -60..0 dBFS scale.</summary>
    [ObservableProperty] public partial double Level { get; set; }

    /// <summary>Live: a problem with this track, or empty.</summary>
    [ObservableProperty] public partial string Problem { get; set; } = "";

    /// <summary>Set when Steam's mix doesn't include this source, so its timing can't be checked against it.</summary>
    [ObservableProperty] public partial string TimingNote { get; set; } = "";

    partial void OnTitleChanged(string value)
    {
        if (_ready) _changed();
    }

    partial void OnEnabledChanged(bool value)
    {
        if (_ready) _changed();
    }

    partial void OnSelectedDeviceChanged(DeviceOption? value)
    {
        if (!_ready || !HasDeviceChoice) return;
        Source = Source with { Device = value?.Id };
        _changed();
    }

    /// <summary>True while the name box is empty; the track keeps its last saved name until one is typed.</summary>
    public bool HasBlankTitle => string.IsNullOrWhiteSpace(Title);

    /// <summary>The name a blank box falls back to: the last one actually saved.</summary>
    public string SavedTitle => _savedTitle;

    public TrackSettings ToSettings() => new(Id, HasBlankTitle ? _savedTitle : Title.Trim(), Enabled, Source);

    /// <summary>Called once settings were saved, so the fallback name only ever holds a saved name.</summary>
    public void MarkSaved()
    {
        if (!HasBlankTitle) _savedTitle = Title.Trim();
    }

    public void UpdateTimingNote(SteamRecordingSettings steam)
    {
        bool inSteamMix = Source.Kind switch
        {
            SourceKind.SteamGame => true,
            // Unknown (Steam's setting unread) claims nothing; only a known "off" gets the note.
            SourceKind.Microphone => steam.MicRecorded ?? true,
            // Unknown (no Steam setting read) claims nothing.
            SourceKind.Application => steam.RecordsApp(Source.Apps) ?? true,
            _ => true, // an output device may or may not be in Steam's mix; claim nothing
        };
        TimingNote = inSteamMix || !Enabled ? "" : Source.Kind == SourceKind.Microphone
            ? "Steam's Record Microphone is off, so this track's timing can't be double-checked against Steam's audio."
            : steam.RecordsSelectedPrograms
                ? "This app isn't ticked in Steam, so this track's timing can't be double-checked against Steam's audio."
                : "Steam records game audio only, so this track's timing can't be double-checked against Steam's audio.";
    }
}
