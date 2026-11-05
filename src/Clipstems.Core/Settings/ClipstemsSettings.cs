using System.Text.Json;
using System.Text.Json.Serialization;
using Clipstems.Core.Capture;

namespace Clipstems.Core.Settings;

public enum RecordPolicy
{
    /// <summary>Record while Steam reports a running game, and briefly after it exits.</summary>
    WhileSteamGameRuns,

    /// <summary>Record whenever Clipstems runs.</summary>
    Always,
}

/// <summary>One track the user wants on their clips.</summary>
/// <param name="Id">Stable key; names the buffer folder, so it never changes after creation.</param>
/// <param name="Title">Shown to the user and written as the track's name in the MP4.</param>
public sealed record TrackSettings(string Id, string Title, bool Enabled, CaptureSource Source);

public sealed record ClipstemsSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public RecordPolicy RecordWhen { get; init; } = RecordPolicy.WhileSteamGameRuns;
    public int RetentionMinutes { get; init; } = 120;

    /// <summary>Null means <see cref="ClipstemsPaths.DefaultOutput"/>.</summary>
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
    public string EffectiveOutputFolder => string.IsNullOrWhiteSpace(OutputFolder) ? ClipstemsPaths.DefaultOutput : OutputFolder;

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

    public static SettingsStore Default => new(System.IO.Path.Combine(ClipstemsPaths.DataRoot, "settings.json"));

    /// <summary>
    /// The saved settings, or defaults when there are none. A file that does
    /// not parse is renamed to <c>settings.json.bad-&lt;time&gt;</c> and
    /// reported through <paramref name="problem"/>, so the user's edits are not lost.
    /// </summary>
    public ClipstemsSettings Load(out string? problem)
    {
        problem = null;
        if (!File.Exists(Path)) return new ClipstemsSettings();
        string text;
        try
        {
            text = File.ReadAllText(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or unreadable (antivirus, permissions): the file may be fine, so leave it in place.
            problem = $"Settings could not be read ({ex.Message}); using defaults for now without changing {Path}.";
            return new ClipstemsSettings();
        }
        try
        {
            var settings = JsonSerializer.Deserialize<ClipstemsSettings>(text, ClipstemsSettings.Json)
                           ?? throw new JsonException("empty document");
            if (settings.Version > ClipstemsSettings.CurrentVersion)
            {
                problem = $"{Path} was written by a newer Clipstems (version {settings.Version}); using defaults without overwriting it.";
                return new ClipstemsSettings();
            }
            return Validate(settings);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            var aside = $"{Path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(Path, aside);
                problem = $"Settings could not be read ({ex.Message}); kept as {aside} and reset to defaults.";
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                problem = $"Settings could not be read ({ex.Message}) or moved aside ({moveEx.Message}); using defaults for now.";
            }
            return new ClipstemsSettings();
        }
    }

    /// <summary>
    /// Writes the settings so that a crash or power cut leaves either the old
    /// file or the new one, never a partial one. Refuses to replace a file a
    /// newer Clipstems wrote, so trying an older version never loses settings.
    /// </summary>
    /// <exception cref="IOException">The file could not be written, or belongs to a newer version.</exception>
    public void Save(ClipstemsSettings settings)
    {
        var json = JsonSerializer.Serialize(Validate(settings), ClipstemsSettings.Json);
        if (SavedVersion() is { } saved && saved > ClipstemsSettings.CurrentVersion)
            throw new IOException($"{Path} was written by a newer Clipstems (version {saved}), so it is not overwritten.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = $"{Path}.{Environment.ProcessId}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true); // the data must be on disk before the rename can be
            }
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The <c>version</c> of the file on disk, or null when there is none or it does not parse.</summary>
    private int? SavedVersion()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(Path));
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("version", out var v) && v.TryGetInt32(out var n) ? n : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Rejects settings that would break the buffer layout (duplicate or unsafe track ids).</summary>
    public static ClipstemsSettings Validate(ClipstemsSettings settings)
    {
        // System.Text.Json does not enforce nullable annotations, so a hand-edited
        // file can hold nulls where the types say there are none.
        if (settings.Tracks is null) throw new ArgumentException("The track list is missing.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in settings.Tracks)
        {
            if (t is null) throw new ArgumentException("The track list has an empty entry.");
            if (t.Source is null) throw new ArgumentException($"Track \"{t.Title ?? t.Id}\" has no source.");
            if (t.Title is null) throw new ArgumentException($"Track \"{t.Id}\" has no name.");
            if (t.Source.Apps is null || t.Source.Apps.Any(a => string.IsNullOrWhiteSpace(a)))
                throw new ArgumentException($"Track \"{t.Title}\" has a blank app name.");
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
