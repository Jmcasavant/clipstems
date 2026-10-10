using System.Runtime.InteropServices;
using Clipstems.Core.Audio;
using Clipstems.Core.Buffer;

namespace Clipstems.Tests;

public class BufferTests
{
    private static readonly long T0 = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc).Ticks;

    /// <summary>A packet whose every sample holds <paramref name="value"/>.</summary>
    private static byte[] Packet(int frames, short value)
    {
        var samples = Enumerable.Repeat(value, frames * PcmFormat.Channels).ToArray();
        return MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    }

    private static long Ms(double ms) => (long)(ms * TimeSpan.TicksPerMillisecond);

    [Fact]
    public void Buffer_lock_refuses_a_second_owner_until_the_first_lets_go()
    {
        var root = Path.Combine(Path.GetTempPath(), "clipstems-tests", Guid.NewGuid().ToString("N"));
        using (var first = BufferLock.TryAcquire(root))
        {
            Assert.NotNull(first);
            Assert.Null(BufferLock.TryAcquire(root));
        }
        using var again = BufferLock.TryAcquire(root);
        Assert.NotNull(again);
    }

    [Fact]
    public void Contiguous_packets_form_one_segment_named_by_first_frame()
    {
        using var dir = new TempDir();
        var store = new SegmentStore(dir.Path);
        using (var w = new SegmentWriter(store, "mic"))
        {
            w.Write(Packet(480, 100), T0, silent: false);
            w.Write(Packet(480, 100), T0 + Ms(10), silent: false);
        }

        var seg = Assert.Single(store.List("mic"));
        Assert.Equal(T0, seg.StartTicks);
        Assert.Equal(960, seg.Frames);
    }

    [Fact]
    public void Gap_beyond_tolerance_is_filled_with_silence_so_later_audio_keeps_its_time()
    {
        using var dir = new TempDir();
        var store = new SegmentStore(dir.Path);
        using (var w = new SegmentWriter(store, "discord"))
        {
            w.Write(Packet(480, 100), T0, silent: false);
            w.Write(Packet(480, 200), T0 + Ms(210), silent: false); // 200 ms gap after the first 10 ms
        }

        var window = store.ReadWindow("discord", T0, PcmFormat.TicksToFrames(Ms(220)));
        Assert.Equal(100, window.Samples[0]);
        Assert.Equal(0, window.Samples[PcmFormat.TicksToFrames(Ms(100)) * 2]);
        Assert.Equal(200, window.Samples[PcmFormat.TicksToFrames(Ms(215)) * 2]);
    }

    [Fact]
    public void Overlapping_packet_is_trimmed_not_appended()
    {
        using var dir = new TempDir();
        var store = new SegmentStore(dir.Path);
        using (var w = new SegmentWriter(store, "mic"))
        {
            w.Write(Packet(4800, 100), T0, silent: false);              // 0–100 ms
            w.Write(Packet(4800, 200), T0 + Ms(40), silent: false);     // claims 40–140 ms: 60 ms overlap
        }

        var seg = Assert.Single(store.List("mic"));
        Assert.Equal(PcmFormat.TicksToFrames(Ms(140)), seg.Frames);
    }

    [Fact]
    public void Silent_flag_writes_zeros_and_is_not_counted_as_audible()
    {
        using var dir = new TempDir();
        var store = new SegmentStore(dir.Path);
        using var w = new SegmentWriter(store, "app");
        w.Write(Packet(480, 999), T0, silent: true);
        w.Write(Packet(480, 5), T0 + Ms(10), silent: false);
        w.CloseCurrent();

        Assert.Equal(960, w.TotalFrames);
        Assert.Equal(480, w.AudibleFrames);
        Assert.Equal(0, store.ReadWindow("app", T0, 480).Samples.Max());
    }

    [Fact]
    public void Peak_reports_real_level_and_resets()
    {
        using var dir = new TempDir();
        using var w = new SegmentWriter(new SegmentStore(dir.Path), "discord");
        w.Write(Packet(480, 0), T0, silent: false); // unflagged but silent: what process loopback sends
        Assert.Equal(0, w.TakePeak());

        w.Write(Packet(480, -1200), T0 + Ms(10), silent: false);
        w.Write(Packet(480, 300), T0 + Ms(20), silent: false);
        Assert.Equal(1200, w.TakePeak());
        Assert.Equal(0, w.TakePeak());
    }

    [Fact]
    public void Segments_rotate_and_each_new_one_starts_at_its_packet_time()
    {
        using var dir = new TempDir();
        var store = new SegmentStore(dir.Path);
        using (var w = new SegmentWriter(store, "mic", segmentLength: TimeSpan.FromMilliseconds(100)))
        {
            for (int i = 0; i < 3; i++) w.Write(Packet(4800, 1), T0 + Ms(100 * i + 3), silent: false);
        }

        var segs = store.List("mic");
        Assert.Equal(3, segs.Count);
        Assert.Equal([T0 + Ms(3), T0 + Ms(103), T0 + Ms(203)], segs.Select(s => s.StartTicks));
    }

    [Fact]
    public void Window_reports_coverage_across_segments_and_gaps()
    {
        using var dir = new TempDir();
        var store = new SegmentStore(dir.Path);
        using (var w = new SegmentWriter(store, "mic"))
        {
            w.Write(Packet(48_000, 7), T0, silent: false);                        // 0–1 s
            w.CloseCurrent();
            w.Write(Packet(48_000, 9), T0 + TimeSpan.TicksPerSecond * 2, silent: false); // 2–3 s
        }

        var window = store.ReadWindow("mic", T0 + TimeSpan.TicksPerSecond / 2, 48_000 * 2); // 0.5–2.5 s
        Assert.Equal(0.5, window.Coverage, precision: 3);
        Assert.Equal(7, window.Samples[0]);
        Assert.Equal(0, window.Samples[48_000 * 2]);           // 1.5 s: gap
        Assert.Equal(9, window.Samples[(48_000 + 24_000) * 2 + 2]); // 2.0 s onward
    }

    [Fact]
    public void Window_before_any_recording_is_silent_with_zero_coverage()
    {
        using var dir = new TempDir();
        var store = new SegmentStore(dir.Path);
        var window = store.ReadWindow("mic", T0, 4800);
        Assert.Equal(0, window.CoveredFrames);
        Assert.All(window.Samples, s => Assert.Equal(0, s));
    }

    [Fact]
    public void Prune_deletes_only_segments_that_ended_before_the_cutoff()
    {
        using var dir = new TempDir();
        var store = new SegmentStore(dir.Path);
        using (var w = new SegmentWriter(store, "mic"))
        {
            w.Write(Packet(48_000, 1), T0, silent: false);
            w.CloseCurrent();
            w.Write(Packet(48_000, 1), T0 + TimeSpan.TicksPerSecond * 10, silent: false);
        }

        Assert.Equal(1, store.Prune(T0 + TimeSpan.TicksPerSecond * 5));
        Assert.Equal(T0 + TimeSpan.TicksPerSecond * 10, Assert.Single(store.List("mic")).StartTicks);
    }

    [Fact]
    public void Segment_cut_short_by_a_crash_still_reads_by_length()
    {
        using var dir = new TempDir();
        var store = new SegmentStore(dir.Path);
        Directory.CreateDirectory(store.TrackDirectory("mic"));
        var path = Path.Combine(store.TrackDirectory("mic"), SegmentStore.SegmentFileName(T0));
        using (var fs = File.Create(path))
        {
            WavFile.WriteHeader(fs, 0); // header never patched
            fs.Write(Packet(480, 3));
        }

        Assert.Equal(480, Assert.Single(store.List("mic")).Frames);
        Assert.Equal(3, store.ReadWindow("mic", T0, 480).Samples[0]);
    }
}
