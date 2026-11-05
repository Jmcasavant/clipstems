using Clipstems.Core.Audio;

namespace Clipstems.Core.Buffer;

/// <summary>
/// Turns timestamped capture packets into gap-free WAV segments for one track.
/// Each segment starts at a real packet timestamp, so device-clock drift is
/// bounded by the segment length instead of accumulating for the whole session.
/// </summary>
public sealed class SegmentWriter : IDisposable
{
    private readonly SegmentStore _store;
    private readonly string _track;
    private readonly long _segmentFrames;
    private readonly long _gapToleranceTicks;
    private readonly long _flushIntervalMs;
    private WavWriter? _current;
    private long _currentStartTicks;
    private long _lastFlushTickCount;

    /// <summary>A step back larger than this is the system clock being set, not jitter: start a new segment.</summary>
    private static readonly long ClockStepTicks = TimeSpan.FromSeconds(1).Ticks;

    public SegmentWriter(SegmentStore store, string track, TimeSpan? segmentLength = null,
        TimeSpan? gapTolerance = null, TimeSpan? flushInterval = null)
    {
        _store = store;
        _track = track;
        _segmentFrames = PcmFormat.TicksToFrames((segmentLength ?? TimeSpan.FromMinutes(1)).Ticks);
        _gapToleranceTicks = (gapTolerance ?? TimeSpan.FromMilliseconds(50)).Ticks;
        _flushIntervalMs = (long)(flushInterval ?? TimeSpan.FromSeconds(1)).TotalMilliseconds;
        Directory.CreateDirectory(store.TrackDirectory(track));
    }

    /// <summary>Frames written since this writer was created, including filled silence.</summary>
    public long TotalFrames { get; private set; }

    /// <summary>
    /// Frames Windows did not flag as silent. Process loopback rarely sets the
    /// flag, so this counts packets, not sound; use <see cref="TakePeak"/> for level.
    /// </summary>
    public long AudibleFrames { get; private set; }

    private int _peak;

    /// <summary>Largest absolute sample since the last call (0..32768), then resets.</summary>
    public int TakePeak() => Interlocked.Exchange(ref _peak, 0);

    /// <summary>
    /// Appends one packet. <paramref name="utcTicks"/> is the UTC time of the
    /// packet's first frame; when <paramref name="timestampValid"/> is false the
    /// packet is appended directly after the previous one.
    /// </summary>
    public void Write(ReadOnlySpan<byte> pcm, long utcTicks, bool silent, bool timestampValid = true)
    {
        int frames = pcm.Length / PcmFormat.BlockAlign;
        if (frames == 0) return;

        if (_current is not null && timestampValid)
        {
            long expected = _currentStartTicks + PcmFormat.FramesToTicks(_current.Frames);
            long delta = utcTicks - expected;
            if (delta > _gapToleranceTicks)
            {
                long gapFrames = PcmFormat.TicksToFrames(delta);
                if (_current.Frames + gapFrames > _segmentFrames)
                    CloseCurrent(); // long gap: the store leaves it silent, no need to write zeros
                else
                {
                    _current.WriteSilence(gapFrames);
                    TotalFrames += gapFrames;
                }
            }
            else if (delta < -ClockStepTicks)
            {
                // The wall clock was set back (NTP, the user). Dropping audio until real time
                // caught up would lose it; a new segment at the packet's own time keeps it.
                CloseCurrent();
            }
            else if (delta < -_gapToleranceTicks)
            {
                int drop = (int)Math.Min(frames, PcmFormat.TicksToFrames(-delta));
                pcm = pcm[(drop * PcmFormat.BlockAlign)..];
                utcTicks += PcmFormat.FramesToTicks(drop);
                frames -= drop;
                if (frames == 0) return;
            }
        }

        if (_current is null)
        {
            if (!timestampValid) utcTicks = DateTime.UtcNow.Ticks;
            Open(utcTicks);
        }

        if (silent) _current!.WriteSilence(frames);
        else
        {
            _current!.Write(pcm);
            AudibleFrames += frames;
            int peak = 0;
            foreach (var s in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm))
                peak = Math.Max(peak, Math.Abs((int)s));
            if (peak > Volatile.Read(ref _peak)) Volatile.Write(ref _peak, peak);
        }
        TotalFrames += frames;

        if (_current.Frames >= _segmentFrames) CloseCurrent();
        else if (Environment.TickCount64 - _lastFlushTickCount >= _flushIntervalMs)
        {
            // Timed by the monotonic tick count: packet times can be flagged bad or jump.
            _current.Flush();
            _lastFlushTickCount = Environment.TickCount64;
        }
    }

    /// <summary>Ends the current segment; the next packet starts a new one at its own timestamp.</summary>
    public void CloseCurrent()
    {
        // Cleared first: if closing throws (disk full), the next packet still starts a fresh segment
        // instead of writing into a half-disposed one forever.
        var closing = _current;
        _current = null;
        closing?.Dispose();
    }

    public void Dispose() => CloseCurrent();

    private void Open(long startTicks)
    {
        var path = System.IO.Path.Combine(_store.TrackDirectory(_track), SegmentStore.SegmentFileName(startTicks));
        while (File.Exists(path))
        {
            startTicks++;
            path = System.IO.Path.Combine(_store.TrackDirectory(_track), SegmentStore.SegmentFileName(startTicks));
        }
        _current = new WavWriter(path);
        _currentStartTicks = startTicks;
        _lastFlushTickCount = Environment.TickCount64;
    }
}
