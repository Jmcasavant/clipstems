using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Clipstems.Core.Buffer;
using Clipstems.Core.Capture;
using Clipstems.Core.Media;
using Clipstems.Core.Settings;
using Clipstems.Core.Steam;

namespace Clipstems.Core.Engine;

/// <summary><see cref="EngineStatus.Blocked"/>: another Clipstems process owns the buffer folder.</summary>
public enum EngineStatus { Starting, WaitingForGame, Recording, Paused, Blocked, Stopped }

public enum IssueSeverity { Tip, Warning, Problem }

/// <summary>
/// Something the user should know: a Tip improves results, a Warning degrades
/// them, a Problem stops clips from being made.
/// </summary>
public sealed record EngineIssue(IssueSeverity Severity, string Message);

/// <param name="Failing">
/// The track should be recording and can't: its capture keeps failing, or a
/// Steam game has been running a while and its sound still isn't found. One
/// definition for the tray and the settings window.
/// </param>
public sealed record TrackStatus(string Id, string Title, RecorderState State, string? Detail, double? PeakDbfs, bool Failing = false);

public sealed record ClipOutcome(string ClipDirectory, string? Title, AttachResult Result, DateTime FinishedUtc)
{
    public string ClipName => Path.GetFileName(ClipDirectory);
}

public sealed record EngineOptions
{
    public string BufferRoot { get; init; } = ClipstemsPaths.Buffer;
    public string ProcessedLog { get; init; } = ClipstemsPaths.ProcessedLog;

    /// <summary>Attach new Steam clips automatically. Off for record-only runs.</summary>
    public bool AutoAttach { get; init; } = true;

    /// <summary>Compress closed buffer segments to Opus (needs ffmpeg). Off keeps WAV.</summary>
    public bool CompressBuffer { get; init; } = true;

