using System.Globalization;
using Clipstems.Cli;
using Clipstems.Core;
using Clipstems.Core.Buffer;
using Clipstems.Core.Capture;
using Clipstems.Core.Engine;
using Clipstems.Core.Media;
using Clipstems.Core.Settings;
using Clipstems.Core.Steam;

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
        "apps" => Commands.Apps(),
        "clips" => Commands.Clips(),
        "record" => await Commands.Record(cli, watch: false, cancel.Token),
        "watch" => await Commands.Record(cli, watch: true, cancel.Token),
        "attach" => await Commands.Attach(cli, cancel.Token),
        "compact" => Commands.Compact(cancel.Token),
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
    Console.Error.WriteLine($"clipstems: {ex.Message}");
    return ex is UsageException ? 2 : 1;
}

namespace Clipstems.Cli
{
    internal static class Commands
    {
        private const string ReferenceTrack = ClipstemsEngine.ReferenceTrackId;

        public static int Help()
        {
            Console.WriteLine("""
                clipstems: separate audio tracks for Steam Game Recording clips

                  clipstems doctor                 check Steam, recording roots, ffmpeg, buffer, capture support
                  clipstems devices                list audio input and output devices
                  clipstems apps                   list apps with audio sessions (for app: sources)
                  clipstems clips                  list Steam clips and whether the buffer covers them
                  clipstems record [--track name=source]... [--when always|game] [--seconds N]
                                                   record tracks into the rolling buffer until Ctrl+C (or N seconds)
                  clipstems watch  [same options as record] [--out dir]
                                                   record, and write a multi-track MP4 for each new Steam clip
                  clipstems attach <clip folder | latest> [--tracks a,b] [--ref _reference] [--out dir] [--keep-temp]
                                                   write the multi-track MP4 for one clip
                  clipstems compact                compress closed buffer segments to Opus now (the app and
                                                   watch do this on their own; refused while either is running)

                Sources: mic, mic:<device>, system, system:<device>, app:<exe>[|<exe>...], game, all, pid:<id>
                Without --track, the tracks saved by the Clipstems app are used (default: mic, voice chat).
                A hidden sync reference (all apps except Clipstems) is always recorded.
                Environment: CLIPSTEMS_FFMPEG (path to ffmpeg.exe), CLIPSTEMS_BUFFER (buffer folder)
                """);
            return 0;
        }

        public static int Unknown(string? command)
        {
            Console.Error.WriteLine($"clipstems: unknown command \"{command}\". Run clipstems help.");
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
                    Ok("record audio from", s.AudioOptionDescription);
                    Ok("programs in Steam's mix", s.ProgramsInMix.Count == 0 ? "none" : string.Join(", ", s.ProgramsInMix.Select(p => p.Label)));
                    Ok("microphone in Steam's mix", s.MicInMix == "1" ? "yes (mic track lined up exactly)" : "no (mic track placed by clock)");
                    var saved = SettingsStore.Default.Load(out _);
                    Ok("Clipstems tracks", string.Join(", ", ClipstemsEngine.EffectiveTracks(saved, s).Select(t => $"{t.Title} ({t.Source})")));
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
            if (ffmpeg is null) Fail("ffmpeg/ffprobe", "not found; install ffmpeg or set CLIPSTEMS_FFMPEG to ffmpeg.exe");
            else Ok("ffmpeg/ffprobe", ffmpeg.FfmpegPath);

            Console.WriteLine("Buffer");
            var store = new SegmentStore(ClipstemsPaths.Buffer, new OpusSegmentCodec());
            Ok("folder", store.Root);
            foreach (var track in store.Tracks())
            {
                var segs = store.List(track);
                if (segs.Count == 0) continue;
                long bytes = segs.Sum(s => new FileInfo(s.Path).Length);
                Ok($"track {track}", $"{segs.Count} segments ({segs.Count(s => s.Compressed)} compressed), " +
                    $"{Local(segs[0].StartTicks)} to {Local(segs[^1].EndTicks)}, {bytes / (1024.0 * 1024):0} MB");
            }

            Console.WriteLine(problems == 0 ? "No blocking problems." : $"{problems} blocking problem(s).");
            return problems == 0 ? 0 : 1;
        }

