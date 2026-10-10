using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Clipstems.Core.Audio;
using Clipstems.Core.Buffer;
using Clipstems.Core.Media;

namespace Clipstems.Tests;

public class CompressedBufferTests
{
    private static readonly long T0 = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc).Ticks;

    /// <summary>
    /// Stores raw PCM, and like ffmpeg reads a WAV by the size its header declares,
    /// so a stale header from a crash shows up as a short encode.
    /// </summary>
    private sealed class RawCodec : ISegmentCodec
    {
        public int Encodes;
        public int DropFramesOnDecode;

        public string Extension => ".raw";

        public void Encode(string wavPath, string compressedPath, CancellationToken cancel = default)
        {
            Encodes++;
            var bytes = File.ReadAllBytes(wavPath);
            int declared = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40));
            File.WriteAllBytes(compressedPath, bytes.AsSpan(WavFile.HeaderSize, Math.Min(declared, bytes.Length - WavFile.HeaderSize)).ToArray());
        }

        public short[] Decode(string compressedPath, CancellationToken cancel = default)
        {
            var bytes = File.ReadAllBytes(compressedPath);
            if (bytes.Length % PcmFormat.BlockAlign != 0) throw new InvalidDataException("corrupt"); // like ffmpeg on a damaged file
            var samples = MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(compressedPath)).ToArray();
            return samples[..^(DropFramesOnDecode * PcmFormat.Channels)];
        }
    }

    /// <summary>A closed one-second segment whose frame i holds value i % 1000, old enough to be settled.</summary>
    private static string ClosedSegment(SegmentStore store, string track, long start, int frames = 48_000)
    {
        Directory.CreateDirectory(store.TrackDirectory(track));
        var samples = new short[frames * PcmFormat.Channels];
        for (int i = 0; i < frames; i++) samples[2 * i] = samples[2 * i + 1] = (short)(i % 1000);
        var path = Path.Combine(store.TrackDirectory(track), SegmentStore.SegmentFileName(start));
        WavFile.Write(path, samples);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromMinutes(1));
        return path;
    }

    [Fact]
    public void A_closed_segment_is_compressed_and_reads_back_at_its_own_time()
    {
        using var dir = new TempDir();
        var codec = new RawCodec();
        var store = new SegmentStore(dir.Path, codec);
        var wav = ClosedSegment(store, "mic", T0);

        Assert.Equal(1, new BufferCompactor(store, codec).CompactClosed(DateTime.UtcNow));

        Assert.False(File.Exists(wav));
        var seg = Assert.Single(store.List("mic"));
        Assert.True(seg.Compressed);
        Assert.Equal(48_000, seg.Frames);
        var window = store.ReadWindow("mic", T0 + PcmFormat.FramesToTicks(500), 10);
        Assert.Equal(1.0, window.Coverage);
        Assert.Equal(500, window.Samples[0]); // frame 500 of the segment, not shifted
    }

    [Fact]
    public void A_segment_still_being_written_is_left_alone()
    {
        using var dir = new TempDir();
        var codec = new RawCodec();
        var store = new SegmentStore(dir.Path, codec);
        using var writer = new SegmentWriter(store, "mic");
        writer.Write(new byte[480 * PcmFormat.BlockAlign], T0, silent: false);
        foreach (var f in Directory.GetFiles(store.TrackDirectory("mic"))) File.SetLastWriteTimeUtc(f, DateTime.UtcNow - TimeSpan.FromMinutes(1));

        Assert.Equal(0, new BufferCompactor(store, codec).CompactClosed(DateTime.UtcNow));
        Assert.Equal(0, codec.Encodes);
    }

    [Fact]
    public void A_recently_written_segment_waits_to_settle()
    {
        using var dir = new TempDir();
        var codec = new RawCodec();
        var store = new SegmentStore(dir.Path, codec);
        var wav = ClosedSegment(store, "mic", T0);
        File.SetLastWriteTimeUtc(wav, DateTime.UtcNow);

        Assert.Equal(0, new BufferCompactor(store, codec).CompactClosed(DateTime.UtcNow));
        Assert.True(File.Exists(wav));
    }

    [Fact]
    public void A_copy_that_decodes_to_a_different_length_is_discarded_and_the_wav_kept()
    {
        using var dir = new TempDir();
        var codec = new RawCodec { DropFramesOnDecode = 1 };
        var store = new SegmentStore(dir.Path, codec);
        var wav = ClosedSegment(store, "mic", T0);
        var compactor = new BufferCompactor(store, codec);
        var now = DateTime.UtcNow;

        Assert.Equal(0, compactor.CompactClosed(now));
        Assert.True(File.Exists(wav));
        Assert.Equal([wav], Directory.GetFiles(store.TrackDirectory("mic")));
        Assert.False(Assert.Single(store.List("mic")).Compressed);

        // Not retried every pass, but retried later.
        compactor.CompactClosed(now + TimeSpan.FromMinutes(1));
        Assert.Equal(1, codec.Encodes);
        codec.DropFramesOnDecode = 0;
        Assert.Equal(1, compactor.CompactClosed(now + BufferCompactor.RetryAfter + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_crash_stale_header_is_repaired_so_the_whole_segment_is_encoded()
    {
        using var dir = new TempDir();
        var codec = new RawCodec();
        var store = new SegmentStore(dir.Path, codec);
        var wav = ClosedSegment(store, "mic", T0);
        using (var fs = new FileStream(wav, FileMode.Open, FileAccess.Write)) WavFile.WriteHeader(fs, 0); // as a crash leaves it
        File.SetLastWriteTimeUtc(wav, DateTime.UtcNow - TimeSpan.FromMinutes(1));

        Assert.Equal(1, new BufferCompactor(store, codec).CompactClosed(DateTime.UtcNow));
        Assert.Equal(48_000, Assert.Single(store.List("mic")).Frames);
    }

    [Fact]
    public void After_an_interrupted_swap_the_wav_wins_and_the_next_pass_removes_it()
    {
        using var dir = new TempDir();
        var codec = new RawCodec();
        var store = new SegmentStore(dir.Path, codec);
        var wav = ClosedSegment(store, "mic", T0);
        // A verified copy was renamed into place, then the process died before deleting the WAV.
        codec.Encode(wav, Path.Combine(store.TrackDirectory("mic"), SegmentStore.CompressedFileName(T0, 48_000, codec.Extension)));

        Assert.False(Assert.Single(store.List("mic")).Compressed);
        new BufferCompactor(store, codec).CompactClosed(DateTime.UtcNow);
        Assert.True(Assert.Single(store.List("mic")).Compressed);
    }

    [Fact]
    public void A_bad_copy_already_in_place_is_rebuilt_before_the_wav_is_deleted()
    {
        using var dir = new TempDir();
        var codec = new RawCodec();
        var store = new SegmentStore(dir.Path, codec);
        var wav = ClosedSegment(store, "mic", T0);
        var target = Path.Combine(store.TrackDirectory("mic"), SegmentStore.CompressedFileName(T0, 48_000, codec.Extension));
        File.WriteAllBytes(target, new byte[1000]); // truncated: decodes to 250 frames, not 48000

        Assert.Equal(1, new BufferCompactor(store, codec).CompactClosed(DateTime.UtcNow));

        Assert.False(File.Exists(wav));
        Assert.Equal(48_000 * PcmFormat.BlockAlign, new FileInfo(target).Length);
    }

    [Fact]
    public void A_copy_in_place_that_wont_decode_at_all_is_rebuilt_too()
    {
        using var dir = new TempDir();
        var codec = new RawCodec();
        var store = new SegmentStore(dir.Path, codec);
        var wav = ClosedSegment(store, "mic", T0);
        var target = Path.Combine(store.TrackDirectory("mic"), SegmentStore.CompressedFileName(T0, 48_000, codec.Extension));
        File.WriteAllBytes(target, new byte[1001]); // decoding throws

        Assert.Equal(1, new BufferCompactor(store, codec).CompactClosed(DateTime.UtcNow));

        Assert.False(File.Exists(wav));
        Assert.Equal(48_000 * PcmFormat.BlockAlign, new FileInfo(target).Length);
    }

    [Fact]
    public void A_bad_copy_in_place_that_cant_be_rebuilt_keeps_the_wav()
    {
        using var dir = new TempDir();
        var codec = new RawCodec { DropFramesOnDecode = 1 };
        var store = new SegmentStore(dir.Path, codec);
        var wav = ClosedSegment(store, "mic", T0);
        File.WriteAllBytes(Path.Combine(store.TrackDirectory("mic"), SegmentStore.CompressedFileName(T0, 48_000, codec.Extension)), new byte[1000]);

        var compactor = new BufferCompactor(store, codec);
        Assert.Equal(0, compactor.CompactClosed(DateTime.UtcNow));
        Assert.True(File.Exists(wav));
        Assert.Equal(1, compactor.FailingSegments);
    }

    [Fact]
    public void A_read_that_finds_its_wav_swapped_for_the_compressed_copy_reads_the_copy()
    {
        using var dir = new TempDir();
        var codec = new RawCodec();
        var store = new SegmentStore(dir.Path, codec);
        var wav = ClosedSegment(store, "mic", T0);
        var listed = Assert.Single(store.List("mic")); // the reader lists the WAV...
        new BufferCompactor(store, codec).CompactClosed(DateTime.UtcNow); // ...then the compactor swaps it

        var dest = new short[10 * PcmFormat.Channels];
        Assert.Equal(10, store.ReadSegment("mic", listed, 500, dest, CancellationToken.None));
        Assert.Equal(500, dest[0]);
    }

    [Fact]
    public void A_read_waits_out_the_compactor_briefly_holding_the_wav()
    {
        using var dir = new TempDir();
        var store = new SegmentStore(dir.Path, new RawCodec());
        var wav = ClosedSegment(store, "mic", T0);
        var held = new FileStream(wav, FileMode.Open, FileAccess.ReadWrite, FileShare.None); // as during header repair
        using var release = new Timer(_ => held.Dispose(), null, 150, Timeout.Infinite);

        var window = store.ReadWindow("mic", T0, 100);
        Assert.Equal(1.0, window.Coverage);
    }

    [Fact]
    public void Files_that_only_look_like_segments_are_neither_read_nor_pruned()
    {
        using var dir = new TempDir();
        var store = new SegmentStore(dir.Path, new RawCodec());
        Directory.CreateDirectory(store.TrackDirectory("mic"));
        var backup = Path.Combine(store.TrackDirectory("mic"), SegmentStore.CompressedFileName(T0, 48_000, ".bak"));
        File.WriteAllBytes(backup, new byte[16]);

        Assert.Empty(store.List("mic"));
        store.Prune(T0 + TimeSpan.FromDays(1).Ticks);
        Assert.True(File.Exists(backup));
    }

    [Fact]
    public void Without_a_decoder_a_compressed_segment_counts_as_unrecorded_not_as_silence()
    {
        using var dir = new TempDir();
        var codec = new RawCodec();
        var store = new SegmentStore(dir.Path, codec);
        ClosedSegment(store, "mic", T0);
        new BufferCompactor(store, codec).CompactClosed(DateTime.UtcNow);

        var window = new SegmentStore(dir.Path).ReadWindow("mic", T0, 100);
        Assert.Equal(0, window.Coverage);
    }

    [Fact]
    public void A_leftover_temporary_file_is_not_a_segment()
    {
        using var dir = new TempDir();
        var store = new SegmentStore(dir.Path, new RawCodec());
        Directory.CreateDirectory(store.TrackDirectory("mic"));
        File.WriteAllBytes(Path.Combine(store.TrackDirectory("mic"), SegmentStore.CompressedFileName(T0, 48_000, ".tmp.raw")), new byte[16]);

        Assert.Empty(store.List("mic"));
    }

    [Fact]
    public void Opus_through_ffmpeg_keeps_every_frame_and_the_exact_timing()
    {
        if (Ffmpeg.Locate() is null) return; // needs ffmpeg; the measured result is recorded on OpusSegmentCodec
        using var dir = new TempDir();
        var codec = new OpusSegmentCodec();
        var store = new SegmentStore(dir.Path, codec);
        Directory.CreateDirectory(store.TrackDirectory("mic"));
        const int frames = 48_000 * 3 - 17, burstAt = 100_003;
        var samples = new short[frames * PcmFormat.Channels];
        var noise = new Random(7);
        for (int i = 0; i < frames; i++)
        {
            short v = (short)(burstAt <= i && i < burstAt + 48 ? (i - burstAt < 24 ? 20000 : -20000) : noise.Next(-300, 300));
            samples[2 * i] = samples[2 * i + 1] = v;
        }
        var wav = Path.Combine(store.TrackDirectory("mic"), SegmentStore.SegmentFileName(T0));
        WavFile.Write(wav, samples);
        File.SetLastWriteTimeUtc(wav, DateTime.UtcNow - TimeSpan.FromMinutes(1));

        Assert.Equal(1, new BufferCompactor(store, codec).CompactClosed(DateTime.UtcNow));

        var seg = Assert.Single(store.List("mic"));
        Assert.True(seg.Compressed);
        var window = store.ReadWindow("mic", T0, frames);
        Assert.Equal(1.0, window.Coverage);
        int found = Enumerable.Range(burstAt - 2000, 4000).First(i => window.Samples[2 * i] > 10000);
        Assert.Equal(burstAt, found);
        Assert.True(new FileInfo(seg.Path).Length * 8 < (long)frames * PcmFormat.BlockAlign, "compressed copy should be at least 8x smaller than the WAV");
    }
}
