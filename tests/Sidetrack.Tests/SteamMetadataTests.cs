using System.Xml.Linq;
using Sidetrack.Core;
using Sidetrack.Core.Steam;

namespace Sidetrack.Tests;

public class SteamMetadataTests
{
    [Fact]
    public void ClipPb_yields_timing_and_segment()
    {
        using var dir = new TempDir();
        var clip = ClipInfo.Read(TestData.ClipFolder(dir, TestData.ClipPb(), TestData.Manifest()));

        Assert.Equal(1867240u, clip.AppId);
        Assert.Equal("WARDOGS - 2026-10-10 1:45:47 AM", clip.Title);
        Assert.Equal(TestData.DurationMs, clip.DurationMs);
        var segment = Assert.Single(clip.Segments);
        Assert.Equal(TestData.BgFolder, segment.BgFolder);
        Assert.Equal(TestData.BgOffsetMs, segment.BgOffsetMs);
        Assert.Equal(new DateTime(2026, 10, 10, 5, 44, 1, 179, DateTimeKind.Utc), clip.CoarseStartUtc);
    }

    [Fact]
    public void Truncated_clip_pb_is_a_format_error_not_zero_timing()
    {
        using var dir = new TempDir();
        var bytes = TestData.ClipPb();
        var folder = TestData.ClipFolder(dir, bytes[..(bytes.Length / 2)], TestData.Manifest());
        Assert.Throws<FormatException>(() => ClipInfo.Read(folder));
    }

    [Fact]
    public void Manifest_reads_period_and_duration_and_tolerates_trailing_bytes()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "session.mpd");
        File.WriteAllText(path, TestData.Manifest(trailer: "\0\0garbage"));

        var mpd = DashManifest.Load(path);

        Assert.Equal(new TimeSpan(0, 2, 52, 56, 850), mpd.PeriodStart);
        Assert.Equal(TimeSpan.FromSeconds(22.17), mpd.PresentationDuration);
        Assert.Equal(1, mpd.AudioRepresentations);
    }

    [Fact]
    public void Ffmpeg_copy_starts_at_zero_and_points_back_without_touching_the_original()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "session.mpd");
        var original = TestData.Manifest();
        File.WriteAllText(path, original);
        var copyPath = Path.Combine(dir.Path, "copy.mpd");

        DashManifest.Load(path).WriteFfmpegCopy(copyPath);

        XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
        var copy = XDocument.Load(copyPath).Root!;
        Assert.Equal("PT0S", copy.Element(ns + "Period")!.Attribute("start")!.Value);
        Assert.Equal(dir.Path.Replace('\\', '/') + "/", copy.Element(ns + "BaseURL")!.Value);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void Ffmpeg_copy_declares_every_segment_on_disk_not_just_the_logical_clip()
    {
        // A 22.17 s clip whose media starts at an earlier segment boundary
        // carries 9 × 3 s of audio; ffmpeg stops at the declared duration.
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "session.mpd");
        File.WriteAllText(path, TestData.Manifest());
        for (int i = 1; i <= 9; i++) File.WriteAllBytes(Path.Combine(dir.Path, $"chunk-stream1-{i:00000}.m4s"), []);
        var mpd = DashManifest.Load(path);
        var copyPath = Path.Combine(dir.Path, "copy.mpd");

        mpd.WriteFfmpegCopy(copyPath);

        Assert.Equal(TimeSpan.FromSeconds(27), mpd.AvailableDuration);
        Assert.Equal("PT27S", XDocument.Load(copyPath).Root!.Attribute("mediaPresentationDuration")!.Value);
    }

    [Fact]
    public void Validate_accepts_consistent_clip()
    {
        using var dir = new TempDir();
        var clip = ClipInfo.Read(TestData.ClipFolder(dir, TestData.ClipPb(), TestData.Manifest()));
        var (manifest, refusal) = ClipAttacher.Validate(clip);
        Assert.Null(refusal);
        Assert.NotNull(manifest);
    }

    [Fact]
    public void Validate_refuses_when_clip_pb_and_manifest_disagree()
    {
        using var dir = new TempDir();
        // Offset moved by one second: the manifest still says PT2H52M56.85S.
        var clip = ClipInfo.Read(TestData.ClipFolder(dir, TestData.ClipPb(clipOffsetMs: TestData.ClipOffsetMs + 1000), TestData.Manifest()));
        var (_, refusal) = ClipAttacher.Validate(clip);
        Assert.Contains("disagrees", refusal);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public void Validate_refuses_multi_segment_or_multi_timeline_clips(int segments, int timelines)
    {
        using var dir = new TempDir();
        var clip = ClipInfo.Read(TestData.ClipFolder(dir, TestData.ClipPb(segments: segments, timelines: timelines), TestData.Manifest()));
        var (_, refusal) = ClipAttacher.Validate(clip);
        Assert.Contains("handles exactly one", refusal);
    }

    [Fact]
    public void Vdf_values_unescape_windows_paths()
    {
        var values = SteamInstall.ReadVdfValues("""
            "system"
            {
                "BackgroundRecordPath"		"C:\\Users\\someone\\Videos"
                "Audio_Mic"		"1"
                "Audio_Mic"		"0"
            }
            """);
        Assert.Equal(@"C:\Users\someone\Videos", values["BackgroundRecordPath"]);
        Assert.Equal("1", values["Audio_Mic"]);
    }

    [Fact]
    public void Recording_roots_list_configured_path_then_default_without_duplicates()
    {
        using var dir = new TempDir();
        var userData = Path.Combine(dir.Path, "userdata", "42");
        var fallback = Path.Combine(userData, "gamerecordings");
        var custom = Path.Combine(dir.Path, "Videos");
        Directory.CreateDirectory(fallback);
        Directory.CreateDirectory(custom);

        var user = new SteamUser("42", userData, new SteamRecordingSettings(custom, "1", "2", "0"));
        Assert.Equal([custom, fallback], user.RecordingRoots());

        var same = new SteamUser("42", userData, new SteamRecordingSettings(fallback, "1", "2", "0"));
        Assert.Equal([fallback], same.RecordingRoots());
    }
}
