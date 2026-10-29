# Test results

Test machine: Windows 11 Home 10.0.26200, Intel Arc integrated GPU (hardware H.264 via Media Foundation),
one 1920×1200 monitor, .NET 10.0.403 SDK. All results below are from real runs on this machine on 2026-10-27.
Nothing here is simulated, except that `curl` stood in for the iPhone.

## Automated tests: 101 passed, 0 failed (2026-10-29)

`dotnet test desktop\tests\LifeReplay.Tests` (also run by `build.ps1`). The table below lists the original areas
plus the iPhone app-event tests; the topic/plan analysis tests are described in the CHANGELOG.

| Area | Tests |
|---|---|
| Migrations: fresh DB, idempotent, WAL | 3 |
| Metadata store: range queries, updates, gaps, transactional deletes | 4 |
| MP4 integrity: valid, fragmented (iPhone), no moov, truncated, garbage, wrong first box | 6 |
| Time: wall-clock segment boundaries, DST days (23 h / 25 h), local↔UTC | 6 |
| Encoding plan: scaling, even sizes, bitrate, storage estimate | 4 |
| Config: defaults, broken file fallback, clamping | 3 |
| Retention: age, quota, never deletes the live segment, files + rows removed | 4 |
| Crash recovery: lost segment + gap + session, completed-but-unrecorded file, idempotent | 3 |
| Timeline: segment lookup, gaps, lost not playable, inactive skipping, multi-screen, lane order | 6 |
| iPhone app events: timestamp normalization (offsets, ms, `+0900`), no-offset rejected, object/array/JSON Lines, dedupe by id and by derived id, per-line errors (bad device id, type, future, too old, not JSON), Shortcuts wording | 12 |
| iPhone sessions: normal + switching, out-of-order input = same result, close/open in the same second, missing close (near → inferred, far → unknown), missing open, double trigger + duplicate open, still open, cross-midnight split, known/inferred/unknown totals, per device, phone clock ahead, overlap with Windows | 13 |
| Pairing tokens (hash only, revoke) | 1 |
| HTTPS server: 401 without/with wrong token, event stored, queue resend → 1 new + 1 duplicate, all-invalid → 400 with reason; client rejects unpinned cert | 2 |

## End-to-end tests on the real desktop

### 30-minute acceptance run (Phase 1 criteria)
`LifeReplay.exe --record-for 1800` with default settings (5-minute segments, 10 fps, 1080p, hardware encoder),
then `--verify --deep`.

Final build, started 11:45. The PC turned its display off and went into Modern Standby at 11:59, then woke at
16:11. That gave a real standby test in the middle of the run. Recording time adds up to ~30 minutes
(14 min before standby, 14 min after).

| Metric | Result |
|---|---|
| Segments | 8, all `finalized`, all decode (`--verify --deep`: "All segments OK") |
| Boundaries | Wall-clock aligned (11:50:00.008, 11:55:00.028, 16:15:00.014, 16:20:00.000, 16:25:00.012); each segment ends exactly where the next starts |
| File time vs clock | 300.0 s segments are 300.0–300.1 s long in the file |
| Size | 46.6 MB per 5-minute segment (1.31 Mbit/s, hardware encoder, constant bitrate) ≈ 560 MB/hour |
| CPU (recorder process, share of the whole machine) | average 0.33 %, max 0.57 % (59 samples, 30 s apart) |
| Memory (private bytes) | 182 MB at start, peak 224 MB, 183 MB at the end: no growth |
| GPU texture pool | 2–6 textures |
| Standby | Display off at 11:59:24 → recording paused, file closed cleanly at 11:59:24.555. The lock was recorded as a `locked` gap until 16:11:29. On wake, the heartbeat detected the time jump, recorded a `sleep` gap, and restarted capture with fresh segments. Nothing was lost or stretched across the sleep. |
| Known cosmetic issue | The ~17 s between the display-off pause and the lock event (11:59:24–11:59:41) has no reason attached, so the timeline shows it as a plain gap. |

### Earlier runs that found bugs (all fixed)

