using Sidetrack.Core.Capture;
using Sidetrack.Core.Settings;

namespace Sidetrack.Tests;

public class SettingsTests
{
    [Fact]
    public void Missing_file_gives_defaults_with_mic_and_game_on_and_steam_programs_mirrored()
    {
        using var dir = new TempDir();
        var settings = new SettingsStore(Path.Combine(dir.Path, "settings.json")).Load(out var problem);

        Assert.Null(problem);
        Assert.Equal(["mic", "game"], settings.Tracks.Where(t => t.Enabled).Select(t => t.Id));
        Assert.True(settings.MirrorSteamPrograms);
        Assert.Equal(RecordPolicy.WhileSteamGameRuns, settings.RecordWhen);
    }

    [Fact]
    public void Settings_round_trip_including_sources()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(Path.Combine(dir.Path, "settings.json"));
        var saved = new SidetrackSettings
        {
            RecordWhen = RecordPolicy.Always,
            OutputFolder = @"D:\Clips",
            Tracks =
            [
                new("mic", "Mic", true, new CaptureSource(SourceKind.Microphone) { Device = "Yeti" }),
                new("ts", "TeamSpeak", true, new CaptureSource(SourceKind.Application) { Apps = ["TeamSpeak.exe"] }),
            ],
        };

        store.Save(saved);
        var loaded = store.Load(out var problem);

        Assert.Null(problem);
        Assert.Equal(RecordPolicy.Always, loaded.RecordWhen);
        Assert.Equal(@"D:\Clips", loaded.OutputFolder);
        Assert.Equal(saved.Tracks.Select(t => t.Source.Key), loaded.Tracks.Select(t => t.Source.Key));
        Assert.Equal(["Mic", "TeamSpeak"], loaded.Tracks.Select(t => t.Title));
    }

    [Fact]
    public void Unreadable_file_is_set_aside_not_overwritten()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, "{ not json");

        var settings = new SettingsStore(path).Load(out var problem);

        Assert.NotNull(problem);
        Assert.False(File.Exists(path));
        var aside = Assert.Single(Directory.GetFiles(dir.Path, "settings.json.bad-*"));
        Assert.Equal("{ not json", File.ReadAllText(aside));
        Assert.Equal(SidetrackSettings.DefaultTracks.Count, settings.Tracks.Count);
    }

    [Fact]
    public void File_from_a_newer_version_is_left_alone()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, """{ "version": 99, "retentionMinutes": 30 }""");

        new SettingsStore(path).Load(out var problem);

        Assert.Contains("newer", problem);
        Assert.Contains("\"version\": 99", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("_reference")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Track_ids_that_would_break_the_buffer_are_refused(string id)
    {
        var settings = new SidetrackSettings { Tracks = [new(id, "X", true, new CaptureSource(SourceKind.Microphone))] };
        Assert.Throws<ArgumentException>(() => SettingsStore.Validate(settings));
    }

    [Fact]
    public void Duplicate_track_ids_are_refused()
    {
        var mic = new TrackSettings("mic", "Mic", true, new CaptureSource(SourceKind.Microphone));
        Assert.Throws<ArgumentException>(() => SettingsStore.Validate(new SidetrackSettings { Tracks = [mic, mic with { Title = "Mic 2" }] }));
    }

    [Fact]
    public void App_track_without_apps_is_refused()
    {
        var settings = new SidetrackSettings { Tracks = [new("x", "X", true, new CaptureSource(SourceKind.Application))] };
        Assert.Throws<ArgumentException>(() => SettingsStore.Validate(settings));
    }
}
