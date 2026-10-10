using Clipstems.Core.Sync;

namespace Clipstems.Tests;

public class SyncTests
{
    private static float[] Noise(int length, int seed, float amplitude = 0.3f)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, length).Select(_ => (float)(rng.NextDouble() * 2 - 1) * amplitude).ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12_345)]
    [InlineData(47_999)]
    public void Finds_known_offset_of_probe_inside_reference(int offset)
    {
        var reference = Noise(8_000 * 26, seed: 1);
        var hiss = Noise(8_000 * 20, seed: 2, amplitude: 0.05f);
        var probe = reference.Skip(offset).Take(8_000 * 20).Select((v, i) => v + hiss[i]).ToArray();

        var result = Correlator.Locate(reference, probe, exclusionSamples: 80);

        Assert.NotNull(result);
        Assert.Equal(offset, result.LagSamples, precision: 0);
        Assert.True(Correlator.IsConfident(result), $"peak {result.Peak}, margin {result.Margin}");
    }

    [Fact]
    public void Probe_mixed_with_other_audio_still_locates()
    {
        // Steam's track is game audio; the reference is the whole system mix
        // (game plus voice chat), so the probe is only part of the reference.
        var game = Noise(8_000 * 26, seed: 3);
        var chat = Noise(8_000 * 26, seed: 4, amplitude: 0.3f);
        var systemMix = game.Select((g, i) => g + chat[i]).ToArray();
        var probe = game.Skip(9_000).Take(8_000 * 20).ToArray();

        var result = Correlator.Locate(systemMix, probe, exclusionSamples: 80);

        Assert.NotNull(result);
        Assert.Equal(9_000, result.LagSamples, precision: 0);
        Assert.True(Correlator.IsConfident(result), $"peak {result.Peak}, margin {result.Margin}");
    }

    [Fact]
    public void Silent_probe_has_no_result_rather_than_offset_zero()
    {
        var result = Correlator.Locate(Noise(80_000, seed: 5), new float[40_000], exclusionSamples: 80);
        Assert.Null(result);
    }

    [Fact]
    public void Unrelated_audio_is_not_confident()
    {
        var result = Correlator.Locate(Noise(8_000 * 26, seed: 6), Noise(8_000 * 20, seed: 7), exclusionSamples: 80);
        Assert.True(result is null || !Correlator.IsConfident(result));
    }

    [Fact]
    public void Downmix_averages_six_stereo_frames_per_output_sample()
    {
        var interleaved = Enumerable.Repeat((short)16384, 6 * 2 * 3).ToArray();
        var output = Downmix.ToSyncRate(interleaved);
        Assert.Equal(3, output.Length);
        Assert.All(output, v => Assert.Equal(0.5f, v, precision: 4));
    }
}
