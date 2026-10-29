# LifeReplay

Continuous, local-first screen recording for Windows (plus an iPhone companion), with a DVR-style replay
timeline for looking back at your day.

- **Recorder** (`LifeReplay.exe`): tray app that records every monitor in 5-minute H.264 MP4 segments using
  Windows.Graphics.Capture and the GPU's hardware encoder. 10 fps, 1080p, about 560 MB per hour per monitor by default.
- **Viewer** (`LifeReplay.Viewer.exe`): pick a day, see every screen on one timeline, play at 1–16×, scrub with
  thumbnail previews, jump to a time, skip gaps and idle stretches, delete recordings.
- **iPhone** (`ios/`): ReplayKit broadcast extension + app that syncs segments to the PC over Wi‑Fi.
  Written but **not built or tested** (no Mac available). See [docs/ios.md](docs/ios.md).

Everything stays on your PC. Nothing is uploaded anywhere, and iPhone sync (local network only, encrypted,
paired) is off until you enable it.

## Status

| Area | State |
|---|---|
| Windows recording (multi-monitor code path, cursor, segments, HW encoder + SW fallback) | ✅ Verified on this PC (1 monitor, Intel Arc, hardware encoder) |
| Accurate timestamps / contiguous segments / file time = real time | ✅ Verified (`--verify`: within 1 frame) |
| Crash recovery (kill mid-segment, restart) | ✅ Verified |
| Tray, pause/resume, timed pause, hotkey, gaps | ✅ Pause/resume/shutdown verified via the command channel; the tray menu itself was not clicked in testing |
| Sleep / lock / Modern Standby / display off | ✅ Happened for real twice during testing; the final build paused on display-off, closed the file cleanly and resumed after wake |
| Display add/remove, multiple monitors | ⚠️ Implemented, not exercised (only one monitor available) |
| Retention & quota, disk-low pause | ✅ Retention logic unit-tested; ⚠️ disk-low pause not exercised |
| Privacy blanking (excluded apps/titles, manual `--blank`) | ✅ Verified: title match triggered blanking; decoded frames are black exactly during the blank window |
| Auto-start with Windows, restart after crash | ⚠️ Implemented (Run key, RegisterApplicationRestart), not exercised |
| Replay viewer: date, timeline, seek, jump, 1–16×, multi-screen, iPhone lane | ✅ Verified (UI Automation + screenshots) |
| Thumbnail hover preview | ⚠️ Thumbnails generated and verified on disk; hover popup not exercised (needs a real mouse) |
| iPhone sync server (HTTPS, pinning, token, ingest) | ✅ Unit + end-to-end tested with `curl` as the phone |
| iPhone app / broadcast extension | ❌ Not compiled: needs macOS + Xcode (see docs/ios.md) |

Full test log with numbers: [docs/testing.md](docs/testing.md). Design: [docs/architecture.md](docs/architecture.md).

## Quick start (Windows 10 2004+ / Windows 11)

```powershell
.\build.ps1                                   # runs tests, publishes to dist\LifeReplay (self-contained)
.\dist\LifeReplay\LifeReplay.exe              # recorder: appears in the tray and starts recording
.\dist\LifeReplay\LifeReplay.Viewer.exe       # replay (or double-click the tray icon)
```

`build.ps1` needs the .NET 10 SDK. If it's missing, the script prints install commands
(`winget install Microsoft.DotNet.SDK.10`). The published folder runs without .NET installed.

