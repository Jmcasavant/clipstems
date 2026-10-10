namespace Clipstems.Core.Steam;

/// <summary>A clip folder found under a recording root.</summary>
public sealed record ClipRef(string Directory, string Root)
{
    public string Name => Path.GetFileName(Directory);
}

public static class ClipCatalog
{
    /// <summary>Clip folders (those with a <c>clip.pb</c>) under each root, oldest first by folder time.</summary>
    public static IReadOnlyList<ClipRef> List(IEnumerable<string> roots) =>
        roots.SelectMany(root =>
            {
                var clips = Path.Combine(root, "clips");
                return System.IO.Directory.Exists(clips)
                    ? System.IO.Directory.GetDirectories(clips, "clip_*")
                        .Where(d => File.Exists(Path.Combine(d, "clip.pb")))
                        .Select(d => new ClipRef(d, root))
                    : [];
            })
            .OrderBy(c => System.IO.Directory.GetCreationTimeUtc(c.Directory))
            .ToList();
}
