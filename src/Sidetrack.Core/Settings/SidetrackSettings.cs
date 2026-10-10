using System.Text.Json;
using System.Text.Json.Serialization;
using Sidetrack.Core.Capture;

namespace Sidetrack.Core.Settings;

public enum RecordPolicy
{
    /// <summary>Record while Steam reports a running game, and briefly after it exits.</summary>
    WhileSteamGameRuns,

    /// <summary>Record whenever Sidetrack runs.</summary>
    Always,
}

/// <summary>One track the user wants on their clips.</summary>
/// <param name="Id">Stable key; names the buffer folder, so it never changes after creation.</param>
/// <param name="Title">Shown to the user and written as the track's name in the MP4.</param>
public sealed record TrackSettings(string Id, string Title, bool Enabled, CaptureSource Source);

public sealed record SidetrackSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public RecordPolicy RecordWhen { get; init; } = RecordPolicy.WhileSteamGameRuns;
    public int RetentionMinutes { get; init; } = 120;

    /// <summary>Null means <see cref="SidetrackPaths.DefaultOutput"/>.</summary>
    public string? OutputFolder { get; init; }

    public bool StartWithWindows { get; init; } = true;

    /// <summary>
    /// Add a track for every program ticked in Steam's "Record Audio from…"
    /// list, so Steam's mix is split into the same parts Steam records.
    /// </summary>
    public bool MirrorSteamPrograms { get; init; } = true;
    public bool Notifications { get; init; } = true;
    public IReadOnlyList<TrackSettings> Tracks { get; init; } = DefaultTracks;

    [JsonIgnore]
    public string EffectiveOutputFolder => string.IsNullOrWhiteSpace(OutputFolder) ? SidetrackPaths.DefaultOutput : OutputFolder;

    public static readonly IReadOnlyList<TrackSettings> DefaultTracks =
    [
        new("mic", "Mic", true, new CaptureSource(SourceKind.Microphone)),
        new("game", "Game", true, new CaptureSource(SourceKind.SteamGame)),
    ];

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

/// <summary>Loads and saves settings as JSON. Saves are atomic; a corrupt file is kept aside, never overwritten silently.</summary>
public sealed class SettingsStore(string path)
{
    public string Path { get; } = path;

    public static SettingsStore Default => new(System.IO.Path.Combine(SidetrackPaths.DataRoot, "settings.json"));

    /// <summary>
    /// The saved settings, or defaults when there are none. A file that does
    /// not parse is renamed to <c>settings.json.bad-&lt;time&gt;</c> and
    /// reported through <paramref name="problem"/>, so the user's edits are not lost.
    /// </summary>
    public SidetrackSettings Load(out string? problem)
    {
        problem = null;
        if (!File.Exists(Path)) return new SidetrackSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<SidetrackSettings>(File.ReadAllText(Path), SidetrackSettings.Json)
                           ?? throw new JsonException("empty document");
            if (settings.Version > SidetrackSettings.CurrentVersion)
            {
                problem = $"{Path} was written by a newer Sidetrack (version {settings.Version}); using defaults without overwriting it.";
                return new SidetrackSettings();
            }
            return Validate(settings);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            var aside = $"{Path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(Path, aside);
            problem = $"Settings could not be read ({ex.Message}); kept as {aside} and reset to defaults.";
            return new SidetrackSettings();
        }
    }

    public void Save(SidetrackSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Validate(settings), SidetrackSettings.Json));
        File.Move(temp, Path, overwrite: true);
    }

    /// <summary>Rejects settings that would break the buffer layout (duplicate or unsafe track ids).</summary>
    public static SidetrackSettings Validate(SidetrackSettings settings)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in settings.Tracks)
        {
            if (string.IsNullOrWhiteSpace(t.Id) || t.Id.StartsWith('_') || t.Id.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException($"Track id \"{t.Id}\" is not a valid folder name (and must not start with _).");
            if (!ids.Add(t.Id)) throw new ArgumentException($"Track id \"{t.Id}\" is used twice.");
            if (t.Source.Kind == SourceKind.Application && t.Source.Apps.Count == 0 && t.Source.ProcessId is null)
                throw new ArgumentException($"Track \"{t.Title}\" records an app but names none.");
        }
        if (settings.RetentionMinutes is < 5 or > 24 * 60)
            throw new ArgumentException("Buffer length must be between 5 minutes and 24 hours.");
        return settings;
    }
}