        public static int Compact(CancellationToken cancel)
        {
            if (Ffmpeg.Locate() is null) throw new UsageException("compact needs ffmpeg; install it or set CLIPSTEMS_FFMPEG.");
            // The buffer's owner compresses as it records; a second compressor would race it.
            using var owner = BufferLock.TryAcquire(ClipstemsPaths.Buffer)
                ?? throw new UsageException("Clipstems (the app or watch) is running and compresses the buffer itself.");
            var store = new SegmentStore(ClipstemsPaths.Buffer, new OpusSegmentCodec());
            long Size() => store.Tracks().SelectMany(store.List).Sum(s => new FileInfo(s.Path).Length);
            long before = Size();
            int n = new BufferCompactor(store, store.Codec!).CompactClosed(DateTime.UtcNow, cancel);
            long after = Size();
            Console.WriteLine($"Compressed {n} segment(s): {before / (1024.0 * 1024):0} MB -> {after / (1024.0 * 1024):0} MB.");
            return 0;
        }

        public static int Devices()
        {
            foreach (var d in AudioDevices.List())
                Console.WriteLine($"{(d.Kind == DeviceKind.Input ? "input " : "output")}  {(d.IsDefault ? "*" : " ")} {d.Name}");
            Console.WriteLine("* = Windows default");
            return 0;
        }

        public static int Apps()
        {
            foreach (var app in AudioSessions.List())
                Console.WriteLine($"{(app.IsActive ? "playing" : "idle   ")}  {app.Exe,-28} {app.DisplayName}");
            return 0;
        }

