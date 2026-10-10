using Microsoft.Win32;

namespace Clipstems.Core.Steam;

/// <summary>A program ticked under Steam > Settings > Game Recording > "Record Audio from…".</summary>
/// <param name="Exe">Its exe name, e.g. <c>Discord.exe</c>.</param>
/// <param name="Label">The name Steam shows for it.</param>
public sealed record SteamAudioProgram(string Exe, string Label);

/// <summary>
/// Steam's Game Recording settings for one account: the <c>GameRecording</c>
/// block of <c>localconfig.vdf</c>, as observed 2026-10-10.
/// </summary>
/// <param name="AudioOption">
/// Raw <c>Recording_Audio_Option</c>: "0" Game Audio Only and "2" Game and
/// Selected Programs (each observed against the settings screen, 2026-10-10);
/// "1" All System Audio, inferred as the dropdown's remaining choice.
/// </param>
/// <param name="MicInMix">Raw <c>Audio_Mic</c>; "1" means "Record Microphone" is on.</param>
/// <param name="Programs">The ticked programs (<c>ExtraAudioSessions</c>).</param>
public sealed record SteamRecordingSettings(
    string? BackgroundRecordPath,
    string? BackgroundRecordMode,
    string? AudioOption,
    string? MicInMix,
    IReadOnlyList<SteamAudioProgram> Programs)
{
    public const string GameAudioOnly = "0";
    public const string AllSystemAudio = "1";
    public const string GameAndSelectedPrograms = "2";

    public static readonly SteamRecordingSettings Empty = new(null, null, null, null, []);

    public bool RecordsSelectedPrograms => AudioOption == GameAndSelectedPrograms;

    /// <summary>The programs whose audio is in Steam's own track: the ticked ones, when Steam uses that list.</summary>
    public IReadOnlyList<SteamAudioProgram> ProgramsInMix => RecordsSelectedPrograms ? Programs : [];

    /// <summary>
    /// Whether Steam's track contains the microphone; null when Steam's
    /// recording settings weren't read at all. An absent <c>Audio_Mic</c> in a
    /// block that was read counts as off, Steam's default for the setting
    /// (assumed: only the "1" value has been observed).
    /// </summary>
    public bool? MicRecorded => MicInMix switch
    {
        "1" => true,
        "0" => false,
        _ => ReferenceEquals(this, Empty) || (AudioOption is null && BackgroundRecordMode is null) ? null : false,
    };

    /// <summary>Whether Steam's own track contains this app's sound; null when Steam's setting isn't known.</summary>
    public bool? RecordsApp(IEnumerable<string> exes) => AudioOption switch
    {
        GameAudioOnly => false,
        AllSystemAudio => true,
        GameAndSelectedPrograms => Programs.Any(p => exes.Contains(p.Exe, StringComparer.OrdinalIgnoreCase)),
        _ => null,
    };

    public string AudioOptionDescription => AudioOption switch
    {
        GameAudioOnly => "Game Audio Only",
        AllSystemAudio => "All System Audio",
        GameAndSelectedPrograms => "Game and Selected Programs",
        null => "(not set)",
        _ => "a setting Clipstems doesn't recognise",
    };

    public static SteamRecordingSettings FromLocalConfig(VdfNode localConfig)
    {
        if (localConfig.Find("GameRecording") is not { } block) return Empty;
        var programs = new List<SteamAudioProgram>();
        foreach (var session in block.Child("ExtraAudioSessions")?.Children ?? [])
        {
            if (ExeFromSessionName(session["name"]) is { } exe)
                programs.Add(new SteamAudioProgram(exe, session["Label"] ?? Path.GetFileNameWithoutExtension(exe)));
        }
        return new SteamRecordingSettings(
            block["BackgroundRecordPath"], block["BackgroundRecordMode"], block["Recording_Audio_Option"], block["Audio_Mic"], programs);
    }

    /// <summary>
    /// An audio session id looks like
    /// <c>{device}|\Device\HarddiskVolume3\...\Discord.exe%b{guid}</c>; the exe
    /// name is what survives app updates, which change the folder.
    /// </summary>
    internal static string? ExeFromSessionName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        int bar = name.IndexOf('|');
        var path = bar >= 0 ? name[(bar + 1)..] : name;
        int marker = path.IndexOf("%b", StringComparison.Ordinal);
        if (marker >= 0) path = path[..marker];
        var file = path[(path.LastIndexOf('\\') + 1)..];
        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file : null;
    }
}

public sealed record SteamUser(string AccountId, string UserDataDirectory, SteamRecordingSettings Settings)
{
    /// <summary>
    /// Folders that hold <c>clips/</c>. The configured path first, then the
    /// default under userdata, because clips made before a path change stay
    /// where they were.
    /// </summary>
    public IReadOnlyList<string> RecordingRoots()
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(Settings.BackgroundRecordPath) && Directory.Exists(Settings.BackgroundRecordPath))
            roots.Add(Path.GetFullPath(Settings.BackgroundRecordPath));
        var fallback = Path.Combine(UserDataDirectory, "gamerecordings");
        if (Directory.Exists(fallback) && !roots.Contains(Path.GetFullPath(fallback), StringComparer.OrdinalIgnoreCase))
            roots.Add(Path.GetFullPath(fallback));
        return roots;
    }
}

/// <summary>Locates Steam and its accounts. Read-only: nothing here writes to Steam.</summary>
public static class SteamInstall
{
    public static string? FindSteamPath()
    {
        var fromRegistry = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (!string.IsNullOrEmpty(fromRegistry) && Directory.Exists(fromRegistry)) return Path.GetFullPath(fromRegistry);
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }

    /// <summary>Steam's running app id, or null when no game is running or Steam is closed.</summary>
    public static uint? RunningAppId() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "RunningAppID", 0) is int id && id != 0 ? (uint)id : null;

    public static IReadOnlyList<SteamUser> Users(string steamPath)
    {
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) return [];
        var users = new List<SteamUser>();
        foreach (var dir in Directory.GetDirectories(userdata))
        {
            var id = Path.GetFileName(dir);
            if (!id.All(char.IsAsciiDigit) || id == "0") continue;
            users.Add(new SteamUser(id, dir, ReadSettings(Path.Combine(dir, "config", "localconfig.vdf"))));
        }
        return users;
    }

    /// <summary>
    /// The signed-in account when Steam reports one, else the account whose
    /// config changed most recently.
    /// </summary>
    public static SteamUser? ActiveUser(string steamPath)
    {
        var users = Users(steamPath);
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", 0) is int active && active != 0)
        {
            var match = users.FirstOrDefault(u => u.AccountId == ((uint)active).ToString());
            if (match is not null) return match;
        }
        return users.OrderByDescending(u => LastWrite(Path.Combine(u.UserDataDirectory, "config", "localconfig.vdf"))).FirstOrDefault();
    }

    private static SteamRecordingSettings ReadSettings(string localConfig)
    {
        if (!File.Exists(localConfig)) return SteamRecordingSettings.Empty;
        try
        {
            // Steam rewrites this file while running; share so a read never blocks it.
            using var stream = new FileStream(localConfig, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return SteamRecordingSettings.FromLocalConfig(Vdf.Parse(reader.ReadToEnd()));
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            return SteamRecordingSettings.Empty;
        }
    }

    private static DateTime LastWrite(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
}
