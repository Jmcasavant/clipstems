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

public partial class App : Application
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartLogging();

        var settings = _settingsStore.Load(out _settingsProblem);
        if (_settingsProblem is not null) _log!.LogWarning("{Problem}", _settingsProblem);
        bool firstRun = !File.Exists(_settingsStore.Path);
        if (firstRun) _settingsStore.Save(settings);
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
        _tray?.Dispose();
        if (_engine is not null)
        {
            // Bounded: never let a stuck capture keep the process alive after Quit.
            Task.Run(async () => await _engine.DisposeAsync()).Wait(TimeSpan.FromSeconds(10));
        }
        _log?.LogInformation("Clipstems stopped");
        Log.CloseAndFlush();
        base.OnExit(e);
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
            .WriteTo.File(Path.Combine(LogFolder, "clipstems-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        _loggers = new SerilogLoggerFactory(Log.Logger, dispose: false);
        _log = _loggers.CreateLogger<App>();

        DispatcherUnhandledException += (_, e) =>
        {
            _log.LogError(e.Exception, "Unhandled UI exception");
            e.Handled = true; // keep recording; the tray stays usable
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
