using System.Globalization;
using Sidetrack.Cli;
using Sidetrack.Core;
using Sidetrack.Core.Buffer;
using Sidetrack.Core.Capture;
using Sidetrack.Core.Media;
using Sidetrack.Core.Steam;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var cli = Args.Parse(args);
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

try
{
    return cli.Command switch
    {
        "doctor" => Commands.Doctor(),
        "devices" => Commands.Devices(),
        "clips" => Commands.Clips(),
        "record" => await Commands.Record(cli, watch: false, cancel.Token),
        "watch" => await Commands.Record(cli, watch: true, cancel.Token),
        "attach" => await Commands.Attach(cli, cancel.Token),
        "help" or "--help" or "-h" or null => Commands.Help(),
        _ => Commands.Unknown(cli.Command),
    };
}
catch (OperationCanceledException)
{
    return 130;
}
catch (Exception ex) when (ex is UsageException or FormatException or FfmpegException or InvalidOperationException)
{
    Console.Error.WriteLine($"sidetrack: {ex.Message}");
    return ex is UsageException ? 2 : 1;
}

namespace Sidetrack.Cli
{
    internal static class Commands
    {
        private const string ReferenceTrack = "system";

        private static readonly (string Name, string Spec)[] DefaultTracks =
        [
            ("mic", "mic"),
            ("discord", "app:Discord.exe"),
            (ReferenceTrack, "system"),
        ];

        public static int Help()
        {
            Console.WriteLine("""
                sidetrack: separate audio tracks for Steam Game Recording clips

                  sidetrack doctor                 check Steam, recording roots, ffmpeg, buffer, capture support
                  sidetrack devices                list audio input and output devices
                  sidetrack clips                  list Steam clips and whether the buffer covers them
                  sidetrack record [--track name=source]... [--retention-minutes 120] [--seconds N]
                                                   record tracks into the rolling buffer until Ctrl+C (or N seconds)
                  sidetrack watch  [same options as record] [--out dir]
                                                   record, and write a multi-track MP4 for each new Steam clip
                  sidetrack attach <clip folder | latest> [--tracks a,b] [--ref system] [--out dir] [--keep-temp]
                                                   write the multi-track MP4 for one clip

                Sources: mic, mic:<device name part>, system, system:<device name part>, app:<exe name>, pid:<process id>
                Default tracks: mic=mic, discord=app:Discord.exe, system=system (sync reference, not exported)
                Environment: SIDETRACK_FFMPEG (path to ffmpeg.exe), SIDETRACK_BUFFER (buffer folder)
                """);
            return 0;
        }

        public static int Unknown(string? command)
        {
            Console.Error.WriteLine($"sidetrack: unknown command \"{command}\". Run sidetrack help.");
            return 2;
        }

        public static int Doctor()
        {
            int problems = 0;
            void Ok(string label, string value) => Console.WriteLine($"  ok    {label}: {value}");
            void Warn(string label, string value) => Console.WriteLine($"  warn  {label}: {value}");
            void Fail(string label, string value)
            {
                problems++;
                Console.WriteLine($"  FAIL  {label}: {value}");
            }

            Console.WriteLine("Windows");
            Ok("version", Environment.OSVersion.VersionString);
            if (CaptureSession.ProcessLoopbackSupported(out var loopbackError)) Ok("per-app capture", "supported");
            else Fail("per-app capture", $"unavailable ({loopbackError}); app: tracks will not record");

            Console.WriteLine("Steam");
            var steam = SteamInstall.FindSteamPath();
            if (steam is null) Fail("install", "not found");
            else
            {
                Ok("install", steam);
                var user = SteamInstall.ActiveUser(steam);
                if (user is null) Fail("account", "no userdata folder");
                else
                {
                    Ok("account", user.AccountId);
                    var s = user.Settings;
                    Ok("background recording mode", s.BackgroundRecordMode ?? "(not set)");
                    Ok("audio option (raw)", s.AudioOption ?? "(not set)");
                    if (s.MicInMix == "1")
                        Warn("microphone", "Steam already mixes your mic into its track. Turn off the microphone in Steam > Settings > Game Recording, or drop Sidetrack's mic track, so it is not heard twice.");
                    else Ok("microphone", "not in Steam's mix");
                    var roots = user.RecordingRoots();
                    if (roots.Count == 0) Warn("recording roots", "none yet (record something with Steam first)");
                    foreach (var root in roots) Ok("recording root", root);

                    var newest = ClipCatalog.List(roots).LastOrDefault();
                    if (newest is null) Warn("newest clip", "none");
                    else
                    {
                        try
                        {
                            var clip = ClipInfo.Read(newest.Directory);
                            var (_, refusal) = ClipAttacher.Validate(clip);
                            if (refusal is null)
                                Ok("newest clip", $"{clip.Name}, metadata consistent with its manifest");
                            else Warn("newest clip", $"{clip.Name}: {refusal}");
                        }
                        catch (Exception ex) when (ex is FormatException or IOException)
                        {
                            Fail("newest clip", $"{newest.Name}: unreadable ({ex.Message})");
                        }
                    }
                }
            }

            Console.WriteLine("ffmpeg");
            var ffmpeg = Ffmpeg.Locate();
            if (ffmpeg is null) Fail("ffmpeg/ffprobe", "not found; install ffmpeg or set SIDETRACK_FFMPEG to ffmpeg.exe");
            else Ok("ffmpeg/ffprobe", ffmpeg.FfmpegPath);

            Console.WriteLine("Buffer");
            var store = new SegmentStore(SidetrackPaths.Buffer);
            Ok("folder", store.Root);
            foreach (var track in store.Tracks())
            {
                var segs = store.List(track);
                if (segs.Count == 0) continue;
                long bytes = segs.Sum(s => new FileInfo(s.Path).Length);
                Ok($"track {track}", $"{segs.Count} segments, {Local(segs[0].StartTicks)} to {Local(segs[^1].EndTicks)}, {bytes / (1024.0 * 1024):0} MB");
            }

            Console.WriteLine(problems == 0 ? "No blocking problems." : $"{problems} blocking problem(s).");
            return problems == 0 ? 0 : 1;
        }

