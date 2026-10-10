using System.Text.Json.Serialization;

namespace Clipstems.Core.Capture;

public enum SourceKind
{
    /// <summary>An input device: the Windows default, or one matched by name.</summary>
    Microphone,

    /// <summary>Loopback of an output device: the Windows default, or one matched by name.</summary>
    SystemOutput,

    /// <summary>The process tree of the first running app from a list of exe names.</summary>
    Application,

    /// <summary>The process tree of the game Steam reports as running.</summary>
    SteamGame,

    /// <summary>Every app's audio except Clipstems' own, on any device.</summary>
    AllExceptSelf,
}

/// <summary>
/// Where a track's audio comes from. CLI spec forms: <c>mic</c>,
/// <c>mic:&lt;device name part&gt;</c>, <c>system</c>, <c>system:&lt;device&gt;</c>,
/// <c>app:&lt;exe&gt;[|&lt;exe&gt;...]</c>, <c>pid:&lt;id&gt;</c> (diagnostics),
/// <c>game</c>, <c>all</c>.
/// </summary>
public sealed record CaptureSource(SourceKind Kind)
{
    /// <summary>Device name part for mic and output sources; null follows the Windows default.</summary>
    public string? Device { get; init; }

    /// <summary>Exe names for application sources; the first one running is captured.</summary>
    public IReadOnlyList<string> Apps { get; init; } = [];

    /// <summary>A fixed process tree (diagnostics only).</summary>
    public uint? ProcessId { get; init; }

    /// <summary>Common voice-chat apps, in the order the default voice track tries them.</summary>
    public static readonly IReadOnlyList<string> VoiceChatApps =
    [
        "Discord.exe", "DiscordPTB.exe", "DiscordCanary.exe", "Vesktop.exe",
        "TeamSpeak.exe", "ts3client_win64.exe", "mumble.exe",
    ];

    /// <summary>True when the capture should restart if the Windows default device changes.</summary>
    [JsonIgnore]
    public bool FollowsDefaultDevice => Kind is SourceKind.Microphone or SourceKind.SystemOutput && Device is null;

    public static CaptureSource Parse(string spec)
    {
        var (head, tail) = spec.IndexOf(':') is var i and >= 0 ? (spec[..i], spec[(i + 1)..]) : (spec, null);
        tail = string.IsNullOrWhiteSpace(tail) ? null : tail.Trim();
        return head.Trim().ToLowerInvariant() switch
        {
            "mic" => new CaptureSource(SourceKind.Microphone) { Device = tail },
            "system" => new CaptureSource(SourceKind.SystemOutput) { Device = tail },
            "app" when tail is not null => new CaptureSource(SourceKind.Application)
            {
                Apps = tail.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ExeName).ToList(),
            },
            "pid" when uint.TryParse(tail, out var pid) && pid > 0 => new CaptureSource(SourceKind.Application) { ProcessId = pid },
            "game" when tail is null => new CaptureSource(SourceKind.SteamGame),
            "all" when tail is null => new CaptureSource(SourceKind.AllExceptSelf),
            _ => throw new FormatException(
                $"Unknown source \"{spec}\". Use mic, mic:<device>, system, system:<device>, app:<name.exe>[|<name.exe>], game, all or pid:<id>."),
        };
    }

    public static string ExeName(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";

    public override string ToString() => Kind switch
    {
        SourceKind.Microphone => Device is null ? "default microphone" : $"microphone matching \"{Device}\"",
        SourceKind.SystemOutput => Device is null ? "default output device" : $"output device matching \"{Device}\"",
        SourceKind.Application when ProcessId is not null => $"process tree {ProcessId}",
        SourceKind.Application => Apps.Count == 1 ? Apps[0] : $"first running of {string.Join(", ", Apps)}",
        SourceKind.SteamGame => "the running Steam game",
        _ => "all apps except Clipstems",
    };

    /// <summary>Stable identity for change detection (records compare lists by reference).</summary>
    [JsonIgnore]
    public string Key => $"{Kind}|{Device}|{string.Join('|', Apps).ToLowerInvariant()}|{ProcessId}";
}
