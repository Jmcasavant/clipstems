namespace Clipstems.Core;

/// <summary>Where Clipstems keeps its own files. Nothing here is inside a Steam folder.</summary>
public static class ClipstemsPaths
{
    public static string DataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipstems");

    /// <summary>Rolling audio buffer; overridable with <c>CLIPSTEMS_BUFFER</c>.</summary>
    public static string Buffer =>
        Environment.GetEnvironmentVariable("CLIPSTEMS_BUFFER") is { Length: > 0 } custom ? custom : Path.Combine(DataRoot, "buffer");

    /// <summary>Clips the watcher has already handled, one folder path per line.</summary>
    public static string ProcessedLog => Path.Combine(DataRoot, "processed.txt");

    public static string DefaultOutput =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Clipstems");

    public static string Temp => Path.Combine(Path.GetTempPath(), "clipstems");

    /// <summary>
    /// Device names Windows reserves in any case, with or without an extension
    /// (learn.microsoft.com/windows/win32/fileio/naming-a-file, verified 2026-10-10).
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL",
         .. Enumerable.Range(1, 9).Select(n => $"COM{n}"), .. Enumerable.Range(1, 9).Select(n => $"LPT{n}"),
         "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³"],
        StringComparer.OrdinalIgnoreCase);

    private static bool IsReservedName(string name) => ReservedNames.Contains(name.Split('.')[0].TrimEnd());

    /// <summary>
    /// A path clips can be saved under: a full path on a drive that exists and
    /// not an existing file. The folder itself is created at the first export,
    /// not while someone is typing it.
    /// </summary>
    public static bool IsUsableOutputFolder(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || Path.GetPathRoot(path) is not { Length: > 0 } root || !Directory.Exists(root))
                return false;
            // Each folder name must be a valid file name (no * ? : etc.), and no part of the path may be a file.
            var names = path[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var invalid = Path.GetInvalidFileNameChars();
            var current = root;
            foreach (var name in names.Where(n => n.Length > 0))
            {
                if (name.IndexOfAny(invalid) >= 0 || name.EndsWith('.') || name.EndsWith(' ') || IsReservedName(name)) return false;
                current = Path.Combine(current, name);
                if (File.Exists(current)) return false; // missing folders are fine: the first export creates them
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
