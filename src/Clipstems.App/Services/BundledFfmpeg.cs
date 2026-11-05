using System.IO;
using System.Text.Json;
using Clipstems.Core.Media;

namespace Clipstems.App.Services;

/// <summary>
/// What the installer says about the FFmpeg it ships: build/ffmpeg.json,
/// copied into the ffmpeg folder at packaging time, so About can name the
/// version and license and link to its source as the LGPL asks.
/// </summary>
public sealed record BundledFfmpeg(string Version, string License, string Source)
{
    private static string Folder => Path.Combine(AppContext.BaseDirectory, Ffmpeg.BundledFolderName);

    /// <summary>FFmpeg's license text as shipped, or null outside an installed copy.</summary>
    public static string? LicensePath => Path.Combine(Folder, "LICENSE.txt") is var p && File.Exists(p) ? p : null;

    public static BundledFfmpeg? Read()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "ffmpeg.json")));
            var root = doc.RootElement;
            string? Get(string name) => root.TryGetProperty(name, out var v) ? v.GetString() : null;
            return Get("version") is { } version && Get("license") is { } license && Get("source") is { } source
                ? new BundledFfmpeg(version, license, source)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
