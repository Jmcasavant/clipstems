using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Clipstems.Core.Media;

/// <summary>Start time of each stream in a media input, in seconds.</summary>
public sealed record StreamTimes(double? VideoStart, double? AudioStart)
{
    /// <summary>
    /// How far the audio starts after the input's first stream. ffmpeg shifts
    /// every input so its first stream starts at zero, so this is where the
    /// audio lands in the output.
    /// </summary>
    public double AudioLead => AudioStart is { } a ? a - Math.Min(a, VideoStart ?? a) : 0;
}

/// <summary>Runs the ffmpeg and ffprobe executables. Clipstems does not ship its own codecs.</summary>
public sealed class Ffmpeg(string ffmpegPath, string ffprobePath)
{
    public string FfmpegPath { get; } = ffmpegPath;
    public string FfprobePath { get; } = ffprobePath;

    /// <summary>
    /// <c>CLIPSTEMS_FFMPEG</c> (path to ffmpeg.exe) if set, else ffmpeg on PATH,
    /// with ffprobe beside it. Null when either is missing.
    /// </summary>
    public static Ffmpeg? Locate()
    {
        var configured = Environment.GetEnvironmentVariable("CLIPSTEMS_FFMPEG");
        var ffmpeg = !string.IsNullOrEmpty(configured) && File.Exists(configured) ? configured : FindOnPath("ffmpeg.exe");
        if (ffmpeg is null) return null;
        var probe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        if (!File.Exists(probe)) probe = FindOnPath("ffprobe.exe") ?? "";
        return File.Exists(probe) ? new Ffmpeg(ffmpeg, probe) : null;
    }

    /// <summary>Decodes the first audio stream to 16-bit mono at <paramref name="sampleRate"/>.</summary>
    public async Task<byte[]> DecodeAudioMonoAsync(string input, TimeSpan maxDuration, int sampleRate, CancellationToken cancel)
    {
        using var output = new MemoryStream();
        await RunAsync(FfmpegPath, DefaultLimit + maxDuration,
        [
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-i", input,
            "-map", "0:a:0", "-t", Seconds(maxDuration.TotalSeconds),
            "-ac", "1", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
            "-f", "s16le", "-",
        ], output, cancel);
        return output.ToArray();
    }

    /// <summary>Decodes the first audio stream to interleaved 16-bit stereo at 48 kHz, the buffer's format.</summary>
    public async Task<byte[]> DecodePcmAsync(string input, CancellationToken cancel)
    {
        using var output = new MemoryStream();
        await RunAsync(FfmpegPath, DefaultLimit,
        [
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-i", input, "-map", "0:a:0", "-ac", "2", "-ar", "48000", "-f", "s16le", "-",
        ], output, cancel);
        return output.ToArray();
    }

    public async Task<StreamTimes> ProbeStreamTimesAsync(string input, CancellationToken cancel)
    {
        using var output = new MemoryStream();
        await RunAsync(FfprobePath, DefaultLimit,
        [
            "-hide_banner", "-loglevel", "error",
            "-show_entries", "stream=codec_type,start_time", "-of", "json", input,
        ], output, cancel);
        JsonDocument json;
        try { json = JsonDocument.Parse(output.ToArray()); }
        catch (JsonException ex) { throw new FfmpegException($"ffprobe printed unreadable output: {ex.Message}"); }
        using var _ = json;
        double? video = null, audio = null;
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            return new StreamTimes(null, null);
        foreach (var stream in streams.EnumerateArray())
        {
            var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
            double? start = stream.TryGetProperty("start_time", out var s) &&
                            double.TryParse(s.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            if (type == "video") video ??= start;
            else if (type == "audio") audio ??= start;
        }
        return new StreamTimes(video, audio);
    }

    /// <summary>Runs ffmpeg with <paramref name="args"/>; throws with the tail of stderr on failure.</summary>
    public Task RunFfmpegAsync(IReadOnlyList<string> args, CancellationToken cancel, TimeSpan? timeLimit = null) =>
        RunAsync(FfmpegPath, timeLimit ?? DefaultLimit, ["-hide_banner", "-nostdin", "-loglevel", "error", .. args], Stream.Null, cancel);

    /// <summary>
    /// Longest a probe or decode may take. Clips attach one at a time, so an
    /// ffmpeg that hangs (a segment Steam holds open) must not block every later clip.
    /// </summary>
    private static readonly TimeSpan DefaultLimit = TimeSpan.FromMinutes(2);

    public static string Seconds(double seconds) => seconds.ToString("0.######", CultureInfo.InvariantCulture);

    private static async Task RunAsync(string exe, TimeSpan timeLimit, IReadOnlyList<string> args, Stream stdout, CancellationToken cancel)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeLimit);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}.");
        var stderr = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(stdout, limit.Token);
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new FfmpegException($"{Path.GetFileName(exe)} did not finish within {timeLimit.TotalMinutes:0.#} minutes and was stopped.");
        }
        finally
        {
            // Whatever ended the wait (cancel, time limit, a failed pipe), never leave ffmpeg running.
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        if (process.ExitCode != 0)
        {
            var err = (await stderr).Trim();
            if (err.Length > 1500) err = "…" + err[^1500..];
            throw new FfmpegException($"{Path.GetFileName(exe)} exited with code {process.ExitCode}: {err}");
        }
    }

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(dir => Path.Combine(dir, exe))
        .FirstOrDefault(File.Exists);
}

public sealed class FfmpegException(string message) : Exception(message);
