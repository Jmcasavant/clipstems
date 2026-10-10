using Clipstems.Core;
using Clipstems.Core.Engine;

namespace Clipstems.Tests;

public class ClipTextTests
{
    private static ClipOutcome Outcome(AttachResult result) => new(@"C:\clips\clip_1_20261010_120000", "Ace", result, DateTime.UtcNow);

    private static TrackCoverage Track(string title, double coverage) => new(title.ToLowerInvariant(), coverage) { Title = title };

    [Fact]
    public void A_fully_covered_verified_clip_is_a_plain_success()
    {
        var (headline, detail, warning) = ClipText.Describe(Outcome(new(AttachStatus.Written, "tech", "out.mp4", SyncState.Verified,
            Coverage: [Track("Mic", 1), Track("Game", 1)])));

        Assert.Equal("Clip saved with 2 extra tracks", headline);
        Assert.Equal("Click to show it.", detail);
        Assert.False(warning);
    }

    [Fact]
    public void A_track_missing_part_of_the_clip_is_named_and_warns_even_when_timing_verified()
    {
        var (headline, detail, warning) = ClipText.Describe(Outcome(new(AttachStatus.Written, "tech", "out.mp4", SyncState.Verified,
            Coverage: [Track("Mic", 0.4), Track("Game", 1), Track("Discord", 0)], Duration: TimeSpan.FromSeconds(60))));

        Assert.True(warning);
        Assert.Equal("Clip saved with 3 extra tracks", headline); // silent tracks are still written
        Assert.Contains("Mic is missing about 36 seconds of audio.", detail);
        Assert.Contains("Discord had no audio, so its track is silent.", detail);
    }

    [Theory]
    [InlineData(0.95, true)] // 3 s of a minute: the flat 95% rule used to hide this
    [InlineData(0.985, false)] // 0.9 s: a hiccup, not worth a warning
    public void A_gap_counts_in_seconds_not_as_a_share_of_the_clip(double coverage, bool warns)
    {
        var (_, _, warning) = ClipText.Describe(Outcome(new(AttachStatus.Written, "tech", "out.mp4", SyncState.Verified,
            Coverage: [Track("Mic", coverage)], Duration: TimeSpan.FromSeconds(60))));

        Assert.Equal(warns, warning);
    }

    [Fact]
    public void Unverified_timing_is_explained_without_correlation_jargon()
    {
        var (_, detail, warning) = ClipText.Describe(Outcome(new(AttachStatus.Written,
            "Sync unverified: no clear match (correlation 0.12, margin 1.1×)", "out.mp4", SyncState.Unverified,
            Coverage: [Track("Mic", 1)])));

        Assert.True(warning);
        Assert.DoesNotContain("correlation", detail);
        Assert.Contains("early or late", detail);
    }

    [Theory]
    [InlineData(AttachProblem.SeveralRecordings, "several recordings")]
    [InlineData(AttachProblem.FfmpegMissing, "FFmpeg is missing")]
    [InlineData(AttachProblem.UnsupportedClip, "doesn't understand yet")]
    [InlineData(AttachProblem.ExportFailed, "The log has details")]
    public void A_refusal_says_why_in_words_not_in_the_technical_message(AttachProblem problem, string expected)
    {
        var (_, detail, warning) = ClipText.Describe(Outcome(new(AttachStatus.Refused, "manifest Period start 3.2 s", Problem: problem)));

        Assert.True(warning);
        Assert.Contains(expected, detail);
        Assert.DoesNotContain("manifest", detail);
    }
}
