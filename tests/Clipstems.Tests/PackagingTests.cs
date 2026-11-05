using System.Text.Json;
using System.Text.RegularExpressions;
using Clipstems.Core;
using Clipstems.Core.Media;
using Clipstems.Core.Settings;

namespace Clipstems.Tests;

/// <summary>The installer's contract with the app: where FFmpeg is found, and what the packaging files promise.</summary>
public class PackagingTests
{
    private static string RepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"{Path.Combine(parts)} not found above {AppContext.BaseDirectory}");
    }

    private static void FakeFfmpeg(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "ffmpeg.exe"), []);
        File.WriteAllBytes(Path.Combine(folder, "ffprobe.exe"), []);
    }

    [Fact]
    public void The_bundled_ffmpeg_is_used_when_nothing_overrides_it()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLIPSTEMS_FFMPEG"))) return; // an explicit override wins by design
        using var dir = new TempDir();
        FakeFfmpeg(Path.Combine(dir.Path, Ffmpeg.BundledFolderName));

        var found = Ffmpeg.Locate(dir.Path);

        Assert.NotNull(found);
        Assert.True(found.IsBundled);
        Assert.Equal(Path.Combine(dir.Path, "ffmpeg", "ffmpeg.exe"), found.FfmpegPath);
        Assert.Equal(Path.Combine(dir.Path, "ffmpeg", "ffprobe.exe"), found.FfprobePath);
    }

    [Fact]
    public void A_bundle_missing_ffprobe_is_not_used()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, Ffmpeg.BundledFolderName);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "ffmpeg.exe"), []);

        Assert.False(Ffmpeg.Locate(dir.Path)?.IsBundled ?? false);
    }

    [Fact]
    public void Settings_saved_before_updates_existed_turn_them_on()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, """{ "version": 1, "notifications": false }""");

        var settings = new SettingsStore(path).Load(out var problem);

        Assert.Null(problem);
        Assert.True(settings.AutoUpdate);
        Assert.False(settings.Notifications);
    }

    [Fact]
    public void The_pinned_ffmpeg_is_an_lgpl_build_with_a_checksum_and_source()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RepoFile("build", "ffmpeg.json")));
        var root = doc.RootElement;
        string Get(string name) => root.GetProperty(name).GetString()!;

        Assert.Matches("^https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-[0-9-]+/.+-win64-lgpl-shared-[0-9.]+\\.zip$", Get("url"));
        Assert.Matches("^[0-9a-f]{64}$", Get("sha256"));
        Assert.StartsWith("LGPL", Get("license"), StringComparison.Ordinal);
        Assert.Contains(Get("version"), Get("url"), StringComparison.Ordinal);
        Assert.StartsWith("https://", Get("source"), StringComparison.Ordinal);
        Assert.StartsWith("https://", Get("buildScripts"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_install_folder_is_never_the_data_folder()
    {
        // Velopack replaces %LocalAppData%\<packId> on install and deletes it on uninstall.
        // If that were %LocalAppData%\Clipstems, installing would wipe settings, logs and recorded audio.
        var script = File.ReadAllText(RepoFile("build", "package.ps1"));
        var packId = Regex.Match(script, @"^\$PackId\s*=\s*'([^']+)'", RegexOptions.Multiline).Groups[1].Value;

        Assert.NotEmpty(packId);
        Assert.NotEqual(Path.GetFileName(ClipstemsPaths.DataRoot), packId, StringComparer.OrdinalIgnoreCase);
    }
}
