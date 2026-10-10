using Clipstems.Core;
using Clipstems.Core.Audio;
using Clipstems.Core.Buffer;
using Clipstems.Core.Sync;

namespace Clipstems.Tests;

public class TrackRefineTests
{
    private static readonly long T0 = new DateTime(2026, 10, 10, 21, 46, 0, DateTimeKind.Utc).Ticks;

    /// <summary>Writes 12 s of noise as one segment and returns it as 48 kHz stereo samples.</summary>
    private static short[] PlantNoise(SegmentStore store, string track, int seed)
    {
        var rng = new Random(seed);
        var samples = new short[48_000 * 12 * 2];
        for (int i = 0; i < samples.Length; i += 2) samples[i] = samples[i + 1] = (short)rng.Next(-6000, 6000);
        Directory.CreateDirectory(store.TrackDirectory(track));
        WavFile.Write(Path.Combine(store.TrackDirectory(track), SegmentStore.SegmentFileName(T0)), samples);
        return samples;
    }

    [Fact]
    public void Track_in_steams_mix_is_moved_onto_steams_clock()
    {
        // Discord sat 21 ms late of the system reference in the live test:
        // Steam's mix holds this track's sound 21 ms after where global sync put it.
        using var dir = new TempDir();
        var store = new SegmentStore(dir.Path);
        var discord = PlantNoise(store, "discord", seed: 1);
        var game = PlantNoise(new SegmentStore(Path.Combine(dir.Path, "other")), "game", seed: 2);
        long globalStart = T0 + TimeSpan.TicksPerSecond;
        int at = (int)PcmFormat.TicksToFrames(TimeSpan.TicksPerSecond + TimeSpan.FromMilliseconds(21).Ticks);
        var mix = new short[48_000 * 8 * 2];
        for (int i = 0; i < mix.Length; i++) mix[i] = (short)(discord[at * 2 + i] / 2 + game[i] / 2);

        var (start, adjustment) = ClipAttacher.RefineTrack(store, "discord", globalStart, Downmix.ToSyncRate(mix));

        Assert.NotNull(adjustment);
        Assert.Equal(21.0, adjustment.Value, precision: 0);
        Assert.Equal(globalStart + TimeSpan.FromMilliseconds(adjustment.Value).Ticks, start, tolerance: 10);
    }

    [Fact]
    public void Track_absent_from_steams_mix_keeps_the_clock_placement()
    {
        // The mic after Steam's own mic recording was turned off.
        using var dir = new TempDir();
        var store = new SegmentStore(dir.Path);
        PlantNoise(store, "mic", seed: 3);
        var steamMix = PlantNoise(new SegmentStore(Path.Combine(dir.Path, "other")), "game", seed: 4);
        long globalStart = T0 + TimeSpan.TicksPerSecond;

        var (start, adjustment) = ClipAttacher.RefineTrack(store, "mic", globalStart, Downmix.ToSyncRate(steamMix.AsSpan(0, 48_000 * 8 * 2)));

        Assert.Null(adjustment);
        Assert.Equal(globalStart, start);
    }

    [Fact]
    public void Track_not_recorded_around_the_clip_is_not_refined()
    {
        using var dir = new TempDir();
        var store = new SegmentStore(dir.Path);
        var probe = new float[8_000 * 8];
        for (int i = 0; i < probe.Length; i++) probe[i] = (float)Math.Sin(i * 0.3);

        var (start, adjustment) = ClipAttacher.RefineTrack(store, "discord", T0, probe);

        Assert.Null(adjustment);
        Assert.Equal(T0, start);
    }
}
