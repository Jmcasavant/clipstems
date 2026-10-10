using Clipstems.Core;
using Clipstems.Core.Capture;

namespace Clipstems.Tests;

public class CliShapeTests
{
    [Theory]
    [InlineData("mic", SourceKind.Microphone, null, "")]
    [InlineData("mic:fifine", SourceKind.Microphone, "fifine", "")]
    [InlineData("system", SourceKind.SystemOutput, null, "")]
    [InlineData("app:Discord", SourceKind.Application, null, "Discord.exe")]
    [InlineData("APP:spotify.exe", SourceKind.Application, null, "spotify.exe")]
    [InlineData("app:Discord|DiscordPTB.exe", SourceKind.Application, null, "Discord.exe,DiscordPTB.exe")]
    [InlineData("game", SourceKind.SteamGame, null, "")]
    [InlineData("all", SourceKind.AllExceptSelf, null, "")]
    public void Source_specs_parse(string spec, SourceKind kind, string? device, string apps)
    {
        var source = CaptureSource.Parse(spec);
        Assert.Equal(kind, source.Kind);
        Assert.Equal(device, source.Device);
        Assert.Equal(apps, string.Join(',', source.Apps));
    }

    [Fact]
    public void Only_default_device_sources_follow_device_changes()
    {
        Assert.True(CaptureSource.Parse("mic").FollowsDefaultDevice);
        Assert.False(CaptureSource.Parse("mic:fifine").FollowsDefaultDevice);
        Assert.False(CaptureSource.Parse("all").FollowsDefaultDevice);
    }

    [Theory]
    [InlineData("app:")]
    [InlineData("speaker")]
    [InlineData("game:cs2")]
    public void Bad_source_specs_are_refused(string spec) =>
        Assert.Throws<FormatException>(() => CaptureSource.Parse(spec));

    [Fact]
    public void Mux_keeps_steam_streams_first_and_default_and_offsets_extra_tracks()
    {
        var args = ClipAttacher.MuxArguments("in.mpd",
            [(new ExportTrack("mic", "Mic"), "0-mic.wav"), (new ExportTrack("discord", "Discord"), "1-discord.wav")],
            audioLead: 0.021, bitrateKbps: 160, comment: "c", output: "out.mp4");
        var line = string.Join(' ', args);

        Assert.StartsWith("-i in.mpd -itsoffset 0.021 -i 0-mic.wav -itsoffset 0.021 -i 1-discord.wav", line);
        Assert.Contains("-map 0:v:0 -map 0:a:0 -map 1:a:0 -map 2:a:0", line);
        Assert.Contains("-c:v copy -c:a:0 copy", line);
        Assert.Contains("-disposition:a:0 default", line);
        Assert.Contains("-metadata:s:a:2 title=Discord", line);
        Assert.Contains("-disposition:a:2 0", line);
        Assert.EndsWith("-y out.mp4", line);
    }

    [Fact]
    public void File_names_lose_characters_windows_rejects()
    {
        Assert.Equal("WARDOGS - 2026-10-10 1-45-47 AM", ClipAttacher.SafeFileName("WARDOGS - 2026-10-10 1:45:47 AM"));
    }
}
