# LifeReplay architecture

## Components

```
desktop/
  src/LifeReplay.Core      .NET 10 library: SQLite store + migrations, MP4 integrity check, crash recovery,
                           retention, day timeline/gap model, iPhone app events + session rebuilding
                           (AppEvents, AppSessions), config, logging. No Windows APIs.
  src/LifeReplay.Sync      Opt-in HTTPS endpoint (127.0.0.1, bearer token) for iPhone Shortcuts app events,
                           published to the tailnet with Tailscale Serve. See docs/iphone-shortcuts.md.
  src/LifeReplay.Recorder  LifeReplay.exe: tray app + capture/encode pipeline (WinForms, WinRT, Direct3D 11).
  src/LifeReplay.Viewer    LifeReplay.Viewer.exe: WPF replay app (calendar, timeline, multi-screen playback).
  src/LifeReplay.Coach     Coach library: deterministic schedule evaluator, notification rules, sanitized context,
                           OpenAI Responses provider, answer validator, audit log/budget (see docs/coach.md).
  src/LifeReplay.CoachTray LifeReplay.CoachTray.exe: separate tray process for coach notifications.
  tests/LifeReplay.Tests   xUnit tests for Core, Sync, Analysis and Coach.
shared/                    iPhone event protocol (sync-protocol.md).
```

The recorder and the viewer are separate processes. A viewer crash can't stop recording, and the viewer
works while the recorder is off. They share only the data folder: SQLite (WAL mode, so both can use it at once),
the MP4 files, and `status.json`, which the recorder rewrites every 5 s.

## Technology choices

**Capture: Windows.Graphics.Capture (WGC), not Desktop Duplication (DXGI DDA).**
- WGC hands frames to us as D3D11 textures only when the screen changes, includes the cursor optionally,
  handles DPI/rotation, and raises `Closed` when a monitor disappears.
- WGC respects `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`, so apps that protect themselves stay protected.
- DDA needs per-output duplication objects, manual cursor compositing, and recreation on every mode change,
  `DXGI_ERROR_ACCESS_LOST` or secure-desktop switch. WGC covers the same needs with less code to get wrong.
- Neither can capture the secure desktop (UAC prompt, lock screen). LifeReplay pauses on lock and records a gap.
- On Windows 11 the yellow capture border is turned off (`IsBorderRequired = false`, borderless access is granted
  to unpackaged apps). The tray icon is the recording indicator.

**Encoding: Media Foundation through `MediaTranscoder` + `MediaStreamSource`.**
- D3D textures go straight to the encoder (`MediaStreamSample.CreateFromDirect3D11Surface`). The BGRA→NV12
  conversion, scaling and H.264 encoding run on the GPU (Intel Quick Sync on this machine).
- `HardwareAccelerationEnabled = true` lets Media Foundation pick the GPU encoder and fall back to its software encoder.
  If the hardware path fails twice, the recorder switches to software for the rest of the run.
- No FFmpeg or native DLLs to ship. Dependencies: Vortice.Direct3D11 (thin D3D11 wrapper), Microsoft.Data.Sqlite,
  QRCoder (pairing QR code).

**One sample per frame slot (important).** The Media Foundation pipeline writes a constant-frame-rate MP4.
Testing showed two behaviours that matter for a "what was on screen at time T" recorder:
1. With irregular input timestamps, every sample still gets one frame interval, so files drifted up to 11%
   longer than real time.
2. Gaps between samples are filled with the **next** frame, so a static stretch showed the *upcoming* screen
   early. Privacy blanking was visibly wrong in decoded frames.

So the encoder submits exactly one sample per slot (`first + k × 1/fps`): the newest captured frame if the
screen changed during that slot, otherwise the previous picture again. File position *k* is then exactly *k*
intervals of real time, and content is never shown early. Verified by decoding frames around a blanking window:
black starts and ends within one frame of the recorded times. `--verify` checks every file's duration against the clock.
This costs no extra encoding (Media Foundation encoded every slot anyway); repeated frames compress to almost nothing.

