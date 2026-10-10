using System.Diagnostics;
using System.IO;

namespace Clipstems.App.Services;

/// <summary>Opens files and folders the way Explorer would.</summary>
public static class Shell
{
    public static void Open(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }

    /// <summary>Opens a URI with its registered handler, such as steam://.</summary>
    public static void OpenUri(string uri) =>
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();

    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Open(folder);
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    public static void Reveal(string file)
    {
        if (!File.Exists(file)) return;
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", file } })?.Dispose();
    }
}
