using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Clipstems.App.Services;

/// <summary>
/// Keeps an installed Clipstems current from the project's GitHub releases:
/// checks soon after start and then every few hours, downloads in the
/// background, and installs when Clipstems next exits or restarts. Recording
/// is never interrupted for an update. Does nothing in a development build,
/// which Velopack did not install.
/// </summary>
public sealed class Updater : IDisposable
{
    public const string ReleasesUrl = "https://github.com/Jmcasavant/clipstems";

    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly UpdateManager? _manager;
    private readonly ILogger _log;
    private readonly Func<bool> _enabled;
    private readonly SemaphoreSlim _checking = new(1, 1);
    private Timer? _timer;
    private VelopackAsset? _ready;

    public Updater(ILogger log, Func<bool> enabled)
    {
        _log = log;
        _enabled = enabled;
        try
        {
            var manager = new UpdateManager(new GithubSource(ReleasesUrl, accessToken: null, prerelease: false));
            if (manager.IsInstalled)
            {
                _manager = manager;
                _ready = manager.UpdatePendingRestart; // downloaded by an earlier run
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Updates are unavailable: {Error}", ex.Message);
        }
    }

    /// <summary>Raised on a background thread when a new version has been downloaded.</summary>
    public event EventHandler? UpdateReady;

    /// <summary>False in development builds; updates only apply to an installed copy.</summary>
    public bool IsInstalled => _manager is not null;

    /// <summary>The downloaded version waiting to be installed, if any.</summary>
    public string? ReadyVersion => _ready?.Version?.ToString();

    /// <summary>What the About page shows about updates.</summary>
    public string Status { get; private set; } = "";

    public void Start()
    {
        if (_manager is null)
        {
            Status = "Updates apply to the installed version only.";
            return;
        }
        Status = ReadyVersion is { } v ? ReadyText(v) : "Checks for updates automatically.";
        _timer = new Timer(_ => _ = CheckAsync(manual: false), null, FirstCheck, CheckInterval);
    }

    /// <summary>Checks and downloads now; the result is in <see cref="Status"/>.</summary>
    public async Task CheckAsync(bool manual)
    {
        if (_manager is null || (!manual && !_enabled())) return;
        if (!await _checking.WaitAsync(0)) return; // a check is already running
        try
        {
            Status = "Checking for updates…";
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null)
            {
                Status = ReadyVersion is { } pending ? ReadyText(pending) : $"Up to date (checked {DateTime.Now:t}).";
                return;
            }
            var version = update.TargetFullRelease.Version.ToString();
            Status = $"Downloading version {version}…";
            await _manager.DownloadUpdatesAsync(update);
            _ready = update.TargetFullRelease;
            Status = ReadyText(version);
            _log.LogInformation("Update {Version} downloaded; it installs when Clipstems restarts", version);
            UpdateReady?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // Offline, GitHub rate limits, a release mid-upload: try again at the next check.
            Status = "Couldn't check for updates. Clipstems tries again later.";
            _log.LogWarning("Update check failed: {Error}", ex.Message);
        }
        finally
        {
            _checking.Release();
        }
    }

    /// <summary>
    /// Call as the app exits: hands a downloaded update to Velopack's updater,
    /// which waits for this process to end, installs, and restarts it if asked.
    /// </summary>
    public void ApplyOnExit(bool restart)
    {
        if (_manager is null || _ready is null) return;
        try
        {
            _manager.WaitExitThenApplyUpdates(_ready, silent: true, restart: restart, restartArgs: restart ? [Program.BackgroundSwitch] : null);
            _log.LogInformation("Installing update {Version} after exit", ReadyVersion);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not start the update: {Error}", ex.Message);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _checking.Dispose();
    }

    private static string ReadyText(string version) => $"Version {version} is ready and installs when Clipstems restarts.";
}
