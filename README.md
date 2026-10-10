# Clipstems

Separate audio tracks for Steam Game Recording clips. Steam records the
video; Clipstems records your mic, Discord, or any app on their own tracks
and, when you save a clip, writes an MP4 with Steam's mix as track 1 plus
one track per source, synced to Steam's audio.

Windows 10 (build 20348+) or 11, .NET 10 SDK, and ffmpeg on PATH
(`winget install Gyan.FFmpeg.Essentials`). Design and measured results:
[docs/PLAN.md](docs/PLAN.md).

## Use

The tray app records in the background and adds the tracks to every clip
you save in Steam; its window shows what each clip will contain:

```bash
dotnet run --project src/Clipstems.App
```

The CLI runs the same engine:

```bash
dotnet run --project src/Clipstems.Cli -- doctor
```

```bash
dotnet run --project src/Clipstems.Cli -- watch
```

`watch` splits Steam's mix into its parts: your mic, the game, and one
track per program ticked under Steam > Settings > Game Recording >
"Record Audio from…" (Game and Selected Programs). It writes
`Videos\Clipstems\<clip title>.mp4` for every clip Steam saves while it
runs, with Steam's mix kept as track 1. Other commands:

| Command | Does |
|---|---|
| `doctor` | Checks Steam (and what its track contains), the tracks Clipstems will record, ffmpeg, buffer, per-app capture |
| `devices` | Lists input and output devices |
| `apps` | Lists apps with audio sessions, for `app:` sources |
| `clips` | Lists Steam clips and which buffered tracks cover them |
| `record [--track name=source]... [--when always\|game] [--seconds N]` | Records into the rolling buffer only |
| `attach <clip folder or latest> [--tracks a,b] [--out dir]` | Writes the MP4 for one clip |

Sources: `mic`, `mic:<device>`, `system`, `system:<device>`,
`app:<exe>[|<exe>...]`, `game`, `all`, `pid:<id>`. Example:
`--track mic=mic --track game=game --track voice=app:Discord.exe|Vesktop.exe`.
Without `--track`, the app's saved tracks plus Steam's ticked programs are used.

## Develop

```bash
dotnet test
```

The buffer lives in `%LOCALAPPDATA%\Clipstems\buffer` (override with
`CLIPSTEMS_BUFFER`) as one-minute WAV segments named by UTC start time,
kept for 120 minutes by default. Clipstems only reads Steam's folders.
