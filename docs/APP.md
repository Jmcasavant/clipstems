# Clipstems app design (phase 2)

The tray app that replaces `clipstems watch`. Each section records a
decision and why; vendor facts are as of 2026-10-10.

## What it splits out

Steam's track is one mix of whatever Steam is set to record. Clipstems
splits that mix into parts, mirroring Steam's own choices, and adds each
part as its own track next to Steam's (which stays track 1, the complete
mix, for sharing):

| Track | Source | Lined up by |
|---|---|---|
| Mic | Windows default input (or a named device) | Steam's mix when Steam's Record Microphone is on, else the clock |
| Game | the running Steam game's process tree | Steam's mix |
| One per program ticked in Steam | that program's process tree | Steam's mix |
| Any app added in Clipstems | that app's process tree | Steam's mix if Steam records it, else the clock |

Steam's settings (verified 2026-10-10 against the settings screen, via
`localconfig.vdf` → `GameRecording`): `Audio_Mic` is Record Microphone,
`Recording_Audio_Option` `"0"` is "Game Audio Only" and `"2"` is "Game and
Selected Programs" (each verified 2026-10-10 by switching it in Steam);
`"1"` is taken to be "All System Audio", the dropdown's remaining choice
(unverified). `ExtraAudioSessions` lists the ticked programs as
audio-session ids ending in the exe path. Programs are mirrored only for
`"2"`, the one option where Steam records exactly the ticked list.
Steam's mic goes through Steam Voice processing (`SteamVoiceSettings`:
gain control, noise cancellation), so Steam's mic and the raw mic Clipstems records
differ in sound but still correlate.

## Behaviour a user sees

- **Tray icon only.** No main window. The icon shows the state (idle,
  recording, paused, problem), its tooltip says what is being recorded,
  and its menu offers: recent clips, open output folder, pause/resume,
  settings, quit.
- **Records only while a Steam game runs** (default), the way ShadowPlay
  and Medal behave: Steam's `RunningAppID` turns recording on, and it stops
  30 s after the game exits. The buffer outlives the game, so a clip saved
  later from Steam's Recordings view is still covered. "Always" is an option.
  Recording a microphone in the background is sensitive, so the icon always
  shows when it is happening and pause is one click away.
- **Every clip saved in Steam gets a multi-track MP4** in the output folder
  (default `Videos\Clipstems`) and a Windows notification that opens it.
  A clip Clipstems cannot sync or cover says so in the notification; it is
  never silently left with misplaced tracks.
- **The track layout is stable.** Every enabled track is written, silent
  where it had no audio, so track N is the same source in every clip and a
  video editor's track setup carries over. The notification names a track
  that was silent or missing a second or more.
- **Starts with Windows** (HKCU `Run`, so it shows and can be disabled
  in Settings > Apps > Startup) and is single-instance: launching it again
  opens Settings.

## Setups it must handle

| Variation | Handling |
|---|---|
| Any voice app or other program | Ticked in Steam: mirrored automatically. Otherwise a track lists one or more exe names and records the first one running; settings offers a picker of apps playing audio right now and a voice-chat preset (Discord stable/PTB/Canary, Vesktop, TeamSpeak 3 and 5, Mumble). |
| Any mic or headset, device changes | A track follows the Windows default device unless one is chosen by name; a default-device change or unplug restarts that capture. |
| Game outputs to a non-default device | The sync reference is process loopback of everything except Clipstems, which is device-independent, rather than the default output device. |
| Game track | On by default: the Steam game's process tree, found from `RunningAppID` → `appmanifest_<id>.acf` `installdir` in any library folder → the root process running from that folder. |
| Steam in any location, several accounts, moved recordings folder | Steam path from the registry; account from `ActiveUser`, else the most recent config; both recording roots watched (already in the CLI). |
| Steam's own mic on or off | Either works. On: Steam's track is the complete mix and the mic track is lined up exactly; Clipstems suggests it as a tip, never as a warning. |
| A user track and a ticked program name the same app | The user's single-app track wins, even switched off (so switching it off stops that app). A multi-app track ("any voice chat app") records only the first one running, so it claims no program. |
| Chosen device unplugged | The saved device id is kept and shown as "(not connected)"; that track retries until it returns rather than moving to another device. Devices are saved by Windows endpoint id, so two mics with the same name stay distinct. |
| ffmpeg missing | Recording continues; new clips wait unprocessed and get their tracks once ffmpeg is found, while their audio is still kept. |
| Two copies running (tray app and `clipstems watch`) | The buffer folder is owned through an exclusive file handle; the second copy says it is waiting and takes over when the first closes. |
| Clip Clipstems cannot handle | Spanning several recordings is refused with a notification naming the reason (support is a later milestone). |

## Structure

- `Clipstems.Core` gains the **engine**: settings, Steam game monitor,
  default-device watcher, track recorders, clip watcher, and a serial attach
  queue, with state and events the UI renders. The CLI's `watch` runs the
  same engine, so the tray app and the CLI cannot drift apart.
- `Clipstems.App` is **WPF on .NET 10 with the Fluent theme** (`ThemeMode`,
  light/dark/accent from Windows). WPF over WinUI 3: neither has a native
  tray icon, and WPF is the proven unpackaged tray-app path (EarTrumpet is
  WPF). The tray icon is H.NotifyIcon.Wpf (2.4.1, maintained), so menus get
  WPF styling; notifications are the tray icon's balloons, which Windows 10/11
  show as toasts, so no Windows App SDK runtime is needed.
- MVVM with CommunityToolkit.Mvvm 8.4; logging through
  `Microsoft.Extensions.Logging` to rolling files under
  `%LOCALAPPDATA%\Clipstems\logs` (Serilog file sink).
- Settings are JSON with a schema version, written atomically (temp file,
  then replace) so a crash never leaves a half-written file.

## Milestones

1. Engine in Core, CLI `watch` on top of it, tests. *(this change)*
2. Tray app: icon/state, menu, notifications, settings window (general,
   tracks with app/device pickers, Steam status, about), start with Windows,
   single instance. *(this change)*
3. Packaging: Velopack installer and updates (1.2.161 current), bundled
   ffmpeg LGPL shared build run as a separate process with its license
   and source link shipped in About; code signing decision.
4. Compressed buffer: done (see PLAN.md, "Compressed buffer"). Next: clips
   spanning several recordings; mixdown "quick share".
