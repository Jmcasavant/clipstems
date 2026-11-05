using System.Xml.Linq;
using Clipstems.Core;
using Clipstems.Core.Audio;
using Clipstems.Core.Buffer;
using Clipstems.Core.Engine;
using Clipstems.Core.Settings;
using Clipstems.Core.Steam;
using Clipstems.Core.Sync;

namespace Clipstems.Tests;

/// <summary>Inputs and failures that must degrade to a clear error, never a crash or lost data.</summary>
public class HardeningTests
{
    private static readonly long T0 = new DateTime(2026, 10, 10, 21, 46, 0, DateTimeKind.Utc).Ticks;

    [Theory]
    [InlineData(new byte[] { 0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F })]       // field 1, length 2^32 - 1
    [InlineData(new byte[] { 0x0A, 0xFE, 0xFF, 0xFF, 0xFF, 0x07, 0x00 })] // length int.MaxValue - 1: i + len overflows int
    [InlineData(new byte[] { 0x0B })]                                     // wire type 3 (group), unsupported
    [InlineData(new byte[] { 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 })] // 11-byte varint
    public void Malformed_protobuf_is_a_format_error(byte[] message)
    {
        Assert.Throws<FormatException>(() => ProtoReader.Read(message));
    }

    [Fact]
    public void Unparseable_manifest_is_a_format_error()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "session.mpd");
        File.WriteAllText(path, "<MPD><Period></MPD>");
        Assert.Throws<FormatException>(() => DashManifest.Load(path));
    }

    [Theory]
    [InlineData("""{ "version": 1, "tracks": null }""")]
    [InlineData("""{ "version": 1, "tracks": [ null ] }""")]
    [InlineData("""{ "version": 1, "tracks": [ { "id": "mic", "title": "Mic", "enabled": true } ] }""")]
    [InlineData("""{ "version": 1, "tracks": [ { "id": "x", "title": "X", "enabled": true, "source": { "kind": "application", "apps": [ null ] } } ] }""")]
    public void Settings_with_nulls_are_set_aside_not_a_crash(string json)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, json);

        var settings = new SettingsStore(path).Load(out var problem);

        Assert.NotNull(problem);
        Assert.Equal(ClipstemsSettings.DefaultTracks, settings.Tracks);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(dir.Path, "settings.json.bad-*"));
    }

    [Fact]
    public void Settings_from_a_newer_version_are_never_overwritten()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        const string newer = """{ "version": 99, "futureOption": true }""";
        File.WriteAllText(path, newer);
        var store = new SettingsStore(path);

        store.Load(out var problem);
        Assert.NotNull(problem);
        Assert.Throws<IOException>(() => store.Save(new ClipstemsSettings()));
        Assert.Equal(newer, File.ReadAllText(path));
    }

    [Fact]
    public void Saving_settings_leaves_no_temporary_file()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(Path.Combine(dir.Path, "settings.json"));
        store.Save(new ClipstemsSettings());
        store.Save(new ClipstemsSettings { RetentionMinutes = 30 });

        Assert.Equal(["settings.json"], Directory.GetFiles(dir.Path).Select(Path.GetFileName));
        Assert.Equal(30, store.Load(out _).RetentionMinutes);
    }

    [Fact]
    public void Clock_set_back_starts_a_new_segment_instead_of_dropping_audio()
    {
        using var dir = new TempDir();
        var store = new SegmentStore(dir.Path);
        var halfSecond = new byte[24_000 * PcmFormat.BlockAlign];
        Array.Fill(halfSecond, (byte)1);
        using (var writer = new SegmentWriter(store, "mic"))
        {
            writer.Write(halfSecond, T0, silent: false);
            // The next packet arrives 10 s "earlier": the wall clock was stepped back.
            writer.Write(halfSecond, T0 - TimeSpan.FromSeconds(9.5).Ticks, silent: false);
            Assert.Equal(48_000, writer.TotalFrames);
        }

        var segments = store.List("mic");
        Assert.Equal(2, segments.Count);
        Assert.All(segments, s => Assert.Equal(24_000, s.Frames));
    }

    [Fact]
    public void Prune_removes_old_empty_segments_a_crash_left_behind()
    {
        using var dir = new TempDir();
        var store = new SegmentStore(dir.Path);
        var track = store.TrackDirectory("mic");
        Directory.CreateDirectory(track);
        var oldEmpty = Path.Combine(track, SegmentStore.SegmentFileName(T0));
        var newEmpty = Path.Combine(track, SegmentStore.SegmentFileName(T0 + TimeSpan.FromHours(3).Ticks));
        File.WriteAllBytes(oldEmpty, new byte[44]);
        File.WriteAllBytes(newEmpty, []);

        store.Prune(T0 + TimeSpan.FromHours(1).Ticks);

        Assert.False(File.Exists(oldEmpty));
        Assert.True(File.Exists(newEmpty));
    }

    [Fact]
    public void Finished_clips_never_overwrite_one_with_the_same_name()
    {
        using var dir = new TempDir();
        var target = Path.Combine(dir.Path, "WARDOGS.mp4");
        File.WriteAllText(target, "first");
        var second = Path.Combine(dir.Path, "muxed.mp4");
        File.WriteAllText(second, "second");

        var written = ClipAttacher.MoveToUniquePath(second, target);

        Assert.Equal(Path.Combine(dir.Path, "WARDOGS (2).mp4"), written);
        Assert.Equal("first", File.ReadAllText(target));
        Assert.Equal("second", File.ReadAllText(written));
    }

    [Fact]
    public void A_clip_queued_when_clipstems_quit_is_retried_on_the_next_start()
    {
        using var dir = new TempDir();
        var root = Path.Combine(dir.Path, "recordings");
        var log = Path.Combine(dir.Path, "processed.log");
        string Clip(string name)
        {
            var clip = Path.Combine(root, "clips", name);
            Directory.CreateDirectory(clip);
            File.WriteAllBytes(Path.Combine(clip, "clip.pb"), TestData.ClipPb());
            return clip;
        }

        var before = Clip("clip_1_20261010_010000");
        var first = new ClipWatcher([root], log);
        var queued = Clip("clip_1_20261010_020000");
        first.MarkQueued(queued); // then Clipstems quits before attaching it

        var second = new ClipWatcher([root], log);
        Assert.Equal([queued], second.TakeResumed());
        Assert.Empty(second.TakeResumed());
        Assert.DoesNotContain(before, second.ReadyClips());
        second.MarkProcessed(queued);

        var third = new ClipWatcher([root], log);
        Assert.Empty(third.TakeResumed());
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4000, false)]
    [InlineData(7999, true)]
    public void A_peak_at_either_end_of_the_search_range_is_flagged(int offset, bool atEdge)
    {
        var rng = new Random(7);
        var reference = new float[8000 + 4000];
        for (int i = 0; i < reference.Length; i++) reference[i] = (float)(rng.NextDouble() * 2 - 1);
        var probe = reference.AsSpan(offset, 4000).ToArray();

        var result = Correlator.Locate(reference, probe, exclusionSamples: 80);

        Assert.NotNull(result);
        Assert.Equal(offset, result.LagSamples, precision: 0);
        Assert.Equal(atEdge, result.AtEdge);
    }

    [Fact]
    public void Mux_time_limit_grows_with_the_clip()
    {
        Assert.True(ClipAttacher.MuxTimeLimit(TimeSpan.FromMinutes(10)) > ClipAttacher.MuxTimeLimit(TimeSpan.FromSeconds(20)));
        Assert.True(ClipAttacher.MuxTimeLimit(TimeSpan.Zero) >= TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Vdf_conditional_between_a_key_and_its_block_is_skipped()
    {
        var root = Vdf.Parse("""
            "a"
            {
                "win" [$WIN32]
                {
                    "k" "v"
                }
                "after" "1"
            }
            """);

        Assert.Equal("v", root.Find("win")!["k"]);
        Assert.Equal("1", root.Child("a")!["after"]);
    }

    [Theory]
    [InlineData("chunk-stream$RepresentationID$-$Number%05d$.m4s", "1", "chunk-stream1-*.m4s")]
    [InlineData("seg_$RepresentationID$_$Number$.m4s", "audio", "seg_audio_*.m4s")]
    [InlineData("$RepresentationID$/$Number$.m4s", "1", null)]   // segments in a sub-folder: unknown layout
    [InlineData("chunk-$Bandwidth$-$Number$.m4s", "1", null)]    // an identifier this reader doesn't fill in
    [InlineData(null, "1", "chunk-stream1-*.m4s")]                // no media attribute: Steam's naming
    public void Segment_file_pattern_follows_the_manifest_template(string? media, string id, string? expected)
    {
        Assert.Equal(expected, DashManifest.ChunkPattern(media, id));
    }

    [Fact]
    public void A_segment_template_on_the_adaptation_set_is_used()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "session.mpd");
        // Move the audio template up from the Representation to its AdaptationSet.
        var manifest = TestData.Manifest();
        int audioSet = manifest.IndexOf("contentType=\"audio\"", StringComparison.Ordinal);
        int start = manifest.IndexOf("<SegmentTemplate", audioSet, StringComparison.Ordinal);
        int end = manifest.IndexOf("/>", start, StringComparison.Ordinal) + 2;
        var template = manifest[start..end];
        manifest = manifest.Remove(start, end - start);
        int setOpen = manifest.IndexOf('>', audioSet) + 1;
        manifest = manifest.Insert(setOpen, template);
        File.WriteAllText(path, manifest);
        for (int i = 1; i <= 9; i++) File.WriteAllBytes(Path.Combine(dir.Path, $"chunk-stream1-{i:00000}.m4s"), []);

        Assert.Equal(TimeSpan.FromSeconds(27), DashManifest.Load(path).AvailableDuration);
    }

    [Fact]
    public void Segments_in_a_folder_with_a_hash_are_copied_for_ffmpeg()
    {
        // ffmpeg's DASH demuxer reads '#' in a BaseURL as a fragment and loses the rest of the path.
        using var dir = new TempDir();
        var clipDir = Path.Combine(dir.Path, "Clips #1", "bg");
        Directory.CreateDirectory(clipDir);
        var path = Path.Combine(clipDir, "session.mpd");
        File.WriteAllText(path, TestData.Manifest());
        File.WriteAllBytes(Path.Combine(clipDir, "init-stream1.m4s"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(clipDir, "chunk-stream1-00001.m4s"), [4, 5]);
        var temp = Path.Combine(dir.Path, "temp");
        Directory.CreateDirectory(temp);

        DashManifest.Load(path).WriteFfmpegCopy(Path.Combine(temp, "session.mpd"));

        XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
        var baseUrl = XDocument.Load(Path.Combine(temp, "session.mpd")).Root!.Element(ns + "BaseURL")!.Value;
        Assert.DoesNotContain("#", baseUrl);
        Assert.Equal(Path.Combine(temp, "segments").Replace('\\', '/') + "/", baseUrl);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(temp, "segments", "init-stream1.m4s")));
        Assert.True(File.Exists(Path.Combine(temp, "segments", "chunk-stream1-00001.m4s")));
    }

    [Fact]
    public void A_bad_timestamp_after_a_rollover_continues_where_the_last_segment_ended()
    {
        using var dir = new TempDir();
        var store = new SegmentStore(dir.Path);
        var oneSecond = new byte[48_000 * PcmFormat.BlockAlign];
        long start = DateTime.UtcNow.Ticks - TimeSpan.TicksPerSecond;
        using (var writer = new SegmentWriter(store, "mic", segmentLength: TimeSpan.FromSeconds(1)))
        {
            writer.Write(oneSecond, start, silent: false);                        // fills and closes segment 1
            writer.Write(oneSecond, 0, silent: false, timestampValid: false);    // Windows flagged this packet's time
        }

        var segments = store.List("mic");
        Assert.Equal(2, segments.Count);
        Assert.Equal(segments[0].EndTicks, segments[1].StartTicks);
    }
}
