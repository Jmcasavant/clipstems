using System.Globalization;
using Clipstems.Core.Audio;

namespace Clipstems.Core.Buffer;

/// <summary>
/// One buffered file: its first frame's UTC time and its length. A segment is
/// WAV while it is recorded and compressed once closed (<see cref="Compressed"/>).
/// </summary>
public sealed record Segment(string Path, long StartTicks, long Frames, bool Compressed = false)
{
    public long EndTicks => StartTicks + PcmFormat.FramesToTicks(Frames);
}

/// <summary>Audio for a requested window, plus how much of it was actually recorded.</summary>
public sealed record TrackWindow(short[] Samples, long Frames, long CoveredFrames)
{
    public double Coverage => Frames == 0 ? 0 : (double)CoveredFrames / Frames;
}

/// <summary>
/// The rolling buffer on disk: <c>&lt;root&gt;/&lt;track&gt;/&lt;utc ticks&gt;.wav</c>
/// while recording, then <c>&lt;utc ticks&gt;-&lt;frames&gt;.opus</c> once
/// compressed. A file's name is the UTC time of its first frame (and, when
/// compressed, its length), so the store needs no index and survives a crash
/// of the recorder.
/// </summary>
public sealed class SegmentStore(string root, ISegmentCodec? codec = null)
{
    public string Root { get; } = root;

    /// <summary>Reads compressed segments; without it they count as unrecorded.</summary>
    public ISegmentCodec? Codec { get; } = codec;

    public string TrackDirectory(string track) => System.IO.Path.Combine(Root, track);

    public IReadOnlyList<string> Tracks() =>
        Directory.Exists(Root)
            ? Directory.GetDirectories(Root).Select(System.IO.Path.GetFileName).OfType<string>().Order().ToList()
            : [];

    public static string SegmentFileName(long startTicks) => startTicks.ToString(CultureInfo.InvariantCulture) + ".wav";

    public static string CompressedFileName(long startTicks, long frames, string extension) =>
        string.Create(CultureInfo.InvariantCulture, $"{startTicks}-{frames}{extension}");