    /// <summary>How long recording continues after Steam stops reporting a game.</summary>
    public TimeSpan GraceAfterGame { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Runs Clipstems: decides when to record (Steam game running, paused,
/// policy), keeps one recorder per enabled track plus the hidden sync
/// reference, follows default-device changes, prunes the buffer, and turns
/// each new Steam clip into a multi-track MP4 on a serial queue. The tray app
/// and the CLI both drive this one class.
/// </summary>
public sealed class ClipstemsEngine : IAsyncDisposable
{
    /// <summary>Buffer id of the sync reference: every app except Clipstems, on any device.</summary>
    public const string ReferenceTrackId = "_reference";

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ClipPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SteamRefreshInterval = TimeSpan.FromSeconds(30);
    private const int RecentClipLimit = 10;

    private readonly EngineOptions _options;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly SegmentStore _store;
    private readonly BufferCompactor _compactor;
    private Task? _compacting;

    /// <summary>How often closed segments are looked for; each closes once a minute per track.</summary>
    private static readonly TimeSpan CompactInterval = TimeSpan.FromSeconds(15);
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<string> _attachQueue = Channel.CreateUnbounded<string>();
    private readonly Dictionary<string, (TrackRecorder Recorder, string Title)> _recorders = new();
    private readonly Dictionary<string, double?> _peaks = new();
    private readonly List<ClipOutcome> _recent = [];
    private readonly object _gate = new();

    private ClipstemsSettings _settings;
    private bool _settingsChanged;
    private volatile bool _paused;
    private Task? _main;
    private Task? _attacher;
    private ClipWatcher? _watcher;
    private string? _steamPath;
    private SteamUser? _steamUser;
    private SteamRecordingSettings _steamSettings = SteamRecordingSettings.Empty;
    private uint? _gameAppId;
    private DateTime _lastGameSeenUtc = DateTime.MinValue;
    private DateTime _gameStartedUtc = DateTime.MinValue;

    /// <summary>Steam reports a game before its process is up; only after this is a missing game sound a failure.</summary>
    private static readonly TimeSpan GameAudioGrace = TimeSpan.FromSeconds(15);
    private List<EngineIssue> _issues = [];
    private readonly HashSet<string> _restartKinds = [];
    private BufferLock? _bufferLock;
    private DateTime _nextLockAttempt = DateTime.MinValue;

    /// <summary>Clips queued and not yet finished, with their start time, so pruning keeps their audio.</summary>
    private readonly Dictionary<string, long> _pendingClips = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    /// <summary>A loop iteration that throws is logged and retried after this, so one bad state never stops recording.</summary>
    private static readonly TimeSpan FaultBackoff = TimeSpan.FromSeconds(5);
    private const string FaultMessage =
        "Something went wrong inside Clipstems and it is retrying. If this stays, the log (About › Open logs) has details.";
    private bool _faulted;

    public ClipstemsEngine(ClipstemsSettings settings, EngineOptions? options = null, ILoggerFactory? loggers = null)
    {
        _settings = SettingsStore.Validate(settings);
        _options = options ?? new EngineOptions();
        _loggers = loggers ?? NullLoggerFactory.Instance;
        _log = _loggers.CreateLogger<ClipstemsEngine>();
        _store = new SegmentStore(_options.BufferRoot, new OpusSegmentCodec());
        _compactor = new BufferCompactor(_store, _store.Codec!, _loggers.CreateLogger<BufferCompactor>());
    }

    /// <summary>Raised about once a second and on every state change, on a background thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when a clip has been handled, whatever the outcome, on a background thread.</summary>
    public event EventHandler<ClipOutcome>? ClipFinished;

    /// <summary>Raised when a new clip starts processing, on a background thread.</summary>
    public event EventHandler<string>? ClipStarted;

    public EngineStatus Status { get; private set; } = EngineStatus.Starting;
    public SteamGame? Game { get; private set; }
    public ClipstemsSettings Settings { get { lock (_gate) return _settings; } }

    public IReadOnlyList<EngineIssue> Issues { get { lock (_gate) return _issues; } }

    public IReadOnlyList<ClipOutcome> RecentClips { get { lock (_gate) return _recent.ToList(); } }

    /// <summary>
    /// The tracks to record: the user's enabled tracks, then (when mirroring)
    /// one per program ticked in Steam's "Record Audio from…" list that no
    /// user track names on its own. Mirrored ids are <c>steam-&lt;exe&gt;</c>.
    /// A user's single-app track claims that app even while switched off, so
    /// turning it off stops the app being recorded. A multi-app track (the
    /// voice-chat preset) records only the first one running, so it claims none.
    /// </summary>
    public static IReadOnlyList<TrackSettings> EffectiveTracks(ClipstemsSettings settings, SteamRecordingSettings steam)
    {
        var tracks = settings.Tracks.Where(t => t.Enabled).ToList();
        if (!settings.MirrorSteamPrograms) return tracks;
        var covered = settings.Tracks.Where(t => t.Source.Kind == SourceKind.Application && t.Source.Apps.Count == 1)
            .Select(t => t.Source.Apps[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var program in steam.ProgramsInMix)
        {
            if (!covered.Add(program.Exe)) continue;
            var id = "steam-" + new string(Path.GetFileNameWithoutExtension(program.Exe).ToLowerInvariant()
                .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
            if (tracks.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
            tracks.Add(new TrackSettings(id, program.Label, true, new CaptureSource(SourceKind.Application) { Apps = [program.Exe] }));
        }
        return tracks;
    }

    /// <summary>What Steam's own track contains, as read from Steam's settings.</summary>
    public SteamRecordingSettings SteamSettings { get { lock (_gate) return _steamSettings; } }

    private IReadOnlyList<TrackSettings> CurrentTracks()
    {
        lock (_gate) return EffectiveTracks(_settings, _steamSettings);
    }

    public IReadOnlyList<TrackStatus> Tracks
    {
        get
        {
            var current = CurrentTracks();
            bool gameSettled = Game is not null && DateTime.UtcNow - _gameStartedUtc > GameAudioGrace;
            lock (_gate)
            {
                return current.Select(t =>
                {
                    if (!_recorders.TryGetValue(t.Id, out var r)) return new TrackStatus(t.Id, t.Title, RecorderState.Stopped, null, null);
                    var state = r.Recorder.State;
                    bool failing = state == RecorderState.Retrying ||
                                   (state == RecorderState.Waiting && t.Source.Kind == SourceKind.SteamGame && gameSettled);
                    return new TrackStatus(t.Id, t.Title, state, r.Recorder.Detail, _peaks.GetValueOrDefault(t.Id), failing);
                }).ToList();
            }
        }
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (value) _log.LogInformation("Paused");
            else _log.LogInformation("Resumed");
        }
    }

    public void Start()
    {
        _main = Task.Run(() => MainLoop(_stop.Token));
        _attacher = Task.Run(() => AttachLoop(_stop.Token));
        if (_options.CompressBuffer) _compacting = Task.Run(() => CompactLoop(_stop.Token));
    }

    /// <summary>Applies new settings; recorders restart only if the tracks changed.</summary>
    public void UpdateSettings(ClipstemsSettings settings)
    {
        SettingsStore.Validate(settings);
        lock (_gate)
        {
            _settingsChanged |= TracksKey(EffectiveTracks(_settings, _steamSettings)) != TracksKey(EffectiveTracks(settings, _steamSettings));
            _settings = settings;
        }
    }

    /// <summary>Attaches one clip now, outside the watcher (e.g. a clip saved before Clipstems ran).</summary>
    public void Enqueue(string clipDirectory) => QueueClip(clipDirectory);

    private void QueueClip(string clipDirectory)
    {
        lock (_gate)
        {
            if (_pendingClips.ContainsKey(clipDirectory)) return; // already waiting its turn
        }
        long start = long.MaxValue;
        try { start = ClipInfo.Read(clipDirectory).CoarseStartUtc.Ticks; }
        catch (Exception ex) when (ex is not OperationCanceledException) { } // AttachOne reports why it can't be read
        lock (_gate) _pendingClips[clipDirectory] = start;
        _attachQueue.Writer.TryWrite(clipDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _attachQueue.Writer.TryComplete();
        try
        {
            foreach (var task in new[] { _main, _attacher, _compacting }.OfType<Task>())
            {
                try { await task; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _log.LogError(ex, "A Clipstems background task failed"); }
            }
        }
        finally
        {
            try { StopRecorders(); }
            catch (Exception ex) { _log.LogError(ex, "Stopping the recorders failed"); }
            _bufferLock?.Dispose();
            Status = EngineStatus.Stopped;
            _stop.Dispose();
        }
    }

    private async Task MainLoop(CancellationToken cancel)
    {
        DefaultDeviceWatcher? devices = null;
        try { devices = new DefaultDeviceWatcher(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            _log.LogWarning("Default-device changes will not be followed: {Error}", ex.Message);
        }
        if (devices is not null)
            devices.DefaultChanged += kind => { lock (_restartKinds) _restartKinds.Add(kind.ToString()); };

        bool loopbackOk = CaptureSession.ProcessLoopbackSupported(out var loopbackError);
        if (!loopbackOk) _log.LogWarning("Per-app capture unavailable: {Error}", loopbackError);
        var nextSteamRefresh = DateTime.MinValue;
        var nextClipPoll = DateTime.MinValue;
        var nextPrune = DateTime.MinValue;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var delay = Tick;
                try
                {
                    Iterate(DateTime.UtcNow, loopbackOk, ref nextSteamRefresh, ref nextClipPoll, ref nextPrune);
                    if (_faulted)
                    {
                        _faulted = false;
                        lock (_gate) _issues = _issues.Where(i => i.Message != FaultMessage).ToList();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Any failure (Steam's folders unreadable, disk errors) is retried, never fatal:
                    // a loop that dies silently would leave the tray claiming to record.
                    _log.LogError(ex, "Engine loop failed; retrying in {Delay}", FaultBackoff);
                    if (!_faulted)
                    {
                        _faulted = true;
                        lock (_gate) _issues = [.. _issues, new(IssueSeverity.Problem, FaultMessage)];
                    }
                    nextSteamRefresh = DateTime.MinValue; // re-read Steam's state on the next pass
                    delay = FaultBackoff;
                }
                Changed?.Invoke(this, EventArgs.Empty);
                await Task.Delay(delay, cancel);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            devices?.Dispose();
        }
    }

    private void Iterate(DateTime now, bool loopbackOk, ref DateTime nextSteamRefresh, ref DateTime nextClipPoll, ref DateTime nextPrune)
    {
        if (_bufferLock is null && !TakeBuffer(now)) return;
        if (now >= nextSteamRefresh)
        {
            nextSteamRefresh = now + SteamRefreshInterval;
            RefreshSteam();
            RefreshIssues(loopbackOk);
        }
        TrackGame(now);
        ApplyRecordingPolicy(now);
        RestartForDeviceChanges();
        SamplePeaks();

        // Without ffmpeg a clip would only fail, so new clips wait (unqueued) until it is installed.
        if (_options.AutoAttach && _watcher is not null && now >= nextClipPoll && Ffmpeg.Locate() is not null)
        {
            nextClipPoll = now + ClipPollInterval;
            foreach (var clip in _watcher.TakeResumed()) QueueClip(clip);
            foreach (var clip in _watcher.ReadyClips())
            {
                _watcher.MarkQueued(clip);
                QueueClip(clip);
            }
        }
        if (now >= nextPrune)
        {
            nextPrune = now + TimeSpan.FromMinutes(1);
            long cutoff = now.Ticks - TimeSpan.FromMinutes(Settings.RetentionMinutes).Ticks;
            lock (_gate)
            {
                // A clip waiting its turn keeps its audio, with the sync search margin to spare.
                if (_pendingClips.Count > 0)
                    cutoff = Math.Min(cutoff, _pendingClips.Values.Min() - TimeSpan.FromMinutes(1).Ticks);
            }
            int pruned = _store.Prune(cutoff);
            if (pruned > 0) _log.LogDebug("Pruned {Count} buffer segments", pruned);
        }
    }

    private const string BlockedMessage =
        "Another copy of Clipstems is already recording. This one takes over when that one closes.";

    /// <summary>Takes the buffer folder, or reports Blocked and retries every few seconds.</summary>
    private bool TakeBuffer(DateTime now)
    {
        if (now < _nextLockAttempt) return false;
        try
        {
            _bufferLock = BufferLock.TryAcquire(_options.BufferRoot);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _log.LogWarning("The buffer folder {Buffer} can't be opened: {Error}", _options.BufferRoot, ex.Message);
            lock (_gate) _issues = [new(IssueSeverity.Problem, $"Clipstems can't use its buffer folder ({ex.Message}).")];
            _nextLockAttempt = now + TimeSpan.FromSeconds(5);
            return false;
        }
        if (_bufferLock is not null)
        {
            lock (_gate) _issues = _issues.Where(i => i.Message != BlockedMessage).ToList();
            if (Status == EngineStatus.Blocked) _log.LogInformation("Buffer folder is free; taking over");
            return true;
        }
        if (Status != EngineStatus.Blocked) _log.LogWarning("Another Clipstems process owns {Buffer}; waiting", _options.BufferRoot);
        Status = EngineStatus.Blocked;
        lock (_gate) _issues = [new(IssueSeverity.Problem, BlockedMessage)];
        _nextLockAttempt = now + TimeSpan.FromSeconds(5);
        return false;
    }

    private void RefreshSteam()
    {
        _steamPath = SteamInstall.FindSteamPath();
        _steamUser = _steamPath is null ? null : SteamInstall.ActiveUser(_steamPath);
        var steamSettings = _steamUser?.Settings ?? SteamRecordingSettings.Empty;
        lock (_gate)
        {
            var before = TracksKey(EffectiveTracks(_settings, _steamSettings));
            _steamSettings = steamSettings;
            if (TracksKey(EffectiveTracks(_settings, _steamSettings)) != before)
            {
                _settingsChanged = true;
                _log.LogInformation("Steam's recorded programs changed: {Programs}",
                    string.Join(", ", steamSettings.ProgramsInMix.Select(p => p.Label)));
            }
        }
        var roots = _steamUser?.RecordingRoots() ?? [];
        if (_watcher is null || !_watcher.Roots.SequenceEqual(roots, StringComparer.OrdinalIgnoreCase))
        {
            _watcher = roots.Count == 0 ? null : new ClipWatcher(roots, _options.ProcessedLog);
            _log.LogInformation("Watching Steam clips in {Roots}", roots.Count == 0 ? "(none)" : string.Join(", ", roots));
        }
    }

    private void RefreshIssues(bool loopbackOk)
    {
        var issues = new List<EngineIssue>();
        var settings = Settings;
        if (_steamPath is null) issues.Add(new(IssueSeverity.Problem, "Steam is not installed, so there are no clips to add tracks to."));
        else if (_steamUser is null) issues.Add(new(IssueSeverity.Problem, "Open Steam and sign in so Clipstems can find your clips."));
        else if (_watcher is null) issues.Add(new(IssueSeverity.Warning, "Steam has no recordings folder yet; turn on Game Recording in Steam's settings."));
        if (_compactor.FailingSegments > 0)
            issues.Add(new(IssueSeverity.Warning,
                "Some recorded audio couldn't be compressed, so the buffer is using more disk than usual. Clipstems keeps retrying; the log (About › Open logs) has details."));
        if (_options.AutoAttach && Ffmpeg.Locate() is null)
            issues.Add(new(IssueSeverity.Problem, "FFmpeg is missing, so clips can't get their tracks yet. Install it (winget install Gyan.FFmpeg.Essentials); while Clipstems keeps running, clips you save meanwhile get their tracks once it's found."));
        if (!loopbackOk && settings.Tracks.Any(t => t.Enabled && t.Source.Kind is SourceKind.Application or SourceKind.SteamGame))
            issues.Add(new(IssueSeverity.Problem, "This version of Windows can't record a single app, so app and game tracks stay silent. Updating Windows fixes this."));
        if (_steamUser is { } user)
        {
            var steam = user.Settings;
            if (settings.MirrorSteamPrograms && steam.AudioOption is not null && !steam.RecordsSelectedPrograms &&
                !settings.Tracks.Any(t => t.Enabled && t.Source.Kind == SourceKind.Application))
                issues.Add(new(IssueSeverity.Tip,
                    "To get a separate track for Discord or another app, choose \"Game and Selected Programs\" in Steam > Settings > Game Recording and tick it, or add the app as a track in Clipstems."));
            if (steam.MicInMix != "1" && settings.Tracks.Any(t => t.Enabled && t.Source.Kind == SourceKind.Microphone))
                issues.Add(new(IssueSeverity.Tip,
                    "Turn on Record Microphone in Steam's Game Recording settings: Steam's track becomes a complete mix and Clipstems can line your mic track up exactly."));
        }
        if (_faulted) issues.Add(new(IssueSeverity.Problem, FaultMessage));
        lock (_gate) _issues = issues;
    }

    private void TrackGame(DateTime now)
    {
        var appId = SteamInstall.RunningAppId();
        if (appId is not null) _lastGameSeenUtc = now;
        if (appId == _gameAppId) return;
        _gameAppId = appId;
        _gameStartedUtc = now;
        Game = appId is { } id ? SteamGames.Describe(_steamPath, id) : null;
        if (Game is null) _log.LogInformation("Steam game exited");
        else _log.LogInformation("Steam game running: {Game} ({AppId})", Game.Name, Game.AppId);
    }

    private void ApplyRecordingPolicy(DateTime now)
    {
        bool gameActive = now - _lastGameSeenUtc < _options.GraceAfterGame;
        bool shouldRecord = !_paused && (Settings.RecordWhen == RecordPolicy.Always || gameActive);
        bool changed;
        lock (_gate)
        {
            changed = _settingsChanged;
            _settingsChanged = false;
        }

        if (shouldRecord && (_recorders.Count == 0 || changed))
        {
            SyncRecorders();
        }
        else if (!shouldRecord && _recorders.Count > 0)
        {
            StopRecorders();
        }
        Status = _paused ? EngineStatus.Paused : shouldRecord ? EngineStatus.Recording : EngineStatus.WaitingForGame;
    }

    /// <summary>
    /// Starts recorders for tracks that should record and stops the rest.
    /// A track whose source is unchanged keeps recording, so editing one track
    /// never leaves a gap in the others or in the sync reference.
    /// </summary>
    private void SyncRecorders()
    {
        var wanted = CurrentTracks()
            .Select(t => (Id: t.Id, Title: t.Title, Source: t.Source))
            .Append((Id: ReferenceTrackId, Title: "Sync reference", Source: new CaptureSource(SourceKind.AllExceptSelf)))
            .ToList();
        var stopping = new List<TrackRecorder>();
        var started = new List<string>();
        lock (_gate)
        {
            foreach (var (id, entry) in _recorders.ToList())
            {
                if (wanted.Any(w => w.Id == id && w.Source.Key == entry.Recorder.Source.Key)) continue;
                stopping.Add(entry.Recorder);
                _recorders.Remove(id);
                _peaks.Remove(id);
            }
        }
        foreach (var r in stopping) r.Dispose();
        foreach (var (id, title, source) in wanted)
        {
            lock (_gate)
            {
                if (_recorders.TryGetValue(id, out var existing))
                {
                    _recorders[id] = (existing.Recorder, title); // a rename needs no restart
                    continue;
                }
            }
            var recorder = new TrackRecorder(id, source, _store, _loggers.CreateLogger<TrackRecorder>());
            lock (_gate) _recorders[id] = (recorder, title);
            recorder.Start();
            started.Add($"{title} ({source})");
        }
        if (started.Count > 0) _log.LogInformation("Recording started: {Tracks}", string.Join(", ", started));
        if (stopping.Count > 0) _log.LogInformation("Recording stopped: {Tracks}", string.Join(", ", stopping.Select(r => r.Track)));
    }

    private void StopRecorders()
    {
        List<TrackRecorder> stopping;
        lock (_gate)
        {
            stopping = _recorders.Values.Select(r => r.Recorder).ToList();
            _recorders.Clear();
            _peaks.Clear();
        }
        if (stopping.Count == 0) return;
        foreach (var r in stopping) r.Dispose();
        _log.LogInformation("Recording stopped");
    }

    private void RestartForDeviceChanges()
    {
        string[] kinds;
        lock (_restartKinds)
        {
            kinds = _restartKinds.ToArray();
            _restartKinds.Clear();
        }
        if (kinds.Length == 0) return;
        lock (_gate)
        {
            foreach (var (recorder, _) in _recorders.Values)
            {
                var kind = recorder.Source.Kind == SourceKind.Microphone ? nameof(DeviceKind.Input) : nameof(DeviceKind.Output);
                if (recorder.Source.FollowsDefaultDevice && kinds.Contains(kind))
                {
                    _log.LogInformation("Default {Kind} device changed; restarting track {Track}", kind.ToLowerInvariant(), recorder.Track);
                    recorder.Restart();
                }
            }
        }
    }

    private void SamplePeaks()
    {
        lock (_gate)
        {
            foreach (var (id, (recorder, _)) in _recorders) _peaks[id] = recorder.TakePeakDbfs();
        }
    }

    /// <summary>
    /// Compresses closed segments in the background. Only the buffer's owner does
    /// it, and only with ffmpeg present; without it segments stay WAV and still read.
    /// </summary>
    private async Task CompactLoop(CancellationToken cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(CompactInterval, cancel);
                if (_bufferLock is null || Ffmpeg.Locate() is null) continue;
                try
                {
                    int n = _compactor.CompactClosed(DateTime.UtcNow, cancel);
                    if (n > 0) _log.LogDebug("Compressed {Count} buffer segments", n);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Compressing the buffer failed this pass");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task AttachLoop(CancellationToken cancel)
    {
        try
        {
            await foreach (var clipDir in _attachQueue.Reader.ReadAllAsync(cancel))
            {
                ClipStarted?.Invoke(this, clipDir);
                var outcome = await AttachOne(clipDir, cancel);
                try
                {
                    // Marked done only once handled, so a clip interrupted by quitting is retried on the next start.
                    _watcher?.MarkProcessed(clipDir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.LogWarning("Could not record clip {Clip} as processed: {Error}", Path.GetFileName(clipDir), ex.Message);
                }
                lock (_gate)
                {
                    _pendingClips.Remove(clipDir);
                    // A retried clip replaces its earlier outcome rather than listing twice.
                    _recent.RemoveAll(r => string.Equals(r.ClipDirectory, clipDir, StringComparison.OrdinalIgnoreCase));
                    _recent.Insert(0, outcome);
                    if (_recent.Count > RecentClipLimit) _recent.RemoveAt(_recent.Count - 1);
                }
                ClipFinished?.Invoke(this, outcome);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<ClipOutcome> AttachOne(string clipDir, CancellationToken cancel)
    {
        string? title = null;
        AttachResult result;
        try
        {
            var clip = ClipInfo.Read(clipDir);
            title = clip.Title;
            var ffmpeg = Ffmpeg.Locate();
            if (ffmpeg is null)
                result = new AttachResult(AttachStatus.Refused, "ffmpeg was not found, so the clip could not be written.", Problem: AttachProblem.FfmpegMissing);
            else
            {
                var options = new AttachOptions(
                    CurrentTracks().Select(t => new ExportTrack(t.Id, t.Title)).ToList(),
                    ReferenceTrackId,
                    Settings.EffectiveOutputFolder);
                result = await new ClipAttacher(_store, ffmpeg).AttachAsync(clip, options, cancel);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw; // quitting: the clip stays pending and is retried next start
        }
        catch (Exception ex)
        {
            // One unreadable clip (a Steam format change, a locked file) must never stop the clips after it.
            _log.LogError(ex, "Attaching clip {Clip} failed", Path.GetFileName(clipDir));
            result = new AttachResult(AttachStatus.Refused, ex.Message, Problem: AttachProblem.ExportFailed);
        }
        _log.Log(result.Status == AttachStatus.Written ? LogLevel.Information : LogLevel.Warning,
            "Clip {Clip}: {Status}: {Message} {Output}", Path.GetFileName(clipDir), result.Status, result.Message, result.OutputPath);
        return new ClipOutcome(clipDir, title, result, DateTime.UtcNow);
    }

    private static string TracksKey(IEnumerable<TrackSettings> tracks) =>
        string.Join(";", tracks.Select(t => $"{t.Id}={t.Source.Key}"));
}
