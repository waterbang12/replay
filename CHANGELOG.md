# Changelog

Each entry says what changed and why it was needed.

## 0.2.0 — 2026-10-29

### Added
- **Settings window** (tray → Settings…, or `LifeReplay.exe --settings`) with General, Video, Storage and
  Privacy tabs. Changes apply immediately.
  *Why:* settings could only be changed by editing `config.json` in Notepad, which is error-prone (one typo
  and the file is rejected) and hides what each option does.
- **Recording hours** ("only record 09:00–18:00", optional Mon–Fri). Outside the hours the recorder stays in
  the tray and the timeline shows "Outside recording hours".
  *Why:* the goal is to replay study/work time, not evenings. A first attempt used two Windows scheduled
  tasks (`schedule.ps1`), but that needed a script, broke when the PC was asleep at the trigger time, and
  hung when the app couldn't find .NET under Task Scheduler. Building it into the recorder is simpler and
  always correct.
- **Window-title log** (new `focus_events` table, database schema v2): which app and window title
  (browser tab, VS Code file, PDF name) was in front, and for how long. Logged only while recording; idle
  time is not counted; excluded apps are stored as "(private)"; deleted recordings take their log with them.
  *Why:* knowing *what* you worked on, not just seeing video of it. Titles are the cheapest and most accurate
  signal ("pwn.college – Program Misuse – Chrome"), and they're the base for later features (per-topic time,
  assignment tracking, ahead/behind estimates) without any image analysis.
- **Viewer: Summary panel and "Apps / tabs" lane.** Time per app with the tabs/documents under it; click to jump.
  *Why:* answers "how long did I spend on X today" at a glance instead of scrubbing through video.
- **`--blank` / `--unblank` and tray "Black out video"**: keep the timeline running but record black frames.
  *Why:* an instant privacy switch that doesn't create a gap in the timeline.

### Fixed
- **Viewer title bar off-screen** (no visible minimize/close). The window opened at a fixed 1400×900, which
  is larger than a 1920×1200 screen at 150% scaling. It now sizes itself to the screen.
- **"Start with Windows" tick not shown.** The tray read the setting only once at startup; it now re-checks
  every time the menu opens.
- **Delete menu not usable with keyboard/automation**; it's now a standard menu.
- **`build.ps1` failed** when an Explorer window was open in `dist`; it now empties the folder instead of deleting it.

## 0.1.0 — 2026-10-27

First version: tray recorder (Windows.Graphics.Capture + hardware H.264 in 5-minute MP4 segments),
replay viewer, crash recovery, retention/quota, privacy exclusions, opt-in iPhone sync server, and the
iOS app (written, not yet compiled). Bugs found by real testing and fixed before release:

- **Video time drifted from real time** (up to 11% longer files): frames are now placed on a fixed frame grid.
  *Why it mattered:* seeking to 14:30 must show 14:30.
- **Static screens showed upcoming content early**, because Media Foundation fills gaps with the *next* frame.
  This also broke privacy blanking. Every frame slot now gets a sample.
- **Memory grew ~10 MB/minute**: full-size thumbnail bitmaps and textures that Windows never handed back.
  Now flat at about 180–220 MB.
- **Overnight Modern Standby lost a segment**: Windows sends no suspend event, so the encoder later tried to fill
  hours of frames. Now recording pauses when the display turns off, and time jumps close the segment cleanly.
- **iPhone sync returned 500 for malformed JSON**; it now returns 400.
