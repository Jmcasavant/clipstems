# Clipstems plan

Clipstems adds separate audio tracks (mic, Discord, any app) to Steam Game
Recording clips. Steam keeps doing the video; Clipstems records the extra
audio alongside it and, when a clip is saved, writes a multi-track MP4 next
to it. No OBS scene setup, no ads, no overlay of its own.

## What Steam gives us

All verified 2026-10-10 on this PC (Steam client, Windows 11 build 26300)
unless a source is named.

- **One mixed audio track.** A clip's `session.mpd` lists one video
  AdaptationSet (HEVC, 3 s segments) and one audio AdaptationSet: AAC-LC
  stereo, 48 kHz, 128 kbps. Every audio source Steam captures is mixed into
  it. Separate tracks remain a community request
  ([Steam Client Beta forum](https://steamcommunity.com/groups/SteamClientBeta/discussions/5/600768571255062111/)).
- **No extension point.** `ISteamTimeline` lets games add markers; nothing
  lets a third party hook the recorder
  ([Steam Support FAQ](https://help.steampowered.com/en/faqs/view/23B7-49AD-4A28-9590)).
  Clipstems is a companion process that reads Steam's files, never writes them.
- **Layout.** A recordings root holds `gamerecording.pb`, `timelines/`,
  `video/bg_<appid>_<yyyyMMdd_HHmmss UTC>/` and `clips/clip_<appid>_<UTC>/`.
  A clip folder holds `clip.pb`, `timelines/`, and
  `video/bg_*/{session.mpd, init-stream{0,1}.m4s, chunk-stream{0,1}-NNNNN.m4s}`.
- **Two roots.** The root is `BackgroundRecordPath` in
  `userdata/<id>/config/localconfig.vdf` when set,
  else `userdata/<id>/gamerecordings`. Clips made before a path change stay
  in the old root, so the watcher scans both.
- **Clip timing (`clip.pb`, schemaless protobuf, field numbers observed):**
  - `1.3` timeline start, Unix seconds (equals `daterecorded` in the timeline JSON)
  - `2` clip start offset from timeline start, ms
  - `1.4` clip duration, ms
  - `1.5.1` bg folder name, `1.5.10` bg folder offset from timeline start, ms
  - `7` title, e.g. `"<Game> - <local date time>"`
  Check: `2 - 1.5.10` equals the manifest's `Period start` (10428179 - 51329
  = 10376850 ms = `PT2H52M56.85S`). Clip wall-clock start is therefore
  `1.3 + 2/1000`, good to about ±1 s because `1.3` is whole seconds.
- **Manifest quirk.** Clip manifests carry a non-zero `Period start`;
  ffmpeg's DASH demuxer then reads only the first segment
  ([Pineapple exporter notes](https://github.com/BLumia/pineapple-steam-recording-exporter)).
  Clipstems rewrites a temp copy with `start="PT0S"` and an absolute
  `BaseURL`, which ffmpeg 9.0.1 reads in place.
- **Clips carry lead-in footage.** Steam keeps whole 3 s segments, so a
  clip's media starts at the segment boundary before its logical start: the
  test clip's audio starts at 10374.008 s on the recording timeline, 2.84 s
  before its `Period start` of 10376.85 s, and holds 9 segments (27 s) for a
  22.17 s clip. Clipstems anchors sync on the first audio sample
  (`ffprobe` start time − `Period start` + metadata time) and declares the
  full on-disk duration in its manifest copy, because ffmpeg stops at the
  declared duration.
- **Steam's own audio settings** live in the same `localconfig.vdf`:
  `Recording_Audio_Option` and `Audio_Mic` (here `"1"`: your mic is already
  in Steam's mix). Clipstems reads them and warns about a doubled mic.

## Capture

- **Per-app audio:** WASAPI process loopback (`ActivateAudioInterfaceAsync`
  on `VAD\Process_Loopback`, include-tree mode), so Discord's child audio
  process is covered by targeting its root process. Documented minimum is
  build 20348; treated as a runtime capability, not a version check
  ([Microsoft sample](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/)).
- **Mic:** WASAPI shared capture on the default (or a named) input device.
- **Sync reference:** WASAPI loopback of the default output device. It
  contains whatever Steam heard, so it correlates with Steam's track. It is
  not exported by default.
- **One format everywhere:** shared mode with `AUTOCONVERTPCM`, 48 kHz,
  stereo, 16-bit, so every track mixes and muxes without resampling.
- **Timestamps:** each packet's `pu64QPCPosition` (100 ns QPC units,
  [GetBuffer docs](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer))
  is mapped to UTC through a QPC↔system-time anchor. Gaps longer than 50 ms
  are filled with silence; overlaps are trimmed.
- **Rolling buffer:** one-minute WAV segments per track, named by the UTC
  start time of their first frame (`<track>/<ticks>.wav`). A fresh anchor per
  segment bounds clock drift to a few ms. Retention defaults to 2 h, matching
  a typical Steam background buffer.

## Attaching a clip

1. Parse `clip.pb` → coarse wall-clock start, duration, bg segment. Refuse
   if it disagrees with the manifest's `Period start` by more than 5 ms.
2. Rewrite `session.mpd` to a temp manifest (`Period start=0`, static,
   full duration, absolute BaseURL) so Steam's folder is never modified.
   Probe stream start times and move the coarse start to the first audio
   sample.
3. Decode up to 60 s of Steam's audio to 8 kHz mono via ffmpeg.
4. Read the reference track from coarse-start − 3 s to end + 3 s, downmix to
   8 kHz mono, and run an FFT cross-correlation. The peak gives the exact
   offset (0.125 ms resolution).
5. **Sync is a state, never assumed.** *Verified* when the normalized peak
   and its margin over the next-best peak clear thresholds. *Unverified* when
   the reference is silent or ambiguous: the export still uses the coarse
   metadata time and says so. *Not covered* when the buffer has no audio for
   the window: no file is written.
6. Cut each exported track to the clip window and mux with ffmpeg: Steam's
   video and audio are stream-copied; extra tracks are encoded to AAC
   160 kbps and titled. Track 1 is Steam's mix and the default track, so
   Discord, X and phones (first track only) still play a full mix.

## Phases

| Phase | Delivers | Done when |
|---|---|---|
| 1. Prototype (this change) | `Clipstems.Core` + CLI: `doctor`, `clips`, `record`, `attach`, `watch`; unit tests | A real Steam clip gets mic + Discord tracks in sync, offset verified by correlation |
| 2. Daily driver | Tray app (WinUI 3 or WPF), settings UI, start with Windows, auto-start buffer when Steam runs a game (`RunningAppID`), game-only track via process loopback on the game's PID | Runs for a week of play with no manual step |
| 3. Quality | Opus/FLAC buffer instead of WAV (~10× less disk), drift resampling instead of per-minute re-anchoring, device-change and app-restart handling, multi-segment clips | 2 h buffer of 3 tracks under 300 MB; no audible seams |
| 4. Sharing | "Quick share" export that mixes selected tracks into one, per-track volume, MKV option, optional Steam Deck/Linux port on PipeWire | One click from clip to a share-ready file |

## Decisions

- **C# on .NET 10, Windows only, classic COM interop for WASAPI.** NAudio
  can't do process loopback and drops packet QPC timestamps, which sync
  depends on.
- **ffmpeg is an external dependency** found on PATH or set via
  `CLIPSTEMS_FFMPEG`; phase 2 bundles it. Tested with Gyan's 9.0.1
  essentials build (`winget install Gyan.FFmpeg.Essentials`).
- **Read-only toward Steam.** No writes into Steam folders and no binary
  patching; `clip.pb` field numbers are re-checked by `doctor` against the
  manifest's `Period start` (the cross-check above), so a Steam format
  change surfaces as an explicit error.
- **Prototype refuses** clips that span several bg segments or timelines,
  rather than guessing.

## Prototype results (verified 2026-10-10, on this PC)

- **Capture:** mic, default-output loopback and process loopback all record
  through the same WASAPI path. A 3 s tone played by one PowerShell process
  landed only on that process's track (RMS 1158, the tone's exact level) and
  in the system track.
- **Cross-source timestamps:** the tone sat 6.5 ms earlier in the system
  track than packet timestamps predicted against the process-loopback track.
  That is below the ~20–45 ms where A/V offset is noticeable; a per-source
  calibration belongs in phase 3 if it proves stable.
- **Sync on a real clip:** Steam's own audio from the WARDOGS clip, planted
  in a synthetic buffer 437.5 ms away from the metadata time, was recovered
  as +437.5 ms (correlation 0.76, next best 0.03, margin 22×).
- **Mux:** output has HEVC copied, Steam's AAC copied as the default track,
  and the extra tracks titled (`handler_name`/`name`). Clicks planted in a
  voice track landed within 0.15 ms of Steam's audio clock. That 0.15 ms is
  the 0.13 ms audio lead, which ffmpeg drops from `-itsoffset`; harmless.
- **Refusals and states:** with no reference track the file is still
  written, labeled "sync unverified" in the console and the MP4 comment
  tag; with no buffered audio, nothing is written (`NotCovered`).
- **Live session (17:42–17:47, WARDOGS, Discord call):** `watch` attached
  the saved clip unattended: sync verified at +551.3 ms from metadata
  (correlation 0.69, margin 3.0×), mic and Discord 100 % covered. An
  independent check against Steam's own mix (which contained Discord but,
  with Steam's mic turned off, not the mic) found Discord 21 ms late of
  Steam's audio: the system-output reference and process loopback differ
  by that much. Per-track refinement (below) brought it to 0.0 ms
  (correlation 0.92).
- **Per-track refinement:** after global sync, each track is matched
  against Steam's audio within ±100 ms and moved when the match is clear
  (peak ≥ 0.2, margin ≥ 2×). A track Steam does not record (the mic, once
  Steam's mic is off) keeps its clock placement and is reported as such.
  The mic's clock placement is not independently verified yet; a run with
  Steam's mic on for one clip would measure it.
- **Thresholds:** normalized peak ≥ 0.25 and margin ≥ 1.5× held with wide
  room on the real clip; revisit after live sessions with music-heavy games.
