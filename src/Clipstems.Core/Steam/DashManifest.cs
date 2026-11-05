using System.Xml;
using System.Xml.Linq;

namespace Clipstems.Core.Steam;

/// <summary>
/// A Steam recording's <c>session.mpd</c>: one video and one audio
/// AdaptationSet over 3 s fragmented-MP4 segments.
/// </summary>
public sealed class DashManifest
{
    private static readonly XNamespace Ns = "urn:mpeg:dash:schema:mpd:2011";
    private readonly XDocument _doc;

    private DashManifest(string path, XDocument doc)
    {
        Path = path;
        _doc = doc;
        var mpd = doc.Root ?? throw new FormatException($"{path}: empty manifest.");
        var period = mpd.Element(Ns + "Period") ?? throw new FormatException($"{path}: no Period.");
        PeriodStart = Duration(period.Attribute("start")?.Value);
        PresentationDuration = Duration(mpd.Attribute("mediaPresentationDuration")?.Value);
        var audio = period.Elements(Ns + "AdaptationSet")
            .Where(a => (string?)a.Attribute("contentType") == "audio" ||
                        a.Elements(Ns + "Representation").Any(r => ((string?)r.Attribute("mimeType"))?.StartsWith("audio/", StringComparison.Ordinal) == true))
            .SelectMany(a => a.Elements(Ns + "Representation"))
            .ToList();
        AudioRepresentations = audio.Count;
        // DASH lets the template sit on the Representation or on its AdaptationSet.
        if (audio.Count == 1 &&
            (audio[0].Element(Ns + "SegmentTemplate") ?? audio[0].Parent?.Element(Ns + "SegmentTemplate")) is { } template &&
            long.TryParse((string?)template.Attribute("duration"), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var segDuration) &&
            long.TryParse((string?)template.Attribute("timescale"), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var timescale) && timescale > 0)
        {
            SegmentDuration = TimeSpan.FromSeconds((double)segDuration / timescale);
            _audioChunkPattern = ChunkPattern((string?)template.Attribute("media"), (string?)audio[0].Attribute("id"));
        }
    }

    /// <summary>A file pattern matching the audio's media segments on disk, or null when it can't be told.</summary>
    private readonly string? _audioChunkPattern;

    /// <summary>
    /// Turns a template such as <c>chunk-stream$RepresentationID$-$Number%05d$.m4s</c>
    /// into <c>chunk-stream1-*.m4s</c>. Steam's naming is the fallback when no media attribute is given.
    /// </summary>
    internal static string? ChunkPattern(string? media, string? representationId)
    {
        if (representationId is null) return null;
        media ??= "chunk-stream$RepresentationID$-$Number%05d$.m4s";
        var pattern = System.Text.RegularExpressions.Regex.Replace(media, @"\$Number(%[0-9]*d)?\$|\$Time(%[0-9]*d)?\$", "*")
            .Replace("$RepresentationID$", representationId, StringComparison.Ordinal)
            .Replace("$$", "$", StringComparison.Ordinal);
        // Anything still templated, or a path, is a layout this reader does not know.
        return pattern.Contains('$') || pattern.IndexOfAny(['/', '\\']) >= 0 || !pattern.Contains('*') ? null : pattern;
    }

    public string Path { get; }

    /// <summary>
    /// Where the clip's logical start sits on the recording's media timeline.
    /// Steam keeps whole segments, so the media itself starts up to one
    /// segment earlier than this.
    /// </summary>
    public TimeSpan PeriodStart { get; }

    public TimeSpan PresentationDuration { get; }
    public int AudioRepresentations { get; }
    public TimeSpan SegmentDuration { get; }

    /// <summary>
    /// Length of the audio segments actually on disk (segment count × segment
    /// duration). Longer than <see cref="PresentationDuration"/> when the clip
    /// carries lead-in footage before its logical start.
    /// </summary>
    public TimeSpan AvailableDuration
    {
        get
        {
            if (SegmentDuration <= TimeSpan.Zero || _audioChunkPattern is null) return PresentationDuration;
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
            int chunks = Directory.GetFiles(dir, _audioChunkPattern).Length;
            var available = SegmentDuration * chunks;
            return available > PresentationDuration ? available : PresentationDuration;
        }
    }

    public static DashManifest Load(string path)
    {
        var text = File.ReadAllText(path);
        // Some manifests carry bytes after </MPD>, which XML parsers reject.
        int end = text.LastIndexOf("</MPD>", StringComparison.Ordinal);
        if (end >= 0) text = text[..(end + "</MPD>".Length)];
        try
        {
            return new DashManifest(path, XDocument.Parse(text));
        }
        catch (System.Xml.XmlException ex)
        {
            // One exception type for "Steam's file is not what we expect", like the protobuf reader's.
            throw new FormatException($"{path} is not a readable DASH manifest: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Writes a copy ffmpeg reads in full: static, Period at zero, a duration
    /// covering every segment on disk (ffmpeg stops at the declared duration,
    /// counted from the first segment), and an absolute BaseURL back to the
    /// segment folder so the copy can live outside Steam's directory. Steam's
    /// own files are never modified.
    /// </summary>
    public void WriteFfmpegCopy(string outputPath)
    {
        var copy = new XDocument(_doc);
        var mpd = copy.Root!;
        mpd.SetAttributeValue("type", "static");
        mpd.SetAttributeValue("mediaPresentationDuration", XmlConvert.ToString(AvailableDuration));
        mpd.Element(Ns + "Period")!.SetAttributeValue("start", "PT0S");
        mpd.Elements(Ns + "BaseURL").Remove();
        var segmentDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
        if (segmentDir.Contains('#'))
            segmentDir = CopySegments(segmentDir, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath))!, "segments"));
        mpd.AddFirst(new XElement(Ns + "BaseURL", segmentDir.Replace('\\', '/').TrimEnd('/') + "/"));
        copy.Save(outputPath);
    }

    /// <summary>
    /// ffmpeg's DASH demuxer resolves segment names as URLs, so a '#' in the
    /// folder (e.g. "D:\Clips #1") starts a fragment and cuts the path short;
    /// neither %23 nor a file: URL helps (verified with ffmpeg 6.1). The clip's
    /// own segments are copied next to the manifest copy instead. Paths given
    /// to ffmpeg directly, not as URLs, are unaffected.
    /// </summary>
    private static string CopySegments(string from, string to)
    {
        if (to.Contains('#'))
            throw new IOException($"Clip folders whose path contains '#' can't be read by ffmpeg, and the temp folder {to} has one too.");
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*.m4s"))
            File.Copy(file, System.IO.Path.Combine(to, System.IO.Path.GetFileName(file)), overwrite: true);
        return to;
    }

    private static TimeSpan Duration(string? value) =>
        string.IsNullOrEmpty(value) ? TimeSpan.Zero : XmlConvert.ToTimeSpan(value);
}
