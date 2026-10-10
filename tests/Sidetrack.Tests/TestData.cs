using System.Text;

namespace Sidetrack.Tests;

/// <summary>Builds Steam-shaped fixtures: a protobuf encoder and a clip folder on disk.</summary>
internal static class TestData
{
    public const long TimelineStartUnix = 1791600613;
    public const long ClipOffsetMs = 10428179;
    public const long BgOffsetMs = 51329;
    public const long DurationMs = 22170;
    public const string BgFolder = "bg_1867240_20261010_025104";

    /// <summary>The structure of a clip.pb observed 2026-10-10, with the observed values.</summary>
    public static byte[] ClipPb(long clipOffsetMs = ClipOffsetMs, int segments = 1, int timelines = 1)
    {
        var timeline = new Proto()
            .Str(1, "timeline_186724020261010_025013")
            .Varint(2, 1867240)
            .Varint(3, TimelineStartUnix)
            .Varint(4, DurationMs);
        for (int i = 0; i < segments; i++)
            timeline.Msg(5, new Proto().Str(1, BgFolder).Varint(2, 0).Varint(3, DurationMs).Varint(4, 4).Varint(10, BgOffsetMs));
        var root = new Proto();
        for (int i = 0; i < timelines; i++) root.Msg(1, timeline);
        return root
            .Varint(2, clipOffsetMs)
            .Varint(3, 1791611041)
            .Varint(4, 1867240)
            .Varint(6, 75112311)
            .Str(7, "WARDOGS - 2026-10-10 1:45:47 AM")
            .Varint(8, 0)
            .ToArray();
    }

    public static string Manifest(string periodStart = "PT2H52M56.85S", string trailer = "") => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <MPD xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns="urn:mpeg:dash:schema:mpd:2011" profiles="urn:mpeg:dash:profile:isoff-live:2011" type="static" maxSegmentDuration="PT3.0S" minBufferTime="PT6.0S" mediaPresentationDuration="PT22.17S">
            <Period id="0" start="{periodStart}">
                <AdaptationSet id="0" contentType="video" startWithSAP="1" segmentAlignment="true" bitstreamSwitching="true" maxWidth="2560" maxHeight="1440">
                    <Representation id="0" mimeType="video/mp4" codecs="hev1.2.4.L123.B0" bandwidth="22000000" width="2560" height="1440">
                        <SegmentTemplate timescale="1000000" duration="3000000" initialization="init-stream$RepresentationID$.m4s" media="chunk-stream$RepresentationID$-$Number%05d$.m4s" startNumber="1"/>
                    </Representation>
                </AdaptationSet>
                <AdaptationSet id="1" contentType="audio" startWithSAP="1" segmentAlignment="true" bitstreamSwitching="true">
                    <Representation id="1" mimeType="audio/mp4" codecs="mp4a.40.2" bandwidth="128000" audioSamplingRate="48000">
                        <SegmentTemplate timescale="1000000" duration="3000000" initialization="init-stream$RepresentationID$.m4s" media="chunk-stream$RepresentationID$-$Number%05d$.m4s" startNumber="1"/>
                    </Representation>
                </AdaptationSet>
            </Period>
        </MPD>{trailer}
        """;

    /// <summary>Writes clip.pb and video/&lt;bg&gt;/session.mpd into a fresh temp folder.</summary>
    public static string ClipFolder(TempDir dir, byte[] clipPb, string manifest)
    {
        var clip = Path.Combine(dir.Path, "clip_1867240_20261010_054548");
        var seg = Path.Combine(clip, "video", BgFolder);
        Directory.CreateDirectory(seg);
        File.WriteAllBytes(Path.Combine(clip, "clip.pb"), clipPb);
        File.WriteAllText(Path.Combine(seg, "session.mpd"), manifest);
        return clip;
    }

    internal sealed class Proto
    {
        private readonly MemoryStream _buffer = new();

        public Proto Varint(int field, long value)
        {
            WriteVarint((ulong)(field << 3));
            WriteVarint((ulong)value);
            return this;
        }

        public Proto Str(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

        public Proto Msg(int field, Proto message) => Bytes(field, message.ToArray());

        public byte[] ToArray() => _buffer.ToArray();

        private Proto Bytes(int field, byte[] bytes)
        {
            WriteVarint((ulong)(field << 3 | 2));
            WriteVarint((ulong)bytes.Length);
            _buffer.Write(bytes);
            return this;
        }

        private void WriteVarint(ulong v)
        {
            while (v >= 0x80)
            {
                _buffer.WriteByte((byte)(v | 0x80));
                v >>= 7;
            }
            _buffer.WriteByte((byte)v);
        }
    }
}

internal sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sidetrack-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}
