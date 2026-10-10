using Clipstems.Core.Capture;
using Clipstems.Core.Engine;
using Clipstems.Core.Settings;
using Clipstems.Core.Steam;

namespace Clipstems.Tests;

public class SteamSettingsTests
{
    /// <summary>The GameRecording block as Steam wrote it on 2026-10-10, nested the way localconfig.vdf nests it.</summary>
    private const string LocalConfig = """
        "UserLocalConfigStore"
        {
            "system"
            {
                "GameRecording"
                {
                    "BackgroundRecordMode"		"1"
                    "Audio_Mic"		"1"
                    "ForceMicMono"		"0"
                    "Recording_Audio_Option"		"2"
                    "ExtraAudioSessions"
                    {
                        "0"
                        {
                            "name"		"{0.0.0.00000000}.{18504bbe-5854-4efb-9ad0-add9cec79811}|\\Device\\HarddiskVolume3\\Users\\someone\\AppData\\Local\\Discord\\app-1.0.9261\\Discord.exe%b{00000000-0000-0000-0000-000000000000}"
                            "Label"		"Discord"
                        }
                    }
                    "BackgroundRecordPath"		"C:\\Users\\someone\\Videos"
                }
            }
        }
        """;

    private static SteamRecordingSettings Steam(string option = "2", params SteamAudioProgram[] programs) =>
        new(null, "1", option, "0", programs);

    [Fact]
    public void Vdf_parses_nesting_escapes_comments_and_conditionals()
    {
        var root = Vdf.Parse("""
            // comment
            "a"
            {
                "path"		"C:\\Games"
                "quote"		"say \"hi\""
                "cond"		"1"	[$WIN32]
                "inner" { "k" "v" }
            }
            """);

        var a = root.Child("a")!;
        Assert.Equal(@"C:\Games", a["path"]);
        Assert.Equal("say \"hi\"", a["quote"]);
        Assert.Equal("1", a["COND"]);
        Assert.Equal("v", root.Find("inner")!["k"]);
    }

    [Theory]
    [InlineData("\"a\" { \"b\" \"c\"")]
    [InlineData("\"a\" \"unterminated")]
    [InlineData("}")]
    public void Malformed_vdf_is_a_format_error(string text) =>
        Assert.Throws<FormatException>(() => Vdf.Parse(text));

    [Fact]
    public void Game_recording_block_yields_programs_mic_and_path()
    {
        var settings = SteamRecordingSettings.FromLocalConfig(Vdf.Parse(LocalConfig));

        Assert.Equal("Game and Selected Programs", settings.AudioOptionDescription);
        Assert.Equal("1", settings.MicInMix);
        Assert.Equal(@"C:\Users\someone\Videos", settings.BackgroundRecordPath);
        var program = Assert.Single(settings.ProgramsInMix);
        Assert.Equal(new SteamAudioProgram("Discord.exe", "Discord"), program);
    }

    [Fact]
    public void Config_without_a_recording_block_is_empty_not_an_error()
    {
        var settings = SteamRecordingSettings.FromLocalConfig(Vdf.Parse("\"UserLocalConfigStore\" { }"));
        Assert.Same(SteamRecordingSettings.Empty, settings);
    }

    [Fact]
    public void Ticked_programs_are_ignored_when_steam_records_something_else()
    {
        // Steam keeps the list when another option is chosen, but no longer records it.
        Assert.Empty(Steam("0", new SteamAudioProgram("Discord.exe", "Discord")).ProgramsInMix);
    }

    [Fact]
    public void Each_program_steam_records_becomes_its_own_track_after_the_users()
    {
        var tracks = ClipstemsEngine.EffectiveTracks(new ClipstemsSettings(),
            Steam("2", new SteamAudioProgram("Discord.exe", "Discord"), new SteamAudioProgram("Spotify.exe", "Spotify")));

        Assert.Equal(["mic", "game", "steam-discord", "steam-spotify"], tracks.Select(t => t.Id));
        Assert.Equal("Discord", tracks[2].Title);
        Assert.Equal(["Discord.exe"], tracks[2].Source.Apps);
    }

    [Fact]
    public void A_program_the_user_already_records_is_not_doubled()
    {
        var tracks = ClipstemsEngine.EffectiveTracks(WithAppTrack(enabled: true, "Discord.exe"),
            Steam("2", new SteamAudioProgram("discord.exe", "Discord")));

        Assert.Equal(["mic", "game", "voice"], tracks.Select(t => t.Id));
    }

    [Fact]
    public void Switching_off_the_users_track_for_a_program_stops_it_rather_than_handing_it_back_to_steam()
    {
        var tracks = ClipstemsEngine.EffectiveTracks(WithAppTrack(enabled: false, "Discord.exe"),
            Steam("2", new SteamAudioProgram("discord.exe", "Discord")));

        Assert.Equal(["mic", "game"], tracks.Select(t => t.Id));
    }

    [Fact]
    public void A_first_running_of_several_apps_track_does_not_claim_a_program_it_may_not_record()
    {
        var tracks = ClipstemsEngine.EffectiveTracks(WithAppTrack(enabled: true, [.. CaptureSource.VoiceChatApps]),
            Steam("2", new SteamAudioProgram("discord.exe", "Discord")));

        Assert.Equal(["mic", "game", "voice", "steam-discord"], tracks.Select(t => t.Id));
    }

    [Theory]
    [InlineData("0", false)] // Game Audio Only: observed
    [InlineData("1", true)] // All System Audio: inferred
    [InlineData("2", true)] // Game and Selected Programs, Discord ticked
    [InlineData(null, null)]
    public void Whether_steams_track_holds_an_app_follows_the_audio_option(string? option, bool? expected)
    {
        var steam = Steam("2", new SteamAudioProgram("Discord.exe", "Discord")) with { AudioOption = option };
        Assert.Equal(expected, steam.RecordsApp(["discord.exe"]));
    }

    [Fact]
    public void Steams_mic_setting_is_on_off_or_unknown_never_assumed_from_nothing()
    {
        Assert.True((Steam("2") with { MicInMix = "1" }).MicRecorded);
        Assert.False((Steam("2") with { MicInMix = null }).MicRecorded); // block read, key absent: Steam's default
        Assert.Null(SteamRecordingSettings.Empty.MicRecorded); // Steam's settings not read
    }

    [Fact]
    public void An_app_not_ticked_is_not_in_steams_track_under_selected_programs() =>
        Assert.False(Steam("2", new SteamAudioProgram("Discord.exe", "Discord")).RecordsApp(["Spotify.exe"]));

    private static ClipstemsSettings WithAppTrack(bool enabled, params string[] apps) => new()
    {
        Tracks = [.. ClipstemsSettings.DefaultTracks,
            new TrackSettings("voice", "Voice chat", enabled, new CaptureSource(SourceKind.Application) { Apps = apps })],
    };

    [Fact]
    public void Mirroring_off_records_only_the_users_tracks()
    {
        var tracks = ClipstemsEngine.EffectiveTracks(new ClipstemsSettings { MirrorSteamPrograms = false },
            Steam("2", new SteamAudioProgram("Discord.exe", "Discord")));
        Assert.Equal(["mic", "game"], tracks.Select(t => t.Id));
    }
}
