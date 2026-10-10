using Clipstems.App.Services;

namespace Clipstems.App;

public static class Program
{
    /// <summary>Command-line switch the Windows startup entry passes: start in the tray without opening Settings.</summary>
    public const string BackgroundSwitch = "--background";

    [STAThread]
    public static int Main(string[] args)
    {
        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            // Already running: ask that instance to show Settings, then leave.
            SingleInstance.SignalExisting();
            return 0;
        }

        var app = new App(instance, startInBackground: args.Contains(BackgroundSwitch, StringComparer.OrdinalIgnoreCase));
        app.InitializeComponent();
        return app.Run();
    }
}
