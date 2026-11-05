using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Clipstems.Core.Buffer;

namespace Clipstems.Core.Capture;

public enum RecorderState { Starting, Recording, Waiting, Retrying, Stopped }

/// <summary>
/// Keeps one track recording into the buffer for as long as it runs: opens
/// the source on its own MTA thread, re-opens after device changes or errors,
/// and re-targets an app or game after it restarts.
/// </summary>
public sealed class TrackRecorder : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProcessPollInterval = TimeSpan.FromSeconds(3);

    private readonly SegmentWriter _writer;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private volatile bool _restart;
    private volatile bool _started;

    /// <summary>Set by whichever of <see cref="Dispose"/> and the capture thread finishes first; the second one frees the writer.</summary>
    private int _exited;

    public TrackRecorder(string track, CaptureSource source, SegmentStore store, ILogger? log = null)
    {
        Track = track;
        Source = source;
        _log = log ?? NullLogger.Instance;
        _writer = new SegmentWriter(store, track);
        _thread = new Thread(Loop) { IsBackground = true, Name = $"clipstems:{track}" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public string Track { get; }
    public CaptureSource Source { get; }
    public RecorderState State { get; private set; } = RecorderState.Starting;

    /// <summary>What is being captured, or why nothing is.</summary>
    public string? Detail { get; private set; }

    public long TotalFrames => _writer.TotalFrames;

    public void Start()
    {
        _started = true;
        _thread.Start();
    }

    /// <summary>Re-opens the source, e.g. after the Windows default device changed.</summary>
    public void Restart() => _restart = true;

    /// <summary>Peak level since the last call, in dBFS; null when nothing but silence arrived.</summary>
    public double? TakePeakDbfs() => _writer.TakePeak() is var p and > 0 ? 20 * Math.Log10(p / 32768.0) : null;

    /// <summary>
    /// Stops the track. Waits a few seconds for the capture thread; a thread
    /// still blocked opening a device frees the writer itself when it gets
    /// out, so nothing is disposed under a thread that is still using it.
    /// </summary>
    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        if (_started) _thread.Join(TimeSpan.FromSeconds(5));
        if (!_started || Interlocked.Exchange(ref _exited, 1) == 1) Release();
    }

    private void Release()
    {
        try { _writer.Dispose(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _log.LogWarning("Track {Track}: closing the last segment failed: {Error}", Track, ex.Message);
        }
        _stop.Dispose();
    }

    private void Loop()
    {
        try
        {
            Capture(_stop.Token);
        }
        finally
        {
            State = RecorderState.Stopped;
            if (Interlocked.Exchange(ref _exited, 1) == 1) Release();
        }
    }

    private void Capture(CancellationToken cancel)
    {
        string? lastWait = null;
        while (!cancel.IsCancellationRequested)
        {
            _restart = false;
            try
            {
                using var session = CaptureSession.Open(Source);
                if (session is null)
                {
                    var why = Source.Kind == SourceKind.SteamGame ? "no Steam game running" : $"waiting for {Source}";
                    if (why != lastWait) _log.LogInformation("Track {Track}: {Why}", Track, why);
                    (State, Detail, lastWait) = (RecorderState.Waiting, why, why);
                    cancel.WaitHandle.WaitOne(ProcessPollInterval);
                    continue;
                }
                lastWait = null;
                (State, Detail) = (RecorderState.Recording, session.Description);
                _log.LogInformation("Track {Track}: recording {Source}", Track, session.Description);
                long nextAliveCheck = Environment.TickCount64 + (long)ProcessPollInterval.TotalMilliseconds;
                session.Run(_writer, () =>
                {
                    if (_restart) return false;
                    if (session.ProcessId is not { } pid || Environment.TickCount64 < nextAliveCheck) return true;
                    nextAliveCheck = Environment.TickCount64 + (long)ProcessPollInterval.TotalMilliseconds;
                    return ProcessTree.IsAlive(pid);
                }, cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Device errors, but also a full disk or a locked buffer file: an exception
                // escaping this thread would end the whole process, so every failure retries.
                if (ex is COMException or InvalidOperationException or TimeoutException)
                    _log.LogWarning("Track {Track}: {Error}; retrying", Track, ex.Message);
                else
                    _log.LogError(ex, "Track {Track}: recording failed; retrying", Track);
                (State, Detail) = (RecorderState.Retrying, ex.Message);
                try { _writer.CloseCurrent(); }
                catch (Exception closeEx) when (closeEx is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
                cancel.WaitHandle.WaitOne(RetryDelay);
            }
        }
    }
}
