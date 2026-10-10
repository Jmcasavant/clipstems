using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Sidetrack.Core.Steam;

/// <summary>Steam's recording-related settings for one account, read from <c>localconfig.vdf</c>.</summary>
/// <param name="AudioOption">Raw <c>Recording_Audio_Option</c>; its value meanings are not documented.</param>
/// <param name="MicInMix">Raw <c>Audio_Mic</c>; "1" means Steam mixes the microphone into its track.</param>
public sealed record SteamRecordingSettings(
    string? BackgroundRecordPath,
    string? BackgroundRecordMode,
    string? AudioOption,
    string? MicInMix);

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
public static partial class SteamInstall
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
            var config = Path.Combine(dir, "config", "localconfig.vdf");
            var values = File.Exists(config) ? ReadVdfValues(File.ReadAllText(config)) : [];
            users.Add(new SteamUser(id, dir, new SteamRecordingSettings(
                values.GetValueOrDefault("BackgroundRecordPath"),
                values.GetValueOrDefault("BackgroundRecordMode"),
                values.GetValueOrDefault("Recording_Audio_Option"),
                values.GetValueOrDefault("Audio_Mic"))));
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

    /// <summary>
    /// Flat key/value scan of a VDF text file: the first occurrence of each
    /// quoted key with a quoted value. Enough for the handful of settings
    /// Sidetrack reads; it does not model nesting.
    /// </summary>
    public static Dictionary<string, string> ReadVdfValues(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in KeyValue().Matches(text))
        {
            var key = Unescape(m.Groups[1].Value);
            values.TryAdd(key, Unescape(m.Groups[2].Value));
        }
        return values;
    }

    private static string Unescape(string s) => s.Replace("\\\\", "\\").Replace("\\\"", "\"");

    private static DateTime LastWrite(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

    [GeneratedRegex("""
        "((?:[^"\\]|\\.)*)"[ \t]+"((?:[^"\\]|\\.)*)"
        """)]
    private static partial Regex KeyValue();
}
