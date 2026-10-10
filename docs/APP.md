# Sidetrack app design (phase 2)

The tray app that replaces `sidetrack watch`. Each section records a
decision and why; vendor facts are as of 2026-10-10.

## What it splits out

Steam's track is one mix of whatever Steam is set to record. Sidetrack
splits that mix into parts, mirroring Steam's own choices, and adds each
part as its own track next to Steam's (which stays track 1, the complete
mix, for sharing):

| Track | Source | Lined up by |
|---|---|---|
| Mic | Windows default input (or a named device) | Steam's mix when Steam's Record Microphone is on, else the clock |
| Game | the running Steam game's process tree | Steam's mix |
| One per program ticked in Steam | that program's process tree | Steam's mix |
| Any app added in Sidetrack | that app's process tree | Steam's mix if Steam records it, else the clock |

Steam's settings (verified 2026-10-10 against the settings screen, via
`localconfig.vdf` → `GameRecording`): `Audio_Mic` is Record Microphone,
`Recording_Audio_Option` `"2"` is "Game and Selected Programs", and
`ExtraAudioSessions` lists the ticked programs as audio-session ids ending
in the exe path. The values for "Game Audio Only" and "All System Audio"
are not mapped yet; until they are, programs are mirrored only for "2".
Steam's mic goes through Steam Voice processing (`SteamVoiceSettings`:
gain control, noise cancellation), so Steam's mic and Sidetrack's raw mic
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
  (default `Videos\Sidetrack`) and a Windows notification that opens it.
  A clip Sidetrack cannot sync or cover says so in the notification; it is
  never silently left with misplaced tracks.
- **Starts with Windows** (HKCU `Run`, so it shows and can be disabled
  in Settings > Apps > Startup) and is single-instance: launching it again
  opens Settings.

## Setups it must handle

| Variation | Handling |
|---|---|
| Any voice app or other program | Ticked in Steam: mirrored automatically. Otherwise a track lists one or more exe names and records the first one running; settings offers a picker of apps playing audio right now and a voice-chat preset (Discord stable/PTB/Canary, Vesktop, TeamSpeak 3 and 5, Mumble). |
| Any mic or headset, device changes | A track follows the Windows default device unless one is chosen by name; a default-device change or unplug restarts that capture. |
| Game outputs to a non-default device | The sync reference is process loopback of everything except Sidetrack, which is device-independent, rather than the default output device. |
| Game track | On by default: the Steam game's process tree, found from `RunningAppID` → `appmanifest_<id>.acf` `installdir` in any library folder → the root process running from that folder. |
| Steam in any location, several accounts, moved recordings folder | Steam path from the registry; account from `ActiveUser`, else the most recent config; both recording roots watched (already in the CLI). |
| Steam's own mic on or off | Either works. On: Steam's track is the complete mix and the mic track is lined up exactly; Sidetrack suggests it as a tip, never as a warning. |
| Clip Sidetrack cannot handle | Spanning several recordings is refused with a notification naming the reason (support is a later milestone). |

## Structure

- `Sidetrack.Core` gains the **engine**: settings, Steam game monitor,
  default-device watcher, track recorders, clip watcher, and a serial attach
  queue, with state and events the UI renders. The CLI's `watch` runs the
  same engine, so the tray app and the CLI cannot drift apart.
- `Sidetrack.App` is **WPF on .NET 10 with the Fluent theme** (`ThemeMode`,
  light/dark/accent from Windows). WPF over WinUI 3: neither has a native
  tray icon, and WPF is the proven unpackaged tray-app path (EarTrumpet is
  WPF). The tray icon is H.NotifyIcon.Wpf (2.4.1, maintained), so menus get
  WPF styling; notifications are the tray icon's balloons, which Windows 10/11
  show as toasts, so no Windows App SDK runtime is needed.
- MVVM with CommunityToolkit.Mvvm 8.4; logging through
  `Microsoft.Extensions.Logging` to rolling files under
  `%LOCALAPPDATA%\Sidetrack\logs` (Serilog file sink).
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
4. Clips spanning several recordings; compressed buffer (Opus segments via
   ffmpeg after each minute closes, ~10× smaller); mixdown "quick share".