        public static int Clips()
        {
            var (_, roots) = SteamRoots();
            var store = new SegmentStore(ClipstemsPaths.Buffer, new OpusSegmentCodec());
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
                    var covered = store.Tracks().Where(t => !t.StartsWith('_') && Covers(store, t, clip)).ToList();
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
            var saved = SettingsStore.Default.Load(out _);
            var settings = SettingsFor(cli) with
            {
                RecordWhen = (cli.Value("--when") ?? "always") switch
                {
                    "always" => RecordPolicy.Always,
                    "game" => RecordPolicy.WhileSteamGameRuns,
                    var other => throw new UsageException($"--when {other}: use always or game."),
                },
                RetentionMinutes = cli.Int("--retention-minutes", saved.RetentionMinutes),
                OutputFolder = cli.Value("--out") ?? saved.OutputFolder,
            };
            var stopAt = cli.Value("--seconds") is null ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(cli.Int("--seconds", 1));

            await using var engine = new ClipstemsEngine(settings, new EngineOptions { AutoAttach = watch });
            engine.ClipFinished += (_, outcome) => Print(outcome);
            engine.Start();
            var steamSettings = SteamInstall.FindSteamPath() is { } steamPath && SteamInstall.ActiveUser(steamPath) is { } user
                ? user.Settings : SteamRecordingSettings.Empty;
            Console.WriteLine($"Tracks: {string.Join(", ", ClipstemsEngine.EffectiveTracks(settings, steamSettings).Select(t => $"{t.Id} ({t.Source})"))}");
            Console.WriteLine($"Buffer: {ClipstemsPaths.Buffer}; recording {(settings.RecordWhen == RecordPolicy.Always ? "now" : "while a Steam game runs")}" +
                              (watch ? $"; new clips go to {settings.EffectiveOutputFolder}" : ""));
            Console.WriteLine("Ctrl+C to stop.");

            var nextStatus = DateTime.UtcNow.AddSeconds(5);
            IReadOnlyList<EngineIssue> shownIssues = [];
            while (!cancel.IsCancellationRequested && DateTime.UtcNow < stopAt)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancel).ContinueWith(_ => { });
                if (!engine.Issues.SequenceEqual(shownIssues))
                {
                    shownIssues = engine.Issues;
                    foreach (var issue in shownIssues)
                        Console.WriteLine($"  {(issue.Severity == IssueSeverity.Problem ? "PROBLEM" : "warning")}: {issue.Message}");
                }
                if (DateTime.UtcNow < nextStatus) continue;
                nextStatus = DateTime.UtcNow.AddSeconds(15);
                Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] {engine.Status}{(engine.Game is { } g ? $" ({g.Name})" : "")}");
                foreach (var t in engine.Tracks)
                {
                    var level = t.PeakDbfs is { } db ? $"peak {db,4:0} dBFS" : "silent        ";
                    Console.WriteLine($"      {t.Id,-8} {t.State,-10} {level}  {t.Detail}");
                }
            }
            Console.WriteLine("Stopping.");
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

            var ffmpeg = Ffmpeg.Locate() ?? throw new UsageException("attach needs ffmpeg; install it or set CLIPSTEMS_FFMPEG.");
            var store = new SegmentStore(ClipstemsPaths.Buffer, new OpusSegmentCodec());
            var options = AttachOptionsFor(cli, SettingsFor(cli));
            var clip = ClipInfo.Read(target);
            Console.WriteLine($"Clip {clip.Name} ({clip.Title}), {clip.DurationMs / 1000.0:0.0} s, starts {clip.CoarseStartUtc.ToLocalTime():HH:mm:ss.fff} per Steam");
            AttachResult result;
            try { result = await new ClipAttacher(store, ffmpeg).AttachAsync(clip, options, cancel); }
            catch (FfmpegException ex) { result = new AttachResult(AttachStatus.Refused, ex.Message); }
            Print(new ClipOutcome(target, clip.Title, result, DateTime.UtcNow));
            return result.Status == AttachStatus.Written ? 0 : 1;
        }

        private static void Print(ClipOutcome outcome)
        {
            var result = outcome.Result;
            Console.WriteLine($"Clip {outcome.ClipName} ({outcome.Title})");
            Console.WriteLine($"  {result.Status}: {result.Message}");
            if (result.Correlation is { } c)
                Console.WriteLine($"  correlation {c.Peak:0.000}, next best {c.RunnerUp:0.000} (margin {c.Margin:0.0}x)");
            foreach (var cov in result.Coverage ?? [])
            {
                var placement = cov.Coverage == 0 ? " (left out)"
                    : cov.AdjustmentMs is { } adj ? $", matched to Steam's mix ({adj:+0.0;-0.0} ms)"
                    : result.Sync == SyncState.Verified ? ", not in Steam's mix, placed by clock" : "";
                Console.WriteLine($"  track {cov.Track}: {cov.Coverage:P0} of the clip recorded{placement}");
            }
            if (result.OutputPath is not null) Console.WriteLine($"  wrote {result.OutputPath}");
        }

        private static AttachOptions AttachOptionsFor(Args cli, ClipstemsSettings settings)
        {
            var names = cli.Value("--tracks")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var tracks = names is null
                ? settings.Tracks.Where(t => t.Enabled).Select(t => new ExportTrack(t.Id, t.Title)).ToList()
                : names.Select(n => new ExportTrack(n, settings.Tracks.FirstOrDefault(t => t.Id == n)?.Title ?? Title(n))).ToList();
            return new AttachOptions(
                tracks,
                cli.Value("--ref") ?? ReferenceTrack,
                cli.Value("--out") ?? settings.EffectiveOutputFolder,
                cli.Flag("--keep-temp"));
        }

        /// <summary>The app's saved settings, with tracks replaced when --track is given.</summary>
        private static ClipstemsSettings SettingsFor(Args cli)
        {
            var saved = SettingsStore.Default.Load(out var problem);
            if (problem is not null) Console.Error.WriteLine($"clipstems: {problem}");
            var given = cli.Values("--track");
            if (given.Count == 0) return saved;
            var tracks = given.Select(t => t.IndexOf('=') is var i and > 0
                    ? new TrackSettings(t[..i].Trim(), Title(t[..i].Trim()), true, CaptureSource.Parse(t[(i + 1)..].Trim()))
                    : throw new UsageException($"--track {t}: expected name=source, e.g. voice=app:Discord.exe")).ToList();
            try { return SettingsStore.Validate(saved with { Tracks = tracks }); }
            catch (ArgumentException ex) { throw new UsageException(ex.Message); }
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
            "voice" => "Voice chat",
            "game" => "Game",
            "system" => "System audio",
            _ => char.ToUpperInvariant(track[0]) + track[1..],
        };

        private static string Local(long utcTicks) => new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm:ss");
    }
}
