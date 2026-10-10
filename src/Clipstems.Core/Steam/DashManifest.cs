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
                        a.Elements(Ns + "Representation").Any(r => ((string?)r.Attribute("mimeType"))?.StartsWith("audio/") == true))
            .SelectMany(a => a.Elements(Ns + "Representation"))
            .ToList();
        AudioRepresentations = audio.Count;
        if (audio.Count == 1 && audio[0].Element(Ns + "SegmentTemplate") is { } template &&
            long.TryParse((string?)template.Attribute("duration"), out var segDuration) &&
            long.TryParse((string?)template.Attribute("timescale"), out var timescale) && timescale > 0)
        {
            SegmentDuration = TimeSpan.FromSeconds((double)segDuration / timescale);
            _audioRepresentationId = (string?)audio[0].Attribute("id");
        }
    }

    private readonly string? _audioRepresentationId;

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
            if (SegmentDuration <= TimeSpan.Zero || _audioRepresentationId is null) return PresentationDuration;
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
            int chunks = Directory.GetFiles(dir, $"chunk-stream{_audioRepresentationId}-*.m4s").Length;
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
        return new DashManifest(path, XDocument.Parse(text));
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
        mpd.AddFirst(new XElement(Ns + "BaseURL", segmentDir.Replace('\\', '/').TrimEnd('/') + "/"));
        copy.Save(outputPath);
    }

    private static TimeSpan Duration(string? value) =>
        string.IsNullOrEmpty(value) ? TimeSpan.Zero : XmlConvert.ToTimeSpan(value);
}
