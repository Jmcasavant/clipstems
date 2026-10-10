using System.Buffers.Binary;

namespace Clipstems.Core.Audio;

/// <summary>
/// Canonical 44-byte-header PCM WAV in <see cref="PcmFormat"/>. Readers size a
/// file from its length, not its header, so a segment cut short by a crash
/// still reads correctly.
/// </summary>
public static class WavFile
{
    public const int HeaderSize = 44;

    public static void WriteHeader(Stream stream, long dataBytes)
    {
        Span<byte> h = stackalloc byte[HeaderSize];
        uint data = (uint)Math.Min(dataBytes, uint.MaxValue - 36);
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], 36 + data);
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 1); // WAVE_FORMAT_PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], PcmFormat.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], PcmFormat.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], PcmFormat.SampleRate * PcmFormat.BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], PcmFormat.BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], PcmFormat.BytesPerSample * 8);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], data);
        stream.Write(h);
    }

    public static long FrameCount(long fileLength) => Math.Max(0, fileLength - HeaderSize) / PcmFormat.BlockAlign;

    /// <summary>Writes interleaved samples as a complete WAV file.</summary>
    public static void Write(string path, ReadOnlySpan<short> interleaved)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        WriteHeader(fs, (long)interleaved.Length * PcmFormat.BytesPerSample);
        fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(interleaved));
    }

    /// <summary>
    /// Reads up to <paramref name="dest"/>.Length / Channels frames starting at
    /// <paramref name="startFrame"/>. Opens with sharing so a segment the
    /// recorder still holds can be read. Returns frames read.
    /// </summary>
    public static int ReadFrames(string path, long startFrame, Span<short> dest)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long available = FrameCount(fs.Length) - startFrame;
        if (available <= 0) return 0;
        int frames = (int)Math.Min(available, dest.Length / PcmFormat.Channels);
        fs.Position = HeaderSize + startFrame * PcmFormat.BlockAlign;
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(dest[..(frames * PcmFormat.Channels)]);
        fs.ReadExactly(bytes);
        return frames;
    }
}

/// <summary>Streams PCM into a WAV file and patches the header sizes on dispose.</summary>
public sealed class WavWriter : IDisposable
{
    private static readonly byte[] Zeros = new byte[PcmFormat.BlockAlign * 4800];
    private readonly FileStream _stream;
    private long _dataBytes;

    public WavWriter(string path)
    {
        Path = path;
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16);
        WavFile.WriteHeader(_stream, 0);
    }

    public string Path { get; }
    public long Frames => _dataBytes / PcmFormat.BlockAlign;

    public void Write(ReadOnlySpan<byte> pcm)
    {
        _stream.Write(pcm);
        _dataBytes += pcm.Length;
    }

    public void WriteSilence(long frames)
    {
        long bytes = frames * PcmFormat.BlockAlign;
        while (bytes > 0)
        {
            int n = (int)Math.Min(bytes, Zeros.Length);
            _stream.Write(Zeros, 0, n);
            bytes -= n;
            _dataBytes += n;
        }
    }

    /// <summary>Pushes buffered audio to disk so another process can read it.</summary>
    public void Flush() => _stream.Flush();

    public void Dispose()
    {
        _stream.Position = 0;
        WavFile.WriteHeader(_stream, _dataBytes);
        _stream.Dispose();
    }
}
