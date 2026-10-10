using Clipstems.Core;

namespace Clipstems.Core.Engine;

/// <summary>
/// What a clip's outcome means, in words for the person who saved it. The
/// technical detail (correlation, manifests, track ids) stays in the log.
/// </summary>
public static class ClipText
{
    /// <summary>
    /// A gap this long or longer is worth telling someone about; shorter ones are
    /// device hiccups the gap filler already covered with silence.
    /// </summary>
    private static readonly TimeSpan NoticeableGap = TimeSpan.FromSeconds(1);

    public static (string Headline, string Detail, bool IsWarning) Describe(ClipOutcome outcome)
    {
        var result = outcome.Result;
        switch (result.Status)
        {
            case AttachStatus.Written:
                var tracks = result.Coverage ?? [];
                var missing = tracks.Where(c => c.Coverage == 0).Select(Name).ToList();
                var notes = new List<string>();
                if (result.Sync != SyncState.Verified)
                    notes.Add("Timing couldn't be checked against Steam's audio, so the extra tracks may be up to about a second early or late.");
                foreach (var c in tracks.Where(c => c.Coverage > 0 && c.Coverage < 1))
                {
                    if (result.Duration is { } duration)
                    {
                        var gap = duration * (1 - c.Coverage);
                        if (gap >= NoticeableGap)
                            notes.Add($"{Name(c)} is missing about {Math.Round(gap.TotalSeconds):0} second{(Math.Round(gap.TotalSeconds) == 1 ? "" : "s")} of audio.");
                    }
                    else if (c.Coverage < 0.98)
                    {
                        notes.Add($"{Name(c)} is missing audio for part of this clip.");
                    }
                }
                if (missing.Count > 0) notes.Add($"{Join(missing)} had no audio, so {(missing.Count == 1 ? "its track is" : "their tracks are")} silent.");
                return ($"Clip saved with {tracks.Count} extra track{(tracks.Count == 1 ? "" : "s")}",
                    notes.Count == 0 ? "Click to show it." : string.Join(" ", notes),
                    notes.Count > 0);

            case AttachStatus.NotCovered:
                return ("No extra audio for this clip",
                    "It may be older than the audio Clipstems keeps, or the tracks weren't recording then.", true);

            default:
                return ("Couldn't add tracks to a clip", result.Problem switch
                {
                    AttachProblem.SeveralRecordings => "This clip combines several recordings. Clipstems handles clips from one recording for now.",
                    AttachProblem.FfmpegMissing => "FFmpeg is missing. Install it and save the clip again.",
                    AttachProblem.UnsupportedClip => "Steam saved this clip in a way Clipstems doesn't understand yet.",
                    _ => "Something went wrong while saving it. The log has details.",
                }, true);
        }
    }

    private static string Name(TrackCoverage c) => c.Title ?? c.Track;

    private static string Join(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}
