namespace Clipstems.Core.Audio;

/// <summary>
/// The one PCM format Clipstems captures, stores and muxes: 48 kHz, stereo,
/// signed 16-bit little-endian. WASAPI converts every source to it
/// (AUTOCONVERTPCM), so tracks never need resampling against each other.
/// </summary>
public static class PcmFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int BytesPerSample = 2;
    public const int BlockAlign = Channels * BytesPerSample;

    /// <summary>Duration of <paramref name="frames"/> in 100 ns ticks.</summary>
    public static long FramesToTicks(long frames) => (long)((Int128)frames * TimeSpan.TicksPerSecond / SampleRate);

    /// <summary>Nearest frame count for a duration (or difference) in 100 ns ticks.</summary>
    public static long TicksToFrames(long ticks) =>
        (long)Math.Round((double)ticks * SampleRate / TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero);
}