    /// <summary>Parses <c>&lt;ticks&gt;-&lt;frames&gt;</c>; anything else (temporary files included) is not a segment.</summary>
    public static bool TryParseCompressedName(string nameWithoutExtension, out long startTicks, out long frames)
    {
        startTicks = frames = 0;
        var parts = nameWithoutExtension.Split('-');
        return parts.Length == 2 &&
               long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out startTicks) &&
               long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out frames);
    }

    /// <summary>
    /// Only real compressed segments count: anything else in a track folder
    /// (a backup, a stray .tmp) is neither read nor pruned.
    /// </summary>
    private bool IsCompressedExtension(string extension) =>
        extension.Equals(".opus", StringComparison.OrdinalIgnoreCase) ||
        (Codec is not null && extension.Equals(Codec.Extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>How often a read that lost a race with the compactor is retried.</summary>
    private const int ReadAttempts = 4;

    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(100);

    public IReadOnlyList<Segment> List(string track)
    {
        var dir = TrackDirectory(track);
        if (!Directory.Exists(dir)) return [];
        var byStart = new Dictionary<long, Segment>();
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles())
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(file.Name);
            if (file.Extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var start)) continue;
                long frames;
                try { frames = WavFile.FrameCount(file.Length); }
                catch (IOException) { continue; }
                // The WAV is exact; it wins over a compressed copy left by an interrupted swap.
                if (frames > 0) byStart[start] = new Segment(file.FullName, start, frames);
            }
            else if (IsCompressedExtension(file.Extension) && TryParseCompressedName(name, out var start, out var frames) &&
                     frames > 0 && !byStart.ContainsKey(start))
            {
                byStart[start] = new Segment(file.FullName, start, frames, Compressed: true);
            }
        }
        var segments = byStart.Values.ToList();
        segments.Sort((a, b) => a.StartTicks.CompareTo(b.StartTicks));
        return segments;
    }

    /// <summary>
    /// Reads <paramref name="frames"/> frames of <paramref name="track"/> from
    /// <paramref name="startTicks"/> (UTC). Each segment lands at its own
    /// timestamp; unrecorded stretches stay silent and are excluded from
    /// <see cref="TrackWindow.CoveredFrames"/>.
    /// </summary>
    public TrackWindow ReadWindow(string track, long startTicks, long frames, CancellationToken cancel = default)
    {
        var samples = new short[checked(frames * PcmFormat.Channels)];
        var intervals = new List<(long Start, long End)>();
        foreach (var seg in List(track))
        {
            long offset = PcmFormat.TicksToFrames(seg.StartTicks - startTicks);
            long from = Math.Max(0, offset);
            long to = Math.Min(frames, offset + seg.Frames);
            if (to <= from) continue;
            cancel.ThrowIfCancellationRequested();
            var dest = samples.AsSpan((int)(from * PcmFormat.Channels), (int)((to - from) * PcmFormat.Channels));
            int read = ReadSegment(track, seg, from - offset, dest, cancel);
            if (read > 0) intervals.Add((from, from + read));
        }
        return new TrackWindow(samples, frames, UnionLength(intervals));
    }

    /// <summary>
    /// Reads one segment, surviving the compactor: a WAV can be swapped for its
    /// compressed copy between listing and reading, or held for a moment while
    /// its header is repaired. A failed read looks the segment up again by its
    /// start time and retries; only a segment that stays unreadable is a gap
    /// (silent and uncovered).
    /// </summary>
    internal int ReadSegment(string track, Segment seg, long startFrame, Span<short> dest, CancellationToken cancel)
    {
        for (int attempt = 1; ; attempt++)
        {
            bool transient;
            try
            {
                return seg.Compressed ? ReadCompressed(seg, startFrame, dest, cancel) : WavFile.ReadFrames(seg.Path, startFrame, dest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                transient = true; // deleted (swapped) or briefly held
            }
            catch (InvalidDataException)
            {
                transient = false; // undecodable: only a different file can help
            }
            if (attempt == ReadAttempts) return 0;
            var again = List(track).FirstOrDefault(s => s.StartTicks == seg.StartTicks);
            if (again is null) return 0; // pruned
            if (again.Path == seg.Path)
            {
                if (!transient) return 0;
                cancel.WaitHandle.WaitOne(ReadRetryDelay);
                cancel.ThrowIfCancellationRequested();
            }
            seg = again;
        }
    }

    private int ReadCompressed(Segment seg, long startFrame, Span<short> dest, CancellationToken cancel)
    {
        if (Codec is null) throw new InvalidDataException($"No decoder for compressed segment {seg.Path}.");
        var all = Codec.Decode(seg.Path, cancel);
        // The name's frame count is what the encoder was given; a decode of any other length is not trusted.
        if (all.Length != seg.Frames * PcmFormat.Channels)
            throw new InvalidDataException($"{seg.Path} decoded to {all.Length / PcmFormat.Channels} frames, expected {seg.Frames}.");
        long available = seg.Frames - startFrame;
        if (available <= 0) return 0;
        int frames = (int)Math.Min(available, dest.Length / PcmFormat.Channels);
        all.AsSpan((int)(startFrame * PcmFormat.Channels), frames * PcmFormat.Channels).CopyTo(dest);
        return frames;
    }

    /// <summary>Latest recorded instant for a track, or null when it has nothing.</summary>
    public long? LatestEndTicks(string track) => List(track) is { Count: > 0 } s ? s.Max(x => x.EndTicks) : null;

    /// <summary>Deletes segments that ended before <paramref name="cutoffTicks"/>. Returns the count deleted.</summary>
    public int Prune(long cutoffTicks)
    {
        int deleted = 0;
        foreach (var track in Tracks())
        {
            foreach (var seg in List(track).Where(s => s.EndTicks < cutoffTicks))
            {
                try { File.Delete(seg.Path); deleted++; }
                catch (IOException) { } // still open by a reader; next pass gets it
                catch (UnauthorizedAccessException) { }
            }
        }
        return deleted;
    }

    private static long UnionLength(List<(long Start, long End)> intervals)
    {
        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
        long total = 0, curStart = long.MinValue, curEnd = long.MinValue;
        foreach (var (s, e) in intervals)
        {
            if (s > curEnd)
            {
                if (curEnd > curStart) total += curEnd - curStart;
                (curStart, curEnd) = (s, e);
            }
            else curEnd = Math.Max(curEnd, e);
        }
        if (curEnd > curStart) total += curEnd - curStart;
        return total;
    }
}
