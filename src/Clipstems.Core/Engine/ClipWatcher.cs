using Clipstems.Core.Steam;

namespace Clipstems.Core.Engine;

/// <summary>
/// Polls Steam's clip folders for clips saved after the watcher started.
/// A clip is ready once its files have been still for a few seconds, so a
/// clip Steam is still copying is never read half-written. Polling, not
/// FileSystemWatcher, because the roots can be on any drive and a missed
/// event would silently skip a clip.
/// </summary>
public sealed class ClipWatcher
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);
    private readonly IReadOnlyList<string> _roots;
    private readonly string _processedLog;
    private readonly string _pendingLog;
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _resumed = [];

    /// <param name="processedLog">
    /// Clips already handled, one folder per line. A sibling <c>.pending</c>
    /// file lists clips that were queued but not finished, so a clip queued
    /// when Clipstems quit is attached on the next start rather than lost.
    /// </param>
    public ClipWatcher(IReadOnlyList<string> roots, string processedLog)
    {
        _roots = roots;
        _processedLog = processedLog;
        _pendingLog = processedLog + ".pending";
        var processed = ReadLines(processedLog);
        _known.UnionWith(processed);
        var pending = ReadLines(_pendingLog).Where(p => !processed.Contains(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Clips that already exist were made while Clipstems may not have been
        // recording; only clips saved from now on are attached automatically,
        // plus those an earlier run queued and did not finish.
        foreach (var clip in ClipCatalog.List(roots))
        {
            if (pending.Contains(clip.Directory)) _resumed.Add(clip.Directory);
            else _known.Add(clip.Directory);
        }
        _known.UnionWith(pending);
        try
        {
            // Keep the pending log to what is still pending, so it stays short.
            if (File.Exists(_pendingLog)) File.WriteAllLines(_pendingLog, _resumed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public IReadOnlyList<string> Roots => _roots;

    /// <summary>Clips an earlier run queued and did not finish; to be queued again once.</summary>
    public IReadOnlyList<string> TakeResumed()
    {
        var resumed = _resumed.ToList();
        _resumed.Clear();
        return resumed;
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

    /// <summary>The clip is queued for attaching: it is not offered again, and is retried after a restart until finished.</summary>
    public void MarkQueued(string clipDirectory)
    {
        _known.Add(clipDirectory);
        Append(_pendingLog, clipDirectory);
    }

    /// <summary>The clip has been handled, whatever the outcome; it is never attached automatically again.</summary>
    public void MarkProcessed(string clipDirectory)
    {
        _known.Add(clipDirectory);
        Append(_processedLog, clipDirectory);
    }

    private static void Append(string log, string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.AppendAllLines(log, [line]);
    }

    private static HashSet<string> ReadLines(string path) =>
        File.Exists(path)
            ? File.ReadAllLines(path).Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static DateTime NewestWrite(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                .Select(f => f.LastWriteTimeUtc)
                .DefaultIfEmpty(Directory.GetLastWriteTimeUtc(dir))
                .Max();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.UtcNow; // still changing or briefly locked; try again next poll
        }
    }
}
