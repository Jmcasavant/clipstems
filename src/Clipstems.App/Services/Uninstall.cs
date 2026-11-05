using System.IO;
using Clipstems.Core;

namespace Clipstems.App.Services;

/// <summary>
/// What uninstalling removes besides the program itself (which Velopack
/// deletes from its own install folder). The audio buffer goes: it holds
/// recordings of the microphone and can be gigabytes. Settings and logs are
/// small and stay, so reinstalling picks up where the user left off.
/// </summary>
public static class Uninstall
{
    /// <summary>Runs inside Velopack's uninstall hook, which allows 30 seconds; never throws.</summary>
    public static void CleanUp()
    {
        StartupRegistration.Apply(enabled: false);
        DeleteQuietly(Path.Combine(ClipstemsPaths.DataRoot, "buffer")); // the default location only, never a custom CLIPSTEMS_BUFFER
        DeleteQuietly(ClipstemsPaths.Temp);
    }

    private static void DeleteQuietly(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still held open; the rest is gone, and nothing reads this folder once uninstalled.
        }
    }
}
