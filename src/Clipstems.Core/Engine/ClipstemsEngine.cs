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

    public ClipstemsEngine(ClipstemsSettings settings, EngineOptions? options = null, ILoggerFactory? loggers = null)
    {
        _settings = SettingsStore.Validate(settings);
        _options = options ?? new EngineOptions();
        _loggers = loggers ?? NullLoggerFactory.Instance;
        _log = _loggers.CreateLogger<ClipstemsEngine>();
        _store = new SegmentStore(_options.BufferRoot);
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
            _log.LogInformation(value ? "Paused" : "Resumed");
        }
    }

    public void Start()
    {
        _main = Task.Run(() => MainLoop(_stop.Token));
        _attacher = Task.Run(() => AttachLoop(_stop.Token));
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
    public void Enqueue(string clipDirectory) => _attachQueue.Writer.TryWrite(clipDirectory);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _attachQueue.Writer.TryComplete();
        foreach (var task in new[] { _main, _attacher }.OfType<Task>())
        {
            try { await task; }
            catch (OperationCanceledException) { }
        }
        StopRecorders();
        _bufferLock?.Dispose();
        Status = EngineStatus.Stopped;
        _stop.Dispose();
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
                var now = DateTime.UtcNow;
                if (_bufferLock is null && !TakeBuffer(now))
                {
                    Changed?.Invoke(this, EventArgs.Empty);
                    await Task.Delay(Tick, cancel);
                    continue;
                }
                if (now >= nextSteamRefresh)
                {
                    RefreshSteam();
                    RefreshIssues(loopbackOk);
                    nextSteamRefresh = now + SteamRefreshInterval;
                }
                TrackGame(now);
                ApplyRecordingPolicy(now);
                RestartForDeviceChanges();
                SamplePeaks();

                // Without ffmpeg a clip would only fail, so new clips wait (unmarked) until it is installed.
                if (_options.AutoAttach && _watcher is not null && now >= nextClipPoll && Ffmpeg.Locate() is not null)
                {
                    foreach (var clip in _watcher.ReadyClips())
                    {
                        _watcher.MarkProcessed(clip);
                        _attachQueue.Writer.TryWrite(clip);
                    }
                    nextClipPoll = now + ClipPollInterval;
                }
                if (now >= nextPrune)
                {
                    int pruned = _store.Prune(now.Ticks - TimeSpan.FromMinutes(Settings.RetentionMinutes).Ticks);
                    if (pruned > 0) _log.LogDebug("Pruned {Count} buffer segments", pruned);
                    nextPrune = now + TimeSpan.FromMinutes(1);
                }

                Changed?.Invoke(this, EventArgs.Empty);
                await Task.Delay(Tick, cancel);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            devices?.Dispose();
        }
    }

    private const string BlockedMessage =
        "Another copy of Clipstems is already recording. This one takes over when that one closes.";

    /// <summary>Takes the buffer folder, or reports Blocked and retries every few seconds.</summary>
    private bool TakeBuffer(DateTime now)
    {
        if (now < _nextLockAttempt) return false;
        _bufferLock = BufferLock.TryAcquire(_options.BufferRoot);
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
        _log.LogInformation(Game is null ? "Steam game exited" : "Steam game running: {Game} ({AppId})", Game?.Name, Game?.AppId);
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
            StopRecorders();
            StartRecorders();
        }
        else if (!shouldRecord && _recorders.Count > 0)
        {
            StopRecorders();
        }
        Status = _paused ? EngineStatus.Paused : shouldRecord ? EngineStatus.Recording : EngineStatus.WaitingForGame;
    }

    private void StartRecorders()
    {
        var tracks = CurrentTracks()
            .Select(t => (t.Id, t.Title, t.Source))
            .Append((ReferenceTrackId, "Sync reference", new CaptureSource(SourceKind.AllExceptSelf)));
        lock (_gate)
        {
            foreach (var (id, title, source) in tracks)
            {
                var recorder = new TrackRecorder(id, source, _store, _loggers.CreateLogger<TrackRecorder>());
                _recorders[id] = (recorder, title);
                recorder.Start();
            }
        }
        _log.LogInformation("Recording started: {Tracks}", string.Join(", ", tracks.Select(t => $"{t.Item2} ({t.Item3})")));
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

    private async Task AttachLoop(CancellationToken cancel)
    {
        try
        {
            await foreach (var clipDir in _attachQueue.Reader.ReadAllAsync(cancel))
            {
                ClipStarted?.Invoke(this, clipDir);
                var outcome = await AttachOne(clipDir, cancel);
                lock (_gate)
                {
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
        catch (Exception ex) when (ex is IOException or FormatException or FfmpegException or UnauthorizedAccessException or InvalidOperationException)
        {
            result = new AttachResult(AttachStatus.Refused, ex.Message, Problem: AttachProblem.ExportFailed);
        }
        _log.Log(result.Status == AttachStatus.Written ? LogLevel.Information : LogLevel.Warning,
            "Clip {Clip}: {Status}: {Message} {Output}", Path.GetFileName(clipDir), result.Status, result.Message, result.OutputPath);
        return new ClipOutcome(clipDir, title, result, DateTime.UtcNow);
    }

    private static string TracksKey(IEnumerable<TrackSettings> tracks) =>
        string.Join(";", tracks.Select(t => $"{t.Id}={t.Source.Key}"));
}
