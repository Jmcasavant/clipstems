using System.Text;
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
}