**Texture lifetime.** Captured frames are copied into a small pool of GPU textures. A texture returns to the
pool when Media Foundation reports its sample as processed. Media Foundation doesn't report the last few
samples of a file, so each encoder returns whatever is still outstanding once its file is closed. A hard cap (24 textures)
reclaims the least recently used texture if something is still never returned. In testing, the pool stays at 2 textures.

**Replay UI: WPF + MediaElement.** MediaElement plays H.264 MP4 through Windows' built-in decoders, supports
`SpeedRatio` (1×–16× here) and frame-accurate scrubbing (`ScrubbingEnabled`). Tauri was not used: it would add
a Rust + WebView2 toolchain and a second UI stack for no gain in playback reliability.

## Recording pipeline (per display)

```
WGC frame pool ──FrameArrived──► MonitorCapture (keeps only the newest frame)
                                      │ copy into a pooled texture (reference counted)
                                      ▼
                     SegmentEncoder.SampleRequested (≤ fps, grid-aligned timestamps)
                                      │
                       MediaStreamSource ──► MediaTranscoder (HW H.264) ──► HH-mm-ss_<id>.mp4.part
```

- **Pacing:** the encoder takes one picture per 1/fps slot. If the screen changed several times in between,
  only the newest frame is used. If nothing changed, the previous picture is reused (no capture or copy work).
- **Segments:** boundaries are aligned to the wall clock (default every 5 min: :00, :05, …). At a boundary the old
  encoder repeats its last frame up to the boundary and closes; the new one starts from the same picture at the
  same timestamp, so segments are contiguous (verified: end of one = start of the next, to the millisecond).
- **Timestamps:** frames carry QPC time (`SystemRelativeTime`). It's converted to UTC once per segment start and
  stop. The database stores UTC Unix milliseconds; only the UI converts to local time.
- **Multiple monitors:** one pipeline per monitor (own D3D device, capture session and encoder), all rotating at
  the same boundaries. Monitor ids are a hash of the monitor's device interface path, stable across reboots.
- **Thumbnails:** every 30 s a 320-px JPEG is made from a GPU staging copy, downscaled on the CPU and saved with
  System.Drawing. No full-size bitmap is allocated, so memory doesn't depend on garbage collection.
- **Activity:** each segment stores how many real (changed) frames arrived in each 10-second bucket. The viewer
  shades idle buckets and can skip them.

## Segment lifecycle and crash safety

1. A `segments` row is inserted with `status = recording` **before** encoding starts.
2. The encoder writes `<name>.mp4.part`.
3. On close: the MP4 structure check passes (ftyp + moov + media data, no truncated boxes) → rename to `.mp4`
   → update the row to `finalized` with exact start/end/duration/size/activity.
4. If the check fails → `corrupt`, with the reason in `error`.

At startup, recovery resolves anything still marked `recording`:
- the file is complete (crash between rename and DB update) → `finalized`;
- the file is cut off (no `moov`, since MP4 keeps its index at the end) → `lost`. The partial file is deleted and the
  row keeps the time range; a `crash` gap covers the lost segment plus the downtime. The crash time is taken
  from the partial file's last write (or the 5-second heartbeat).

Finalized segments are never modified by recovery. Worst case, a crash loses the current segment
(≤ `segmentSeconds`, default 5 min). Shorter segments reduce that loss at the cost of more files.

`RegisterApplicationRestart` asks Windows to relaunch the recorder after a crash (if it ran ≥ 60 s).

## State machine

Recording runs only when none of these is true. The first one that applies is recorded as the gap reason:

| Condition | Source | Gap reason |
|---|---|---|
| user paused (tray, hotkey `Ctrl+Alt+Shift+P`, `--pause`, timed pause) | user | `paused` |
| system suspending | `SystemEvents.PowerModeChanged` | `sleep` |
| session locked / remote disconnect | `SystemEvents.SessionSwitch` | `locked` |
| console display off | `RegisterPowerSettingNotification(GUID_CONSOLE_DISPLAY_STATE)` | `display-off` |
| free disk < `minFreeDiskGB` (after retention) | 30 s check | `disk-full` |

