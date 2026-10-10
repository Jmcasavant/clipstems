using System.Runtime.InteropServices;
using Sidetrack.Core.Buffer;

namespace Sidetrack.Core.Capture;

public enum RecorderState { Starting, Recording, WaitingForApp, Retrying, Stopped }

/// <summary>
/// Keeps one track recording into the buffer for as long as it runs: opens
/// the source on its own MTA thread, re-opens after device changes or errors,
/// and re-targets an app after it restarts.
/// </summary>
public sealed class TrackRecorder : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AppPollInterval = TimeSpan.FromSeconds(3);

    private readonly CaptureSource _source;
    private readonly SegmentWriter _writer;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;

    public TrackRecorder(string track, CaptureSource source, SegmentStore store)
    {
        Track = track;
        _source = source;
        _writer = new SegmentWriter(store, track);
        _thread = new Thread(Loop) { IsBackground = true, Name = $"sidetrack:{track}" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public string Track { get; }
    public RecorderState State { get; private set; } = RecorderState.Starting;
    public string? Detail { get; private set; }
    public long TotalFrames => _writer.TotalFrames;
    public long AudibleFrames => _writer.AudibleFrames;

    /// <summary>Peak level since the last call, in dBFS; null when nothing but silence arrived.</summary>
    public double? TakePeakDbfs() => _writer.TakePeak() is var p and > 0 ? 20 * Math.Log10(p / 32768.0) : null;

    public void Start() => _thread.Start();

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(TimeSpan.FromSeconds(3));
        _writer.Dispose();
        _stop.Dispose();
    }

    private void Loop()
    {
        var cancel = _stop.Token;
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                using var session = CaptureSession.Open(_source);
                if (session is null)
                {
                    (State, Detail) = (RecorderState.WaitingForApp, $"{_source.Target} is not running");
                    cancel.WaitHandle.WaitOne(AppPollInterval);
                    continue;
                }
                (State, Detail) = (RecorderState.Recording, session.Description);
                long nextAliveCheck = Environment.TickCount64 + (long)AppPollInterval.TotalMilliseconds;
                session.Run(_writer, () =>
                {
                    if (session.ProcessId is not { } pid || Environment.TickCount64 < nextAliveCheck) return true;
                    nextAliveCheck = Environment.TickCount64 + (long)AppPollInterval.TotalMilliseconds;
                    return ProcessTree.IsAlive(pid);
                }, cancel);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or TimeoutException)
            {
                (State, Detail) = (RecorderState.Retrying, ex.Message);
                cancel.WaitHandle.WaitOne(RetryDelay);
            }
        }
        State = RecorderState.Stopped;
    }
}
