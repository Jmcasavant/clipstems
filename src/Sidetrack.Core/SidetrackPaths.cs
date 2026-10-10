namespace Sidetrack.Core;

/// <summary>Where Sidetrack keeps its own files. Nothing here is inside a Steam folder.</summary>
public static class SidetrackPaths
{
    public static string DataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sidetrack");

    /// <summary>Rolling audio buffer; overridable with <c>SIDETRACK_BUFFER</c>.</summary>
    public static string Buffer =>
        Environment.GetEnvironmentVariable("SIDETRACK_BUFFER") is { Length: > 0 } custom ? custom : Path.Combine(DataRoot, "buffer");

    /// <summary>Clips the watcher has already handled, one folder path per line.</summary>
    public static string ProcessedLog => Path.Combine(DataRoot, "processed.txt");

    public static string DefaultOutput =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Sidetrack");

    public static string Temp => Path.Combine(Path.GetTempPath(), "sidetrack");
}
