using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Clipstems.App.Services;
using Clipstems.App.Settings;
using Clipstems.App.Tray;
using Clipstems.Core;
using Clipstems.Core.Engine;
using Clipstems.Core.Settings;

namespace Clipstems.App;

#pragma warning disable CA1001 // WPF owns the App's lifetime; OnExit disposes the engine, tray and loggers
public partial class App : Application
#pragma warning restore CA1001
{
    private readonly SingleInstance _instance;
    private readonly bool _startInBackground;
    private readonly SettingsStore _settingsStore = SettingsStore.Default;
    private ILoggerFactory? _loggers;
    private Microsoft.Extensions.Logging.ILogger? _log;
    private ClipstemsEngine? _engine;
    private TrayController? _tray;
    private SettingsWindow? _settingsWindow;
    private string? _settingsProblem;

    public App(SingleInstance instance, bool startInBackground)
    {
        _instance = instance;
        _startInBackground = startInBackground;
    }

    public static string LogFolder => Path.Combine(ClipstemsPaths.DataRoot, "logs");

    private bool _started;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartLogging();
        try
        {
            Start();
            _started = true;
        }
        catch (Exception ex)
        {
            // Without this the dispatcher handler would swallow the failure and leave a process
            // with no tray icon holding the single-instance lock, so no later launch could start.
            _log!.LogCritical(ex, "Clipstems could not start");
            MessageBox.Show($"Clipstems could not start:\n\n{ex.Message}\n\nThe log in {LogFolder} has details.",
                "Clipstems", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void Start()
    {
        var settings = _settingsStore.Load(out _settingsProblem);
        if (_settingsProblem is not null) _log!.LogWarning("{Problem}", _settingsProblem);
        bool firstRun = !File.Exists(_settingsStore.Path);
        if (firstRun)
        {
            try { _settingsStore.Save(settings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log!.LogWarning("Saving first-run settings failed: {Error}", ex.Message);
            }
        }
        if (StartupRegistration.Apply(settings.StartWithWindows) is { } skipped) _log!.LogInformation("{Why}", skipped);

        _engine = new ClipstemsEngine(settings, loggers: _loggers);
        _tray = new TrayController(_engine, ShowSettings, Quit);
        _engine.Changed += (_, _) => Dispatcher.BeginInvoke(() => _tray?.Refresh());
        _engine.ClipFinished += (_, outcome) => Dispatcher.BeginInvoke(() => _tray?.NotifyClip(outcome));
        _engine.Start();
        _instance.Listen(() => Dispatcher.BeginInvoke(ShowSettings));
        _log!.LogInformation("Clipstems {Version} started", typeof(App).Assembly.GetName().Version);

        // First run, or a manual launch: show Settings so the user sees what it will record.
        if (firstRun || !_startInBackground) ShowSettings();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _tray?.Dispose();
            if (_engine is not null)
            {
                // Bounded: never let a stuck capture keep the process alive after Quit.
                if (!Task.Run(async () => await _engine.DisposeAsync()).Wait(TimeSpan.FromSeconds(10)))
                    _log?.LogWarning("Stopping took longer than 10 s; exiting anyway");
            }
            _log?.LogInformation("Clipstems stopped");
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Shutting down failed");
        }
        finally
        {
            Log.CloseAndFlush(); // the last lines are the ones that explain a bad exit
            base.OnExit(e);
        }
    }

    private void ShowSettings()
    {
        if (_engine is null) return;
        if (_settingsWindow is { IsLoaded: true })
        {
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }
        var viewModel = new SettingsViewModel(_engine, _settingsStore, _loggers!.CreateLogger<SettingsViewModel>(), _settingsProblem);
        _settingsProblem = null;
        _settingsWindow = new SettingsWindow(viewModel);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void Quit() => Shutdown();

    private void StartLogging()
    {
        Directory.CreateDirectory(LogFolder);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(LogFolder, "clipstems-.log"), formatProvider: System.Globalization.CultureInfo.InvariantCulture, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        _loggers = new SerilogLoggerFactory(Log.Logger, dispose: false);
        _log = _loggers.CreateLogger<App>();

        DispatcherUnhandledException += (_, e) =>
        {
            _log.LogError(e.Exception, "Unhandled UI exception");
            // Keep recording once running; the tray stays usable. Before then, OnStartup reports it.
            e.Handled = _started;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            _log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception; terminating={Terminating}", e.IsTerminating);
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _log.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }
}
