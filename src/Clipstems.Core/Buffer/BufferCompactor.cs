using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using Clipstems.Core.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clipstems.Core.Buffer;

/// <summary>
/// Compresses closed WAV segments (about 12x smaller as Opus at 128 kbps).
/// A segment is only touched once its writer has let go of it, and its WAV is
/// only deleted after the compressed copy decodes back to exactly the same
/// number of frames and has been flushed to disk; any failure leaves the WAV
/// in place, which still reads.
/// </summary>
public sealed partial class BufferCompactor(SegmentStore store, ISegmentCodec codec, ILogger? log = null)
{
    /// <summary>A WAV written to this recently may still be open by a writer that hasn't flushed.</summary>
    internal static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    /// <summary>A segment that failed is tried again after this, not every pass.</summary>
    internal static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    private const string TempMarker = ".tmp";

    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly Dictionary<string, DateTime> _failedUntil = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Segments currently left as WAV because compressing them failed; they use about 12x the disk.</summary>
    public int FailingSegments => Volatile.Read(ref _failing);

    private int _failing;

    /// <summary>Compresses every closed segment in the store. Returns how many were compressed.</summary>
    public int CompactClosed(DateTime nowUtc, CancellationToken cancel = default)
    {
        int done = 0;
        foreach (var track in store.Tracks())
        {
            var dir = new DirectoryInfo(store.TrackDirectory(track));
            if (!dir.Exists) continue;
            RemoveStaleTemporaryFiles(dir, nowUtc);
            foreach (var wav in dir.EnumerateFiles("*.wav").OrderBy(f => f.Name, StringComparer.Ordinal).ToList())
            {
                cancel.ThrowIfCancellationRequested();
                if (Compact(wav, nowUtc, cancel)) done++;
            }
        }
        // Pruned segments stop counting as failures.
        foreach (var gone in _failedUntil.Keys.Where(path => !File.Exists(path)).ToList()) _failedUntil.Remove(gone);
        Volatile.Write(ref _failing, _failedUntil.Count);
        return done;
    }

    private bool Compact(FileInfo wav, DateTime nowUtc, CancellationToken cancel)
    {
        if (!long.TryParse(Path.GetFileNameWithoutExtension(wav.Name), NumberStyles.None, CultureInfo.InvariantCulture, out var start))
            return false;
        if (_failedUntil.TryGetValue(wav.FullName, out var until) && nowUtc < until) return false;
        wav.Refresh();
        if (!wav.Exists || nowUtc - wav.LastWriteTimeUtc < Settle) return false;

        long frames;
        // The writer holds its file open with read sharing only, so an exclusive open
        // succeeds only once it has closed the segment. While held, also repair a
        // header a crash left stale: the store sizes WAVs by length, ffmpeg by header.
        try
        {
            using var exclusive = new FileStream(wav.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            frames = WavFile.FrameCount(exclusive.Length);
            if (frames == 0) return false;
            RepairHeader(exclusive, frames);
        }
        catch (IOException) { return false; } // still being written, or a reader has it: next pass
        catch (UnauthorizedAccessException) { return false; }

        var dir = wav.DirectoryName!;
        var target = Path.Combine(dir, SegmentStore.CompressedFileName(start, frames, codec.Extension));
        // Temporary name keeps the codec's extension (ffmpeg picks the format from it)
        // but doesn't parse as a segment, so a crash mid-encode leaves nothing the store reads.
        var temp = Path.Combine(dir, SegmentStore.CompressedFileName(start, frames, TempMarker + codec.Extension));
        try
        {
            // A copy already in place (an earlier pass stopped before deleting the WAV) is
            // trusted only after the same check a new one gets; a bad one is rebuilt.
            if (File.Exists(target) && !ExistingCopyChecksOut(target, frames, cancel)) File.Delete(target);
            if (!File.Exists(target))
            {
                codec.Encode(wav.FullName, temp, cancel);
                if (!DecodesTo(temp, frames, cancel))
                    throw new InvalidDataException($"the compressed copy doesn't decode to the WAV's {frames} frames");
                // Make the copy durable before the WAV goes, on any filesystem: its bytes are
                // flushed, then published by a write-through rename that returns only once the
                // move is on disk. A power cut can then never keep the WAV's deletion and lose the copy.
                using (var file = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    file.Flush(flushToDisk: true);
                MoveDurably(temp, target);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            _failedUntil[wav.FullName] = nowUtc + RetryAfter;
            _log.LogWarning("Keeping {Segment} uncompressed: {Error}", wav.FullName, ex.Message);
            return false;
        }
        TryDelete(wav.FullName);
        _failedUntil.Remove(wav.FullName);
        return true;
    }

    private bool DecodesTo(string path, long frames, CancellationToken cancel) =>
        codec.Decode(path, cancel).Length == frames * PcmFormat.Channels;

    /// <summary>A copy found in place that doesn't decode at all fails the check like a short one, so it gets rebuilt.</summary>
    private bool ExistingCopyChecksOut(string path, long frames, CancellationToken cancel)
    {
        try
        {
            return DecodesTo(path, frames, cancel);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private const uint MoveFileWriteThrough = 0x8; // MOVEFILE_WRITE_THROUGH

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string existing, string destination, uint flags);

    private static void MoveDurably(string from, string to)
    {
        if (!MoveFileEx(from, to, MoveFileWriteThrough))
            throw new IOException($"Couldn't move {from} to {to}: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
    }

    private static void RepairHeader(FileStream wav, long frames)
    {
        Span<byte> header = stackalloc byte[WavFile.HeaderSize];
        wav.Position = 0;
        wav.ReadExactly(header);
        long dataBytes = frames * PcmFormat.BlockAlign;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[40..]) == dataBytes) return;
        wav.Position = 0;
        WavFile.WriteHeader(wav, dataBytes);
    }

    private static void RemoveStaleTemporaryFiles(DirectoryInfo dir, DateTime nowUtc)
    {
        foreach (var temp in dir.EnumerateFiles("*" + TempMarker + ".*"))
            if (nowUtc - temp.LastWriteTimeUtc > TimeSpan.FromMinutes(1))
                TryDelete(temp.FullName);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
