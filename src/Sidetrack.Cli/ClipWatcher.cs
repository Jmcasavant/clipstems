using Sidetrack.Core.Steam;

namespace Sidetrack.Cli;

/// <summary>
/// Polls Steam's clip folders for clips saved after the watcher started.
/// A clip is ready once its files have been still for a few seconds, so a
/// clip Steam is still copying is never read half-written. Polling, not
/// FileSystemWatcher, because the roots can be on any drive and a missed
/// event would silently skip a clip.
/// </summary>
internal sealed class ClipWatcher
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);
    private readonly IReadOnlyList<string> _roots;
    private readonly string _processedLog;
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);

    public ClipWatcher(IReadOnlyList<string> roots, string processedLog)
    {
        _roots = roots;
        _processedLog = processedLog;
        if (File.Exists(processedLog)) _known.UnionWith(File.ReadAllLines(processedLog));
        // Clips that already exist were made while Sidetrack may not have been
        // recording; only clips saved from now on are attached automatically.
        _known.UnionWith(ClipCatalog.List(roots).Select(c => c.Directory));
    }

    public IEnumerable<string> ReadyClips()
    {
        foreach (var clip in ClipCatalog.List(_roots))
        {
            if (_known.Contains(clip.Directory)) continue;
            if (DateTime.UtcNow - NewestWrite(clip.Directory) < Settle) continue;
            yield return clip.Directory;
        }
    }

    public void MarkProcessed(string clipDirectory)
    {
        _known.Add(clipDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(_processedLog)!);
        File.AppendAllLines(_processedLog, [clipDirectory]);
    }

    private static DateTime NewestWrite(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                .Select(f => f.LastWriteTimeUtc)
                .DefaultIfEmpty(Directory.GetLastWriteTimeUtc(dir))
                .Max();
        }
        catch (IOException)
        {
            return DateTime.UtcNow; // still changing; try again next poll
        }
    }
}
