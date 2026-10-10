using Sidetrack.Core.Capture;
using Sidetrack.Core.Engine;
using Sidetrack.Core.Settings;
using Sidetrack.Core.Steam;

namespace Sidetrack.Tests;

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
        var tracks = SidetrackEngine.EffectiveTracks(new SidetrackSettings(),
            Steam("2", new SteamAudioProgram("Discord.exe", "Discord"), new SteamAudioProgram("Spotify.exe", "Spotify")));

        Assert.Equal(["mic", "game", "steam-discord", "steam-spotify"], tracks.Select(t => t.Id));
        Assert.Equal("Discord", tracks[2].Title);
        Assert.Equal(["Discord.exe"], tracks[2].Source.Apps);
    }

    [Fact]
    public void A_program_the_user_already_records_is_not_doubled()
    {
        var settings = new SidetrackSettings
        {
            Tracks = [.. SidetrackSettings.DefaultTracks,
                new TrackSettings("voice", "Voice chat", true, new CaptureSource(SourceKind.Application) { Apps = CaptureSource.VoiceChatApps })],
        };

        var tracks = SidetrackEngine.EffectiveTracks(settings, Steam("2", new SteamAudioProgram("discord.exe", "Discord")));

        Assert.Equal(["mic", "game", "voice"], tracks.Select(t => t.Id));
    }

    [Fact]
    public void Mirroring_off_records_only_the_users_tracks()
    {
        var tracks = SidetrackEngine.EffectiveTracks(new SidetrackSettings { MirrorSteamPrograms = false },
            Steam("2", new SteamAudioProgram("Discord.exe", "Discord")));
        Assert.Equal(["mic", "game"], tracks.Select(t => t.Id));
    }
}