        public static int Devices()
        {
            foreach (var d in AudioDevices.List())
                Console.WriteLine($"{(d.Kind == DeviceKind.Input ? "input " : "output")}  {(d.IsDefault ? "*" : " ")} {d.Name}");
            Console.WriteLine("* = Windows default");
            return 0;
        }

        public static int Clips()
        {
            var (_, roots) = SteamRoots();
            var store = new SegmentStore(SidetrackPaths.Buffer);
            var clips = ClipCatalog.List(roots);
            if (clips.Count == 0)
            {
                Console.WriteLine("No Steam clips found.");
                return 0;
            }
            foreach (var c in clips)
            {
                try
                {
                    var clip = ClipInfo.Read(c.Directory);
                    var covered = store.Tracks().Where(t => t != ReferenceTrack && Covers(store, t, clip)).ToList();
                    Console.WriteLine($"{c.Name}  {clip.CoarseStartUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {clip.DurationMs / 1000.0,6:0.0}s  " +
                                      $"{clip.Title ?? ""}  buffer: {(covered.Count == 0 ? "-" : string.Join(",", covered))}");
                }
                catch (Exception ex) when (ex is FormatException or IOException)
                {
                    Console.WriteLine($"{c.Name}  unreadable: {ex.Message}");
                }
            }
            return 0;
        }

