namespace Sidetrack.Core.Capture;

public enum SourceKind { Microphone, SystemOutput, Application }

/// <summary>
/// Where a track's audio comes from, parsed from a short spec:
/// <c>mic</c>, <c>mic:&lt;device name part&gt;</c>, <c>system</c>,
/// <c>system:&lt;device name part&gt;</c>, <c>app:&lt;exe name&gt;</c>, or
/// <c>pid:&lt;process id&gt;</c> (one process tree, for diagnostics).
/// </summary>
public sealed record CaptureSource(SourceKind Kind, string? Target, uint? ProcessId = null)
{
    public static CaptureSource Parse(string spec)
    {
        var (head, tail) = spec.IndexOf(':') is var i and >= 0 ? (spec[..i], spec[(i + 1)..]) : (spec, null);
        tail = string.IsNullOrWhiteSpace(tail) ? null : tail.Trim();
        return head.Trim().ToLowerInvariant() switch
        {
            "mic" => new CaptureSource(SourceKind.Microphone, tail),
            "system" => new CaptureSource(SourceKind.SystemOutput, tail),
            "app" when tail is not null => new CaptureSource(SourceKind.Application,
                tail.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? tail : tail + ".exe"),
            "pid" when uint.TryParse(tail, out var pid) && pid > 0 => new CaptureSource(SourceKind.Application, $"pid {pid}", pid),
            _ => throw new FormatException($"Unknown source \"{spec}\". Use mic, mic:<device>, system, system:<device>, app:<name.exe> or pid:<id>."),
        };
    }

    public override string ToString() => Kind switch
    {
        SourceKind.Microphone => Target is null ? "mic (default input)" : $"mic matching \"{Target}\"",
        SourceKind.SystemOutput => Target is null ? "system output (default device)" : $"system output matching \"{Target}\"",
        _ when ProcessId is not null => $"process tree {ProcessId}",
        _ => $"app {Target}",
    };
}
