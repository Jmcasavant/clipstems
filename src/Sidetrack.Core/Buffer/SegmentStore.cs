using System.Globalization;
using Sidetrack.Core.Audio;

namespace Sidetrack.Core.Buffer;

/// <summary>One buffered WAV file: its first frame's UTC time and its length.</summary>
public sealed record Segment(string Path, long StartTicks, long Frames)
{
    public long EndTicks => StartTicks + PcmFormat.FramesToTicks(Frames);
}

/// <summary>Audio for a requested window, plus how much of it was actually recorded.</summary>
public sealed record TrackWindow(short[] Samples, long Frames, long CoveredFrames)
{
    public double Coverage => Frames == 0 ? 0 : (double)CoveredFrames / Frames;
}

/// <summary>
/// The rolling buffer on disk: <c>&lt;root&gt;/&lt;track&gt;/&lt;utc ticks&gt;.wav</c>.
/// A file's name is the UTC time of its first frame, so the store needs no
/// index and survives a crash of the recorder.
/// </summary>
public sealed class SegmentStore(string root)
{
    public string Root { get; } = root;

    public string TrackDirectory(string track) => System.IO.Path.Combine(Root, track);

    public IReadOnlyList<string> Tracks() =>
        Directory.Exists(Root)
            ? Directory.GetDirectories(Root).Select(System.IO.Path.GetFileName).OfType<string>().Order().ToList()
            : [];

    public static string SegmentFileName(long startTicks) => startTicks.ToString(CultureInfo.InvariantCulture) + ".wav";

    public IReadOnlyList<Segment> List(string track)
    {
        var dir = TrackDirectory(track);
        if (!Directory.Exists(dir)) return [];
        var segments = new List<Segment>();
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*.wav"))
        {
            if (!long.TryParse(System.IO.Path.GetFileNameWithoutExtension(file.Name), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var start))
                continue;
            long frames;
            try { frames = WavFile.FrameCount(file.Length); }
            catch (IOException) { continue; }
            if (frames > 0) segments.Add(new Segment(file.FullName, start, frames));
        }
        segments.Sort((a, b) => a.StartTicks.CompareTo(b.StartTicks));
        return segments;
    }

    /// <summary>
    /// Reads <paramref name="frames"/> frames of <paramref name="track"/> from
    /// <paramref name="startTicks"/> (UTC). Each segment lands at its own
    /// timestamp; unrecorded stretches stay silent and are excluded from
    /// <see cref="TrackWindow.CoveredFrames"/>.
    /// </summary>
    public TrackWindow ReadWindow(string track, long startTicks, long frames)
    {
        var samples = new short[checked(frames * PcmFormat.Channels)];
        var intervals = new List<(long Start, long End)>();
        foreach (var seg in List(track))
        {
            long offset = PcmFormat.TicksToFrames(seg.StartTicks - startTicks);
            long from = Math.Max(0, offset);
            long to = Math.Min(frames, offset + seg.Frames);
            if (to <= from) continue;
            int read;
            try
            {
                read = WavFile.ReadFrames(seg.Path, from - offset,
                    samples.AsSpan((int)(from * PcmFormat.Channels), (int)((to - from) * PcmFormat.Channels)));
            }
            catch (IOException) { continue; } // pruned or locked mid-read: leave the gap silent
            if (read > 0) intervals.Add((from, from + read));
        }
        return new TrackWindow(samples, frames, UnionLength(intervals));
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