| Finding | How it was found | Fix |
|---|---|---|
| File duration drifted up to 11% from real time (e.g. 16.7 s for 15.0 s) | `--verify` + parsing the MP4 `stts` table | Frames placed on the fixed frame grid |
| On static screens Media Foundation filled gaps with the **next** frame, so content appeared early and privacy blanking was wrong | Decoded frames around a blanking window | One sample per frame slot (previous picture repeated) |
| Memory grew ~10 MB/min | 30-min run with CPU/RAM sampling | Thumbnails from a GPU staging copy (no full-size bitmaps); per-segment GC; textures Windows never released are returned when the file closes; hard pool cap |
| Overnight Modern Standby: no suspend event; on wake the encoder spent minutes filling 3 h of frames and the segment was lost | The PC went to sleep for 3 h 20 m during a test run | Display-off pause, heartbeat time-jump detection, segment closed at its last frame when the clock jumps |
| Sync: malformed JSON returned 500 | curl test | Returns 400 |
| Viewer: dropdown text unreadable; Delete menu not keyboard/automation accessible | Screenshots / UI Automation | Style fix; standard Menu control |

### Feature checks

| Check | Result |
|---|---|
| Segment rotation | Contiguous: each segment's end equals the next one's start to the millisecond (wall-clock aligned :00/:05/...). |
| File time vs clock | Within 0.1 s per segment (`--verify` tolerance = 1 frame + 250 ms). |
| Playable files | Every finalized file decodes (`--verify --deep` extracts a frame from the middle of each file). |
| Crash recovery | Recorder killed (`Stop-Process -Force`) 2 s into a segment: the 3 earlier segments stayed intact; the cut-off one was marked `lost` with an estimated end 0.15 s before the real kill time; a `crash` gap was recorded and recording resumed on restart. |
| Restart keeps history | The viewer shows recordings from before and after the restart on the same timeline. |
| Tray mode + command channel | `--pause` → status `paused`, `paused` gap; `--resume` → new session; `--shutdown` → files closed, `stopped` gap (closed on next start). |
| Privacy blanking | A window titled "…InPrivate…" triggered blanking within milliseconds (it never appeared in the video). `--blank`/`--unblank`: decoded frames are black exactly inside the 8.9–17.1 s window (content at 8.8 s and 17.2 s). |
| Viewer | Date selection, timeline with gaps and reasons, jump to time, 1×/4×/16× (16× for 3 s advanced 48 s across a segment boundary with video showing), skip inactive (2.5 s of playback skipped 22 s of idle time), delete segment through the UI (file and rows removed). Driven with UI Automation plus window screenshots. |
| iPhone app events (Windows side, 2026-10-29) | Debug recorder with its own data folder, tracking enabled on 127.0.0.1:47811. Fresh DB migrated to v4. With `curl.exe` as the phone: no token → 401, wrong token → 401, `/hello` → 200; the spec's example event → `accepted 1`; a 9-line JSON Lines queue (out of order, one repeated event, one missing close, one timestamp without offset) → `7 new, 1 duplicate, 1 rejected` with the reason; the same queue again → `0 new, 8 duplicate`; a 3 MB body → 413. Each request logged. Viewer on that folder: iPhone lane with solid, hatched-inferred and unknown markers, sync status "synced 15:11"; Summary → iPhone shows per-app totals (YouTube marked inferred, Safari "+1 incomplete"), 2 missing-event sessions, and the combined block. Hover popup not captured (the viewer couldn't be brought to the front). |
| Software H.264 encoder | `--software-encoder` for 40 s: 3 valid, decodable segments (`mf-h264-sw`), about 1.9% CPU of the whole machine vs about 0.3% with the GPU encoder. |
| Self-contained publish | `dist\LifeReplay\LifeReplay.exe` records and verifies with no .NET installed system-wide. |

## Not tested here (and why)

| Item | Reason |
|---|---|
| Shortcuts automations on a real iPhone, Tailscale Serve, iOS network permission prompt, Screen Time comparison | No device test was done. Procedure: docs/iphone-shortcuts.md → "Test on a real device". |
| Multiple monitors, monitor hot-plug, resolution change during recording | Only one monitor attached. The code path is per-monitor (one pipeline per display) and handles `DisplaySettingsChanged` / WGC `Closed`. |
| Automatic switch to software after hardware failures | The hardware encoder never failed. The software path itself was tested by forcing it (see above). |
| Start with Windows, RegisterApplicationRestart | Would change the user's login items / need a real crash after 60 s. |
| Disk-full pause | Would need a nearly full disk. |
| Thumbnail hover popup in the timeline | Needs real mouse movement over the timeline. Thumbnails themselves are generated and verified on disk. |
| Tray menu clicks and the global hotkey | The same actions were driven through the command channel instead. |
