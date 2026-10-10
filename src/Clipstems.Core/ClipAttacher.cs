using System.Globalization;
using Clipstems.Core.Audio;
using Clipstems.Core.Buffer;
using Clipstems.Core.Media;
using Clipstems.Core.Steam;
using Clipstems.Core.Sync;

namespace Clipstems.Core;

/// <summary>A buffered track to add to the clip, and the title players show for it.</summary>
public sealed record ExportTrack(string Name, string Title);

public sealed record AttachOptions(
    IReadOnlyList<ExportTrack> Tracks,
    string ReferenceTrack,
    string OutputDirectory,
    bool KeepTemp = false)
{
    /// <summary>How far either side of Steam's coarse start the sync search looks.</summary>
    public TimeSpan SearchMargin { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How much of the clip's audio is correlated; more adds cost, not accuracy.</summary>
    public TimeSpan MaxProbe { get; init; } = TimeSpan.FromSeconds(60);

    public int AacBitrateKbps { get; init; } = 160;
}

/// <summary>Whether the extra tracks' alignment was confirmed against Steam's audio.</summary>
public enum SyncState
{
    /// <summary>Correlation found one clear match; offset is sample-accurate.</summary>
    Verified,

    /// <summary>No confident match (silence, or no reference audio); Steam's metadata time (±1 s) was used.</summary>
    Unverified,
}

public enum AttachStatus
{
    Written,

    /// <summary>The buffer holds no audio for this clip's window; no file was written.</summary>
    NotCovered,

    /// <summary>The clip is outside what this version handles, or its metadata is inconsistent.</summary>
    Refused,
}

/// <summary>How much of a track was recorded and how it was placed.</summary>
/// <param name="AdjustmentMs">
/// Per-track correction found by matching the track against Steam's own mix,
/// or null when the track is not audible in Steam's mix and was placed by the
/// shared clock alone.
/// </param>
public sealed record TrackCoverage(string Track, double Coverage, double? AdjustmentMs = null)
{
    /// <summary>The track's display title, for telling a person which track is short.</summary>
    public string? Title { get; init; }
}

/// <summary>Why a clip was refused, in categories a UI can explain without the technical detail in the message.</summary>
public enum AttachProblem { None, SeveralRecordings, UnsupportedClip, FfmpegMissing, ExportFailed }

public sealed record AttachResult(
    AttachStatus Status,
    string Message,
    string? OutputPath = null,
    SyncState? Sync = null,
    double? OffsetFromMetadataMs = null,
    CorrelationResult? Correlation = null,
    IReadOnlyList<TrackCoverage>? Coverage = null,
    AttachProblem Problem = AttachProblem.None,
    TimeSpan? Duration = null);

/// <summary>
/// Turns a Steam clip plus the Clipstems buffer into one MP4: Steam's video and
/// mixed audio stream-copied as track 1 (the default), each buffered track
/// cut to the clip, aligned, and added as its own titled AAC track.
/// </summary>
public sealed class ClipAttacher(SegmentStore store, Ffmpeg ffmpeg)
{
    /// <summary>Steam's manifest and clip.pb must agree to within this before timing is trusted.</summary>
    private const double MetadataToleranceMs = 5;

    /// <summary>
    /// How far a single track may be moved after global sync. Capture paths
    /// differ by tens of milliseconds (Discord ran 21 ms late of the system
    /// reference on 2026-10-10), never by more than this.
    /// </summary>
    internal static readonly TimeSpan TrackSearch = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// A track is only part of Steam's mix, so its correlation is diluted by
    /// everything else in the mix; the margin over other lags carries the decision.
    /// </summary>
    internal const double TrackMinPeak = 0.2;
    internal const double TrackMinMargin = 2.0;

    /// <summary>
    /// Loads the clip's manifest and checks it against <c>clip.pb</c>. Returns
    /// the refusal reason when the clip's timing cannot be trusted or the clip
    /// is outside what this version handles.
    /// </summary>
    public static (DashManifest? Manifest, string? Refusal) Validate(ClipInfo clip)
    {
        if (clip.TimelineCount != 1 || clip.Segments.Count != 1)
            return (null, $"Clip spans {clip.TimelineCount} timeline(s) and {clip.Segments.Count} recording segment(s); this version handles exactly one of each.");

        var segment = clip.Segments[0];
        var manifestPath = Path.Combine(clip.Directory, "video", segment.BgFolder, "session.mpd");
        if (!File.Exists(manifestPath)) return (null, $"No manifest at {manifestPath}.");
        var manifest = DashManifest.Load(manifestPath);

        double expectedPeriodMs = clip.ClipOffsetMs - segment.BgOffsetMs;
        if (Math.Abs(manifest.PeriodStart.TotalMilliseconds - expectedPeriodMs) > MetadataToleranceMs)
            return (manifest, $"clip.pb timing ({expectedPeriodMs:0} ms) disagrees with the manifest's Period start " +
                $"({manifest.PeriodStart.TotalMilliseconds:0} ms); Steam's format may have changed, so the clip's time is not trusted.");
        if (manifest.AudioRepresentations != 1)
            return (manifest, $"Manifest has {manifest.AudioRepresentations} audio representations; expected 1.");
        return (manifest, null);
    }

    public async Task<AttachResult> AttachAsync(ClipInfo clip, AttachOptions options, CancellationToken cancel = default)
    {
        var (manifest, refusal) = Validate(clip);
        if (refusal is not null)
            return new(AttachStatus.Refused, refusal, Problem: clip.TimelineCount != 1 || clip.Segments.Count != 1
                ? AttachProblem.SeveralRecordings
                : AttachProblem.UnsupportedClip);

        var duration = manifest!.AvailableDuration > TimeSpan.Zero
            ? manifest.AvailableDuration
            : TimeSpan.FromMilliseconds(clip.DurationMs);

        var temp = Path.Combine(ClipstemsPaths.Temp, $"{clip.Name}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(temp);
        try
        {
            var ffmpegManifest = Path.Combine(temp, "session.mpd");
            manifest.WriteFfmpegCopy(ffmpegManifest);

            // Steam's metadata time is the clip's logical start (Period start);
            // the media begins at the first whole segment, up to 3 s earlier.
            // Anchor on the first audio sample, which is what gets decoded and muxed.
            var times = await ffmpeg.ProbeStreamTimesAsync(ffmpegManifest, cancel);
            if (times.AudioStart is not { } audioMediaStart)
                return new(AttachStatus.Refused, "ffprobe reported no start time for the clip's audio, so it cannot be placed in time.",
                    Problem: AttachProblem.UnsupportedClip);
            long coarseTicks = clip.CoarseStartUtc.Ticks +
                (long)Math.Round((audioMediaStart - manifest.PeriodStart.TotalSeconds) * TimeSpan.TicksPerSecond);

            var (startTicks, sync, correlation, syncNote, steamProbe) = await SyncAsync(ffmpegManifest, coarseTicks, duration, options, cancel);

            var coverage = new List<TrackCoverage>();
            var inputs = new List<(ExportTrack Track, string Wav)>();
            long frames = PcmFormat.TicksToFrames(duration.Ticks);
            foreach (var track in options.Tracks)
            {
                // Only refine when global sync held: ±100 ms cannot rescue a ±1 s guess.
                var (trackStart, adjustmentMs) = sync == SyncState.Verified
                    ? RefineTrack(store, track.Name, startTicks, steamProbe)
                    : (startTicks, null);
                var window = store.ReadWindow(track.Name, trackStart, frames);
                coverage.Add(new TrackCoverage(track.Name, window.Coverage, adjustmentMs) { Title = track.Title });
                // A track with no audio is still written (silent), so track N is always the same
                // source and an editor's track layout holds from clip to clip.
                var wav = Path.Combine(temp, $"{inputs.Count}-{SafeFileName(track.Name)}.wav");
                WavFile.Write(wav, window.Samples);
                inputs.Add((track, wav));
            }
            if (coverage.All(c => c.Coverage == 0))
                return new(AttachStatus.NotCovered,
                    "The buffer has no audio for this clip's time on any exported track (was Clipstems recording, and within its retention?).",
                    Sync: sync, Coverage: coverage, Duration: duration);

            Directory.CreateDirectory(options.OutputDirectory);
            var output = UniquePath(Path.Combine(options.OutputDirectory, SafeFileName(clip.Title ?? clip.Name) + ".mp4"));
            double offsetMs = (startTicks - coarseTicks) / (double)TimeSpan.TicksPerMillisecond;
            var comment = sync == SyncState.Verified
                ? string.Create(CultureInfo.InvariantCulture, $"Clipstems sync verified: {offsetMs:+0.0;-0.0} ms from Steam metadata, correlation {correlation!.Peak:0.00}")
                : $"Clipstems sync unverified: {syncNote}";

            await ffmpeg.RunFfmpegAsync(MuxArguments(ffmpegManifest, inputs, times.AudioLead, options.AacBitrateKbps, comment, output), cancel);

            var message = sync == SyncState.Verified
                ? $"Sync verified ({offsetMs:+0.0;-0.0} ms from Steam's metadata time)."
                : $"Sync unverified: {syncNote}. Tracks placed at Steam's metadata time, which can be off by about a second.";
            return new(AttachStatus.Written, message, output, sync, offsetMs, correlation, coverage, Duration: duration);
        }
        finally
        {
            if (!options.KeepTemp)
            {
                try { Directory.Delete(temp, recursive: true); }
                catch (IOException) { }
            }
        }
    }

    /// <summary>
    /// Moves one track onto Steam's clock when its sound is in Steam's mix:
    /// correlates the track (±<see cref="TrackSearch"/> around the globally
    /// synced start) against Steam's audio. Returns the start unchanged and a
    /// null adjustment when the match is not clear, e.g. a mic Steam does not record.
    /// </summary>
    internal static (long StartTicks, double? AdjustmentMs) RefineTrack(SegmentStore store, string track, long startTicks, float[]? steamProbe)
    {
        if (steamProbe is not { Length: > 0 }) return (startTicks, null);
        long margin = TrackSearch.Ticks;
        long probeTicks = (long)steamProbe.Length * TimeSpan.TicksPerSecond / Downmix.SyncRate;
        var window = store.ReadWindow(track, startTicks - margin, PcmFormat.TicksToFrames(probeTicks + 2 * margin));
        if (window.Coverage < 0.5) return (startTicks, null);
        var match = Correlator.Locate(Downmix.ToSyncRate(window.Samples), steamProbe, exclusionSamples: Downmix.SyncRate / 100);
        if (match is null || match.Peak < TrackMinPeak || match.Margin < TrackMinMargin) return (startTicks, null);
        long refined = startTicks - margin + (long)Math.Round(match.LagSamples * TimeSpan.TicksPerSecond / Downmix.SyncRate);
        return (refined, (refined - startTicks) / (double)TimeSpan.TicksPerMillisecond);
    }

    private async Task<(long StartTicks, SyncState Sync, CorrelationResult? Correlation, string? Note, float[]? SteamProbe)> SyncAsync(
        string ffmpegManifest, long coarseTicks, TimeSpan duration, AttachOptions options, CancellationToken cancel)
    {
        var probeDuration = duration < options.MaxProbe ? duration : options.MaxProbe;
        long refStart = coarseTicks - options.SearchMargin.Ticks;
        long refFrames = PcmFormat.TicksToFrames(probeDuration.Ticks + 2 * options.SearchMargin.Ticks);
        var reference = store.ReadWindow(options.ReferenceTrack, refStart, refFrames);
        if (reference.Coverage < 0.5)
            return (coarseTicks, SyncState.Unverified, null,
                $"reference track \"{options.ReferenceTrack}\" covers {reference.Coverage:P0} of the search window", null);

        var steamPcm = await ffmpeg.DecodeAudioMonoAsync(ffmpegManifest, probeDuration, Downmix.SyncRate, cancel);
        var probe = Downmix.FromS16Mono(steamPcm);
        var correlation = Correlator.Locate(Downmix.ToSyncRate(reference.Samples), probe,
            exclusionSamples: Downmix.SyncRate / 100);
        if (correlation is null)
            return (coarseTicks, SyncState.Unverified, null, "Steam's audio or the reference is silent", probe);
        if (!Correlator.IsConfident(correlation))
            return (coarseTicks, SyncState.Unverified, correlation,
                string.Create(CultureInfo.InvariantCulture,
                    $"no clear match (correlation {correlation.Peak:0.00}, margin {correlation.Margin:0.0}×)"), probe);

        long start = refStart + (long)Math.Round(correlation.LagSamples * TimeSpan.TicksPerSecond / Downmix.SyncRate);
        return (start, SyncState.Verified, correlation, null, probe);
    }

    internal static List<string> MuxArguments(string manifest, IReadOnlyList<(ExportTrack Track, string Wav)> inputs,
        double audioLead, int bitrateKbps, string comment, string output)
    {
        var args = new List<string> { "-i", manifest };
        foreach (var (_, wav) in inputs) args.AddRange(["-itsoffset", Ffmpeg.Seconds(audioLead), "-i", wav]);
        args.AddRange(["-map", "0:v:0", "-map", "0:a:0"]);
        for (int i = 0; i < inputs.Count; i++) args.AddRange(["-map", $"{i + 1}:a:0"]);
        args.AddRange(["-c:v", "copy", "-c:a:0", "copy"]);
        args.AddRange(["-metadata:s:a:0", "title=Steam mix", "-metadata:s:a:0", "handler_name=Steam mix", "-disposition:a:0", "default"]);
        for (int i = 0; i < inputs.Count; i++)
        {
            int a = i + 1;
            var title = inputs[i].Track.Title;
            args.AddRange([$"-c:a:{a}", "aac", $"-b:a:{a}", $"{bitrateKbps}k",
                $"-metadata:s:a:{a}", $"title={title}", $"-metadata:s:a:{a}", $"handler_name={title}", $"-disposition:a:{a}", "0"]);
        }
        args.AddRange(["-metadata", $"comment={comment}", "-movflags", "+faststart", "-y", output]);
        return args;
    }

    internal static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "clip" : cleaned;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
