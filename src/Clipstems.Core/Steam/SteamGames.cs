namespace Clipstems.Core.Steam;

/// <summary>A Steam game Steam reports as running, with where it is installed.</summary>
/// <param name="InstallDirectory">Null when no library holds its manifest (a non-Steam shortcut, or an uninstalled app).</param>
public sealed record SteamGame(uint AppId, string Name, string? InstallDirectory);

/// <summary>Reads Steam's library folders and app manifests. Read-only.</summary>
public static class SteamGames
{
    /// <summary>The game Steam reports as running, or null when none is.</summary>
    public static SteamGame? Running(string? steamPath)
    {
        if (SteamInstall.RunningAppId() is not { } appId) return null;
        return Describe(steamPath, appId);
    }

    public static SteamGame Describe(string? steamPath, uint appId)
    {
        foreach (var library in steamPath is null ? [] : LibraryFolders(steamPath))
        {
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest)) continue;
            VdfNode? state;
            try { state = Vdf.Parse(File.ReadAllText(manifest)).Find("AppState"); }
            catch (Exception ex) when (ex is IOException or FormatException) { continue; }
            if (state is null) continue;
            var name = state["name"] ?? $"App {appId}";
            var dir = state["installdir"] is { Length: > 0 } installDir
                ? Path.Combine(library, "steamapps", "common", installDir)
                : null;
            return new SteamGame(appId, name, dir is not null && Directory.Exists(dir) ? dir : null);
        }
        return new SteamGame(appId, $"App {appId}", null);
    }

    /// <summary>Every existing library folder in <c>steamapps/libraryfolders.vdf</c>, Steam's own folder first.</summary>
    public static IReadOnlyList<string> LibraryFolders(string steamPath)
    {
        var folders = new List<string> { steamPath };
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return folders;
        VdfNode? root;
        try { root = Vdf.Parse(File.ReadAllText(vdf)).Find("libraryfolders"); }
        catch (Exception ex) when (ex is IOException or FormatException) { return folders; }
        foreach (var library in root?.Children ?? [])
        {
            if (library["path"] is not { Length: > 0 } path || !Directory.Exists(path)) continue;
            if (!folders.Any(f => string.Equals(Path.GetFullPath(f), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
                folders.Add(path);
        }
        return folders;
    }
}
