# Sidetrack

Separate audio tracks for Steam Game Recording clips. Steam records the
video; Sidetrack records your mic, Discord, or any app on their own tracks
and, when you save a clip, writes an MP4 with Steam's mix as track 1 plus
one track per source, synced to Steam's audio.

Windows 10 (build 20348+) or 11, .NET 10 SDK, and ffmpeg on PATH
(`winget install Gyan.FFmpeg.Essentials`). Design and measured results:
[docs/PLAN.md](docs/PLAN.md).

## Use

```bash
dotnet run --project src/Sidetrack.Cli -- doctor
```

```bash
dotnet run --project src/Sidetrack.Cli -- watch
```

`watch` records the default tracks (`mic`, `discord` = `app:Discord.exe`,
`system` = sync reference) and writes `Videos\Sidetrack\<clip title>.mp4`
for every clip Steam saves while it runs. Other commands:

| Command | Does |
|---|---|
| `doctor` | Checks Steam, recording roots, newest clip's metadata, ffmpeg, buffer, per-app capture |
| `devices` | Lists input and output devices |
| `clips` | Lists Steam clips and which buffered tracks cover them |
| `record [--track name=source]... [--seconds N]` | Records into the rolling buffer only |
| `attach <clip folder or latest> [--tracks a,b] [--ref system] [--out dir]` | Writes the MP4 for one clip |

Sources: `mic`, `mic:<device name part>`, `system`, `system:<device name part>`,
`app:<exe>`, `pid:<id>`. Example: `--track game=app:cs2.exe --track mic=mic --track system=system`.

Turn off Steam's own microphone recording (Steam > Settings > Game
Recording) when Sidetrack records your mic, or you hear it twice;
`doctor` warns when it is on.

## Develop

```bash
dotnet test
```

The buffer lives in `%LOCALAPPDATA%\Sidetrack\buffer` (override with
`SIDETRACK_BUFFER`) as one-minute WAV segments named by UTC start time,
kept for 120 minutes by default. Sidetrack only reads Steam's folders.