Everything is configured in tray → **Settings…**:
- **Start with Windows**: launches the recorder when you log in (a per-user entry in
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`; no script or admin rights).
- **Recording hours** (e.g. 09:00–18:00, optionally Mon–Fri only): outside them the recorder stays in the
  tray but records nothing, and the timeline shows "Outside recording hours". It starts and stops on time
  by itself, also after sleep.

### Developer build

```powershell
dotnet build desktop\LifeReplay.slnx                 # outputs to desktop\bin\Debug\
dotnet test  desktop\tests\LifeReplay.Tests
desktop\bin\Debug\LifeReplay.exe --record-for 60 --segment-seconds 20 --data-dir C:\temp\lr-test
desktop\bin\Debug\LifeReplay.exe --verify --deep --data-dir C:\temp\lr-test
desktop\bin\Debug\LifeReplay.Viewer.exe --data-dir C:\temp\lr-test
```

## Using it

**Tray menu:** status, storage used, pause / resume / pause 15 min / pause 1 h, black out video, open replay, open recordings
folder, settings, start with Windows, iPhone sync (enable, pair, revoke), logs, exit.

**Tray icon** (the recording indicator): red dot = recording · red with black square = recording, video blanked
for privacy · grey pause = paused · orange = problem (hover for details).

**Hotkey:** `Ctrl+Alt+Shift+P` pauses and resumes immediately (configurable).

**Command line**

```
LifeReplay.exe                         tray mode (default)
LifeReplay.exe --pause | --resume | --shutdown     control the running recorder
LifeReplay.exe --blank | --unblank     keep recording, but black out the video (privacy)
LifeReplay.exe --export-frame <mp4> <seconds> <out.jpg>    decode one frame (diagnostics)
LifeReplay.exe --record-for <s>        headless timed recording (testing)
LifeReplay.exe --verify [--deep]       integrity check of all segments (--deep decodes a frame of each)
  --data-dir <path>  --segment-seconds <n>  --fps <n>  --software-encoder
LifeReplay.Viewer.exe [--data-dir <path>] [--date 2026-10-27] [--time 14:30]
```

**Viewer keys:** Space play/pause · ←/→ ±10 s (Shift ±1 min) · PgUp/PgDn previous/next recording · ↑/↓ speed ·
F5 refresh · mouse wheel on the timeline = zoom, Shift+wheel or right-drag = pan, click/drag = seek.

On the timeline, blue = Windows recording, green = iPhone, purple = still being written, red = lost/corrupt,
darker stripes = nothing changed on screen, red hatching = gap (labelled with the reason when known: paused,
locked, sleep, display off, private app, recorder not running, crash…).

## What did I work on? (Summary → Apps / Topics / Plan)

The viewer's **Summary** button opens a panel with three views of the selected day, built from the window-title log:

- **Apps**: time per app, with the tabs / documents / files under each.
- **Topics**: totals for **YouTube**, **Social** and **Other** at the top, then YouTube and Social as fixed
  groups and everything else grouped automatically by *meaning*, so "Domain Theory.pdf", "위상수학 과제.pdf" and "Scott
  domains.pdf" become one topic even though they share no words. No setup needed.
- **Plan**: your todo list with time per task and **ahead / behind schedule** for today.

Topics and Plan use a small multilingual language model (paraphrase-multilingual-MiniLM-L12-v2, Korean +
English, 113 MB, downloaded once from Hugging Face and verified by checksum). It runs on the CPU only while the
panel is open (~250 MB of memory; 300 titles take about 1 s), and nothing leaves the PC.

**Todo list** (`Documents\LifeReplay\todo.md` by default; "Open todo list" creates it):

```md
# Every day
hours: 09:00-18:00              ← working window for ahead/behind (else recording hours, else 09–18)

# 2026-10-30
- [ ] pwn.college: Program Misuse ~2h
  match: pwn.college            ← optional exact keywords
  progress: 3/10 levels         ← optional; gives "about 1h 10m left at your pace"
- [ ] Read domain theory paper ~1h
- [x] Fix LifeReplay viewer ~45m
```

How windows are matched to tasks, most trusted first:
1. **your choice**: unmatched windows have an "Assign to…" box; LifeReplay remembers it;
2. **keywords** from `match:`;
3. **learned**: very similar to a window you assigned before;
4. **meaning**: the task the model finds most similar (score ≥ 0.40 and clearly ahead of the runner-up);
5. **context**: a window between two windows of the same task (≤ 5 min apart), e.g. a terminal between
   two pwn.college tabs.

Ahead/behind = time spent on planned tasks − (planned total × share of the working day passed).

## Settings (`%LOCALAPPDATA%\LifeReplay\config.json`)

Changes are applied automatically when the file is saved (or via tray → Reload settings).

| Key | Default | Meaning |
|---|---|---|
| `segmentSeconds` | 300 | Length of each MP4 file (10–3600). A crash loses at most the current segment. |
| `fps` | 10 | Frames per second (1–30). Frames are only taken when the screen changes. |
| `maxOutputHeight` | 1080 | Downscale to this height; 0 = native resolution. |
| `quality` / `bitrateKbps` | medium / 0 | low, medium, high bitrate presets, or a fixed bitrate. |
| `captureCursor` | true | Include the mouse pointer. |
| `showCaptureBorder` | false | Windows' yellow capture border (Windows 11 lets apps turn it off). |
| `hardwareEncoding` | true | GPU H.264 encoder, with automatic software fallback. |
| `recordOnLaunch` | true | Start recording when the recorder starts. |
| `autoRestartAfterCrash` | true | Ask Windows to relaunch the recorder after a crash. |
| `retentionDays` | 30 | Delete recordings older than this (0 = keep). |
| `maxStorageGB` | 100 | Delete oldest recordings above this total (0 = no limit). |
| `minFreeDiskGB` | 5 | Pause recording if the disk gets this full. |
| `thumbnailIntervalSeconds` | 30 | Timeline preview frequency. |
| `excludedProcesses` | KeePass, 1Password, Bitwarden… | Black out the video while one of these is in the foreground. |
| `excludedWindowTitles` | InPrivate, Incognito, Private Browsing | Same, matched against the foreground window title. |
| `pauseHotkey` | Ctrl+Alt+Shift+P | Global pause/resume hotkey. |
| `hours.enabled` / `hours.start` / `hours.end` / `hours.weekdaysOnly` | false / 09:00 / 18:00 / false | Only record during these local hours. |
| `logWindowTitles` | true | Log which app and window title (tab / document / file) is in front, for the viewer's **Summary** panel and "Apps / tabs" timeline lane. Only while recording; excluded windows appear as "(private)". |
| `idleMinutes` | 5 | No input and no screen change for this long isn't counted as time on an app. |
| `sync.enabled` / `sync.port` / `sync.bindAddress` | false / 47800 / 0.0.0.0 | iPhone sync endpoint. |

## Privacy and security

- Local-first: recordings, database and logs live in the data folder. No cloud and no telemetry.
- Never hidden: a tray icon shows the state at all times, the viewer shows recorder status, and the hotkey pauses instantly.
- No audio, ever.
- Exclusions blank the video while sensitive apps are in front (best effort: only the foreground window is
  checked, so an excluded window visible on another monitor while something else has focus is still recorded).
  Apps that use `WDA_EXCLUDEFROMCAPTURE` are excluded by Windows itself.
- Lock screen, UAC prompts and sleep are never recorded (Windows can't capture them, and LifeReplay pauses).
- Delete by segment, session or day in the viewer; retention and quota clean up automatically.
- iPhone sync: HTTPS only, certificate pinned by fingerprint, 256-bit pairing token (stored hashed on the PC),
  upload-only API (recordings can't be downloaded over the network), off by default.
  See [shared/sync-protocol.md](shared/sync-protocol.md).
- Anyone with access to your Windows account can read the recordings. Use BitLocker / device encryption for
  protection at rest.

## Repository layout

```
desktop/   Windows: Core, Sync, Recorder (tray), Viewer (WPF), Tests
ios/       iPhone app + Broadcast Upload Extension (XcodeGen project.yml)
shared/    sync protocol, metadata JSON schema
docs/      architecture, testing, iOS
build.ps1  test + publish
```

The runtime folders from the original plan (`recordings/`, `database/`) live in the data folder
(`%LOCALAPPDATA%\LifeReplay`), not in the repository. Point `LIFEREPLAY_DATA` or `--data-dir` elsewhere if you prefer.

## Known limitations (Windows)

- The segment being written when the recorder crashes or loses power is lost (≤ 5 min by default). Everything
  finalized before it is safe.
- Media Foundation's encoders run at a constant bitrate here, so a static screen costs as much space as a busy
  one. Lower `quality`, `fps` or `maxOutputHeight` to save space (see the storage table in docs/architecture.md).
- Playback at 16× may drop frames on slow PCs (it decodes 160 fps for 10 fps recordings).
- Multi-monitor recording, monitor hot-plug, exclusions and auto-start are implemented but were only exercised
  on a single-monitor PC.