        public static async Task<int> Record(Args cli, bool watch, CancellationToken cancel)
        {
            var tracks = TrackSpecs(cli);
            var retention = TimeSpan.FromMinutes(cli.Int("--retention-minutes", 120));
            var stopAt = cli.Value("--seconds") is null ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(cli.Int("--seconds", 1));
            var store = new SegmentStore(SidetrackPaths.Buffer);
            Directory.CreateDirectory(store.Root);

            Ffmpeg? ffmpeg = null;
            IReadOnlyList<string> roots = [];
            ClipWatcher? watcher = null;
            if (watch)
            {
                ffmpeg = Ffmpeg.Locate() ?? throw new UsageException("watch needs ffmpeg; install it or set SIDETRACK_FFMPEG.");
                (_, roots) = SteamRoots();
                watcher = new ClipWatcher(roots, SidetrackPaths.ProcessedLog);
            }

            var recorders = tracks.Select(t => new TrackRecorder(t.Name, t.Source, store)).ToList();
            foreach (var r in recorders) r.Start();
            Console.WriteLine($"Recording {string.Join(", ", tracks.Select(t => $"{t.Name} ({t.Source})"))} into {store.Root}");
            if (watch) Console.WriteLine($"Watching for new clips in {string.Join(", ", roots)}");
            Console.WriteLine("Ctrl+C to stop.");

            var nextStatus = DateTime.UtcNow.AddSeconds(5);
            var nextPrune = DateTime.UtcNow;
            try
            {
                while (!cancel.IsCancellationRequested && DateTime.UtcNow < stopAt)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancel).ContinueWith(_ => { });
                    if (DateTime.UtcNow >= nextStatus)
                    {
                        nextStatus = DateTime.UtcNow.AddSeconds(15);
                        foreach (var r in recorders)
                        {
                            var level = r.TakePeakDbfs() is { } db ? $"peak {db,4:0} dBFS" : "silent        ";
                            Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] {r.Track,-8} {r.State,-13} {r.TotalFrames / 48000.0,6:0}s  {level}  {r.Detail}");
                        }
                    }
                    if (DateTime.UtcNow >= nextPrune)
                    {
                        nextPrune = DateTime.UtcNow.AddMinutes(1);
                        store.Prune(DateTime.UtcNow.Ticks - retention.Ticks);
                    }
                    if (watcher is not null)
                    {
                        foreach (var clipDir in watcher.ReadyClips())
                        {
                            var options = AttachOptionsFor(cli, tracks);
                            await AttachOne(new ClipAttacher(store, ffmpeg!), clipDir, options, cancel);
                            watcher.MarkProcessed(clipDir);
                        }
                    }
                }
            }
            finally
            {
                foreach (var r in recorders) r.Dispose();
                Console.WriteLine("Stopped.");
            }
            return 0;
        }

        public static async Task<int> Attach(Args cli, CancellationToken cancel)
        {
            var target = cli.Positional.FirstOrDefault() ?? throw new UsageException("attach needs a clip folder or \"latest\".");
            if (target == "latest")
            {
                var (_, roots) = SteamRoots();
                target = ClipCatalog.List(roots).LastOrDefault()?.Directory ?? throw new UsageException("No Steam clips found.");
            }
            if (!File.Exists(Path.Combine(target, "clip.pb"))) throw new UsageException($"{target} is not a Steam clip folder (no clip.pb).");

            var ffmpeg = Ffmpeg.Locate() ?? throw new UsageException("attach needs ffmpeg; install it or set SIDETRACK_FFMPEG.");
            var store = new SegmentStore(SidetrackPaths.Buffer);
            var options = AttachOptionsFor(cli, TrackSpecs(cli));
            var result = await AttachOne(new ClipAttacher(store, ffmpeg), target, options, cancel);
            return result.Status == AttachStatus.Written ? 0 : 1;
        }

        private static async Task<AttachResult> AttachOne(ClipAttacher attacher, string clipDir, AttachOptions options, CancellationToken cancel)
        {
            var clip = ClipInfo.Read(clipDir);
            Console.WriteLine($"Clip {clip.Name} ({clip.Title}), {clip.DurationMs / 1000.0:0.0} s, starts {clip.CoarseStartUtc.ToLocalTime():HH:mm:ss.fff} per Steam");
            AttachResult result;
            try
            {
                result = await attacher.AttachAsync(clip, options, cancel);
            }
            catch (FfmpegException ex)
            {
                Console.WriteLine($"  failed: {ex.Message}");
                return new AttachResult(AttachStatus.Refused, ex.Message);
            }
            Console.WriteLine($"  {result.Status}: {result.Message}");
            if (result.Correlation is { } c)
                Console.WriteLine($"  correlation {c.Peak:0.000}, next best {c.RunnerUp:0.000} (margin {c.Margin:0.0}×)");
            foreach (var cov in result.Coverage ?? [])
            {
                var placement = cov.Coverage == 0 ? " (left out)"
                    : cov.AdjustmentMs is { } adj ? $", matched to Steam's mix ({adj:+0.0;-0.0} ms)"
                    : result.Sync == SyncState.Verified ? ", not in Steam's mix, placed by clock" : "";
                Console.WriteLine($"  track {cov.Track}: {cov.Coverage:P0} of the clip recorded{placement}");
            }
            if (result.OutputPath is not null) Console.WriteLine($"  wrote {result.OutputPath}");
            return result;
        }

        private static AttachOptions AttachOptionsFor(Args cli, IReadOnlyList<(string Name, CaptureSource Source)> tracks)
        {
            var reference = cli.Value("--ref") ?? ReferenceTrack;
            var names = cli.Value("--tracks")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        ?? tracks.Select(t => t.Name).Where(n => n != reference).ToArray();
            return new AttachOptions(
                names.Select(n => new ExportTrack(n, Title(n))).ToList(),
                reference,
                cli.Value("--out") ?? SidetrackPaths.DefaultOutput,
                cli.Flag("--keep-temp"));
        }

        private static List<(string Name, CaptureSource Source)> TrackSpecs(Args cli)
        {
            var given = cli.Values("--track");
            var specs = given.Count == 0
                ? DefaultTracks.ToList()
                : given.Select(t => t.IndexOf('=') is var i and > 0
                    ? (t[..i].Trim(), t[(i + 1)..].Trim())
                    : throw new UsageException($"--track {t}: expected name=source, e.g. discord=app:Discord.exe")).ToList();
            foreach (var (name, _) in specs)
                if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new UsageException($"Track name \"{name}\" is not a valid folder name.");
            if (specs.Select(s => s.Item1).Distinct(StringComparer.OrdinalIgnoreCase).Count() != specs.Count)
                throw new UsageException("Track names must be unique.");
            return specs.Select(s => (s.Item1, CaptureSource.Parse(s.Item2))).ToList();
        }

        private static (SteamUser User, IReadOnlyList<string> Roots) SteamRoots()
        {
            var steam = SteamInstall.FindSteamPath() ?? throw new UsageException("Steam is not installed (no SteamPath).");
            var user = SteamInstall.ActiveUser(steam) ?? throw new UsageException("No Steam account folder under userdata.");
            return (user, user.RecordingRoots());
        }

        private static bool Covers(SegmentStore store, string track, ClipInfo clip)
        {
            long start = clip.CoarseStartUtc.Ticks, end = start + TimeSpan.FromMilliseconds(clip.DurationMs).Ticks;
            return store.List(track).Any(s => s.StartTicks < end && s.EndTicks > start);
        }

        private static string Title(string track) => track switch
        {
            "mic" => "Mic",
            "discord" => "Discord",
            "system" => "System audio",
            _ => char.ToUpperInvariant(track[0]) + track[1..],
        };

        private static string Local(long utcTicks) => new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm:ss");
    }
}
