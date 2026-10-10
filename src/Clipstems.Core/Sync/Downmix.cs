using Clipstems.Core.Audio;

namespace Clipstems.Core.Sync;

public static class Downmix
{
    /// <summary>Rate the sync stage works at: 0.125 ms resolution, cheap FFTs.</summary>
    public const int SyncRate = 8_000;

    public const int Factor = PcmFormat.SampleRate / SyncRate;

    /// <summary>
    /// Interleaved 48 kHz stereo → 8 kHz mono. A box average over each group
    /// of six frames is a crude low-pass, adequate for locating a correlation
    /// peak.
    /// </summary>
    public static float[] ToSyncRate(ReadOnlySpan<short> interleaved)
    {
        int frames = interleaved.Length / PcmFormat.Channels;
        var output = new float[frames / Factor];
        for (int o = 0; o < output.Length; o++)
        {
            int baseSample = o * Factor * PcmFormat.Channels;
            int sum = 0;
            for (int k = 0; k < Factor * PcmFormat.Channels; k++) sum += interleaved[baseSample + k];
            output[o] = sum / (32768f * Factor * PcmFormat.Channels);
        }
        return output;
    }

    /// <summary>Signed 16-bit mono little-endian bytes (ffmpeg <c>-f s16le</c>) → floats.</summary>
    public static float[] FromS16Mono(ReadOnlySpan<byte> bytes)
    {
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes[..(bytes.Length & ~1)]);
        var output = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++) output[i] = samples[i] / 32768f;
        return output;
    }
}
