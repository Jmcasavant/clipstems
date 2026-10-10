namespace Clipstems.Core.Buffer;

/// <summary>
/// Compresses closed buffer segments and reads them back. Decoding must
/// return exactly the frames that were encoded, starting at the same sample:
/// segment timing comes from the file name, so any shift moves the track.
/// Failures surface as <see cref="InvalidDataException"/> or <see cref="IOException"/>.
/// </summary>
public interface ISegmentCodec
{
    /// <summary>The compressed file's extension, with the dot.</summary>
    string Extension { get; }

    void Encode(string wavPath, string compressedPath, CancellationToken cancel = default);

    /// <summary>Interleaved 16-bit stereo at 48 kHz, as the buffer records it.</summary>
    short[] Decode(string compressedPath, CancellationToken cancel = default);
}
