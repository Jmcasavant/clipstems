namespace Clipstems.Core.Steam;

/// <summary>One background-recording folder a clip draws its footage from.</summary>
/// <param name="BgFolder">Folder under the clip's <c>video/</c>, e.g. <c>bg_1867240_20261010_025104</c>.</param>
/// <param name="BgOffsetMs">Where that folder starts, measured from the timeline start.</param>
public sealed record ClipSegment(string BgFolder, long DurationMs, long BgOffsetMs);

/// <summary>
/// What Clipstems reads from a clip's <c>clip.pb</c>. Field numbers were
/// observed on 2026-10-10 and are cross-checked against the clip's manifest
/// (<see cref="ClipAttacher"/>) before any timing is trusted.
/// </summary>
public sealed record ClipInfo(
    string Directory,
    uint AppId,
    string? Title,
    long TimelineStartUnix,
    long ClipOffsetMs,
    long DurationMs,
    int TimelineCount,
    IReadOnlyList<ClipSegment> Segments)
{
    public string Name => Path.GetFileName(Directory);

    /// <summary>
    /// Clip start from Steam's metadata. Accurate to about ±1 s because the
    /// timeline start is stored in whole seconds; sync refines it.
    /// </summary>
    public DateTime CoarseStartUtc =>
        DateTime.UnixEpoch.AddSeconds(TimelineStartUnix).AddMilliseconds(ClipOffsetMs);

    public static ClipInfo Read(string clipDirectory)
    {
        var path = Path.Combine(clipDirectory, "clip.pb");
        var root = ProtoReader.Read(File.ReadAllBytes(path));
        var timelines = root.Messages(1).ToList();
        if (timelines.Count == 0) throw new FormatException($"{path}: no timeline entry (field 1).");
        var first = timelines[0];

        var segments = first.Messages(5).Select(s => new ClipSegment(
            s.String(1) ?? throw new FormatException($"{path}: segment without folder name (field 1.5.1)."),
            (long)(s.Varint(3) ?? 0),
            (long)(s.Varint(10) ?? 0))).ToList();

        return new ClipInfo(
            clipDirectory,
            (uint)(root.Varint(4) ?? first.Varint(2) ?? 0),
            root.String(7),
            (long)(first.Varint(3) ?? throw new FormatException($"{path}: no timeline start (field 1.3).")),
            (long)(root.Varint(2) ?? throw new FormatException($"{path}: no clip offset (field 2).")),
            (long)(first.Varint(4) ?? 0),
            timelines.Count,
            segments);
    }
}