Other events:
- **Display change** (add/remove/resolution): all files are closed and capture restarts after 2.5 s.
- **Monitor unplugged mid-segment:** WGC `Closed` → that file is closed at the current time.
- **Modern Standby:** Windows sends no suspend event, and the process is simply frozen. Two safeguards, both
  added after a real overnight standby during testing:
  - the display-off notification pauses recording before standby;
  - if the 5-second heartbeat sees a gap > 30 s, or the encoder finds itself > 90 s behind its frame clock, the
    segment is closed at its last frame (not stretched across the sleep) and a `sleep` gap is recorded.
- **Privacy blanking:** while an excluded process or window title is in the foreground, or after
  `--blank` / tray → "Black out video", frames are replaced with black, and an `excluded` gap with the reason
  is recorded. Blanking starts immediately; it ends only after 750 ms with nothing excluded in front, because
  focus flickers while windows open.
- **Encoder failure:** the display's pipeline restarts with backoff; after repeated hardware failures, software encoding is used.

## Storage

```
<data dir>/                       default %LOCALAPPDATA%\LifeReplay (override: --data-dir or LIFEREPLAY_DATA)
  config.json                     settings (reloaded automatically when saved)
  status.json                     live recorder status for the viewer
  database/lifereplay.db          SQLite metadata (WAL)
  recordings/YYYY-MM-DD/windows/<displayId>/HH-mm-ss_<segment>.mp4
  recordings/YYYY-MM-DD/windows/<displayId>/thumbs/<segment>_<offsetSec>.jpg
  recordings/YYYY-MM-DD/iphone/<deviceId8>/HH-mm-ss_<segment>.mp4
  logs/recorder-YYYYMMDD.log, viewer-YYYYMMDD.log   (kept 14 days)
  sync/pairing.json, certificate-thumbprint.txt
```
Dates and times in paths are local; database timestamps are UTC.

### Database schema (v1)

`devices(id, name, platform, created_utc, last_seen_utc)` ·
`displays(id, device_id, name, width, height, left_px, top_px, is_primary, first_seen_utc, last_seen_utc)` ·
`sessions(id, device_id, start_utc, end_utc, status, end_reason, app_version)` ·
`segments(id, session_id, device_id, display_id, start_utc, end_utc, duration_ms, file_path, file_size, width, height,
source_width, source_height, fps, bitrate, encoder, status, frame_count, activity, error, created_utc, finalized_utc)` ·
`gaps(id, device_id, display_id, start_utc, end_utc, reason, detail)` ·
`thumbnails(segment_id, ts_utc, file_path)`.

Migrations are forward-only, tracked in `PRAGMA user_version`, one transaction each (`Core/Migrations.cs`).
Foreign keys are enforced. Multi-row changes (deletes, ingest) are transactional.

### Retention

Every 10 minutes and at startup: delete segments older than `retentionDays`, then the oldest segments until the
total is under `maxStorageGB`. Segments being recorded are never deleted. Files go first, then rows; a file that
can't be deleted (e.g. open in the viewer) keeps its row and is retried later.

### Storage estimate

The hardware encoder runs at a constant bitrate, so usage is predictable:
`bytes/hour/display ≈ bitrate / 8 × 3600`, with `bitrate = width × height × fps × bpp`
(bpp: low 0.04, medium 0.07, high 0.12), clamped to 0.3–50 Mbit/s.

| Output (per display) | fps | low | medium (default) | high |
|---|---|---|---|---|
| 1280×720 | 10 | 166 MB/h | 290 MB/h | 498 MB/h |
| 1728×1080 (1920×1200 scaled) | 10 | 336 MB/h | **588 MB/h** (560 MiB, measured) | 1.0 GB/h |
| 1920×1080 | 10 | 373 MB/h | 653 MB/h | 1.1 GB/h |
| 1920×1080 | 5 | 187 MB/h | 327 MB/h | 560 MB/h |
| 1920×1080 | 15 | 560 MB/h | 980 MB/h | 1.7 GB/h |
| 2560×1440 → 1080p | 10 | 373 MB/h | 653 MB/h | 1.1 GB/h |

(MB = 10⁶ bytes; the app displays binary units, so 588 MB shows as 560 MB.)
Example: one 1920×1200 monitor, defaults, 10 h/day ≈ 5.5 GiB/day, so the default 100 GiB quota holds about 18 days.
The tray menu and viewer show the live estimate and the actual total.
