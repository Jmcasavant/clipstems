using System.Runtime.InteropServices;
using Clipstems.Core.Buffer;

namespace Clipstems.Core.Media;

/// <summary>
/// Opus in Ogg at 128 kbps through ffmpeg's libopus. Measured 2026-10-10 with
/// ffmpeg 9.0.1: decoding returns exactly the encoded frame count and a click
/// lands on its original sample (the encoder's pre-skip is trimmed by the
/// decoder), at 17-20x smaller than WAV on noise.
/// </summary>
public sealed class OpusSegmentCodec(Func<Ffmpeg?> locate) : ISegmentCodec
{
    public const string Bitrate = "128k";
    public const string FileExtension = ".opus";

    /// <summary>
    /// One minute of audio encodes or decodes in well under a second; a run this
    /// long is hung, and is killed so it can't hold up compaction or shutdown.
    /// </summary>
    internal static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(1);

    /// <summary>Finds ffmpeg on each use, so installing it while Clipstems runs takes effect.</summary>
    public OpusSegmentCodec() : this(Ffmpeg.Locate) { }

    public string Extension => FileExtension;

    public void Encode(string wavPath, string compressedPath, CancellationToken cancel = default)
    {
        var ffmpeg = locate() ?? throw new InvalidDataException("ffmpeg was not found.");
        Run(token => ffmpeg.RunFfmpegAsync(
            ["-y", "-i", wavPath, "-c:a", "libopus", "-b:a", Bitrate, "-vbr", "on", "-application", "audio", compressedPath],
            token), cancel);
    }

    public short[] Decode(string compressedPath, CancellationToken cancel = default)
    {
        var ffmpeg = locate() ?? throw new InvalidDataException("ffmpeg was not found, so compressed audio can't be read.");
        byte[] pcm = [];
        Run(async token => pcm = await ffmpeg.DecodePcmAsync(compressedPath, token), cancel);
        return MemoryMarshal.Cast<byte, short>(pcm).ToArray();
    }

    /// <summary>
    /// The store reads synchronously on worker threads. ffmpeg failures and the
    /// time limit surface as unreadable data; the caller's cancellation stays a cancellation.
    /// </summary>
    private static void Run(Func<CancellationToken, Task> work, CancellationToken cancel)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(RunLimit);
        try
        {
            Task.Run(() => work(limit.Token), CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new InvalidDataException($"ffmpeg took longer than {RunLimit.TotalSeconds:0} s and was stopped.");
        }
        catch (Exception ex) when (ex is FfmpegException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }
}
