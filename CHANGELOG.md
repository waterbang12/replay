# Changelog

Each entry says what changed and why it was needed.

## Planned (not started): ideas and open issues

### Reminders away from the PC: phone push or a small desk device
*Why:* coach notifications currently only appear on the PC. Being late or off schedule matters most when you're not
at it.
- **What's already there:** the trigger. `InterventionPolicy` already decides when to remind (behind, drift,
  overcommitted; quiet hours, cooldown, daily maximum, snooze), with no model call. This only needs new *output
  channels*.
- **Phone options:**
  - **Telegram bot.** Simplest and reliable on iPhone; free.
  - **ntfy app.** On iOS its push notifications go through ntfy.sh's servers even when you host it yourself.
  - **Pushover.** Paid once.
  - Each sends a short text ("Behind by 40 min on t2"). That text passes through the service, so task titles should
    be redacted by default.
- **Desk device options:**
  - an ESP32 with an LED ring, small e-ink screen or buzzer: green = on track, yellow = not enough evidence,
    red = behind;
  - fed over USB serial from the coach tray (no network, nothing to secure), or polled over the home Wi-Fi with a
    token;
  - a smartwatch comes for free through the phone channel.
- **Open issues:**
  - When the PC is off or asleep, nothing evaluates, so no reminders. A phone-only fallback would need the plan
    synced to the phone (e.g. calendar alarms created after approval).
  - A device must never become a way to read activity data. It only receives a state and a short message.
  - Avoid duplicate alerts on PC and phone.
  - Measure whether reminders work (the outcome is already logged per notification) and turn off a channel that
    doesn't help.

### GPS and a "race replay" of the day
*Why:* to see the whole day (where you were, how you moved, what was on screen at each point) as one replay, like
the replay of a race in a racing game.
- **Location source:**
  - Shortcuts can't log location continuously. It only runs on events like arriving at or leaving a place, or at
    set times.
  - Open-source iOS loggers can POST batches of locations to an HTTP endpoint: **Overland** (by Aaron Parecki) and
    **OwnTracks** (HTTP mode). That fits the existing iPhone endpoint and token: a new `POST /api/v1/locations`
    with the same validation and dedupe.
  - Google Maps Timeline can be exported as JSON and imported afterwards.
- **Replay view:**
  - a map with the day's path drawn progressively in sync with the timeline playhead;
  - a moving marker with speed, places (home, school, café) clustered from stays, and the active app or screen
    thumbnail at that moment;
  - play / 4× / 16× like the video;
  - "ghost" comparison with another day.

  A **map-free mode** (a place graph: home → campus → library, with durations) needs no map tiles and shows less.
- **Open issues:**
  - **Privacy:** location is the most sensitive data here. Keep it local only, never send it to the coach model, and
    give it its own retention setting. Excluded zones (e.g. home) should be stored as a place name only.
  - **Map tiles:** OpenStreetMap's tile servers have a usage policy (no heavy use, attribution required). Offline
    tiles (MBTiles) would avoid that but take disk space.
  - **Battery and accuracy:** continuous GPS drains the phone; loggers trade accuracy for battery. Location is poor
    indoors and between buildings.
  - **Time alignment:** phone and PC clocks; the existing clock-skew handling would apply.
  - **Not tested:** none of this has been tried on a device. Overland and OwnTracks behaviour would need the same
    kind of real-device test as the Shortcuts integration.

## 0.5.1 — 2026-10-31

- **Topics: Progress / Not progress / Unsorted instead of YouTube / Social / Other.** *Why:* "YouTube 3h" said
  nothing about whether the time moved anything forward, and the list was too long to read.
  - Rules first: code editors, terminals, PDFs, GitHub, pwn.college, arXiv are progress. Videos, games, chat and
    streaming sites are not. A YouTube video counts as progress only when its title says lecture, tutorial,
    강의 and similar.
  - **Move to** on a topic, or right-clicking a window, saves your choice (new table `topic_kinds`, schema v6).
    Windows at least 0.80 similar to one you sorted follow your choice before the rules.
  - Sections and topics are collapsible and closed by default. They stay open or closed while the viewer runs.
  - The coach's "drift" time now uses the same sorting, so game videos count and lectures don't.
- Tests: 165 (rules, your choices beating rules, similar windows following them, saving the choices).
- Known issue: `CoachServiceTests.ValidAnswerIsStoredWithItsRequestAndAuditPayload` failed once in about 7 runs
  around midnight and passed on every rerun. Not investigated yet.

## 0.5.0 — 2026-10-29

### Added
- **Coach: deterministic schedule evaluation** (`LifeReplay.Coach`): on_track / ahead / behind /
  insufficient_evidence per task and per day, computed only from verified outputs (ticked tasks,
  `progress: x/y`, and commits through a new `repo:` line). It also gives a confidence, warns when the plan
  doesn't fit the time left, and shows YouTube/social drift and goals (`goal: … by yyyy-mm-dd`).
  *Why:* the Plan view's ahead/behind counted time in apps. Time isn't progress, and the coach is meant to track
  what was actually done.
- **Optional AI suggestions** (morning plan, check-in, end-of-day review) over the OpenAI Responses API with
  strict structured output. Tested with BAZE: `gpt-6-astra` cost about 28–31 credits per request (5 of them for
  BAZE's masking pass), so the default model is `gpt-5.4-mini`, which was faster and passed validation the same way.
  - **Before sending:** a preview of the exact payload, and nothing is sent without a click.
  - **Checks on the answer:** cited evidence must exist, every number must appear in the data, a counterpoint is
    required, and actions are limited. A failing answer gets one retry with the reasons, then the rule-based
    fallback.
  - **Limits:** timeouts, retries only for network/5xx/429, and monthly and daily caps in credits.
  - **Logging:** every payload is stored in the audit log.

  *Why:* to catch made-up claims cheaply in code rather than with more model calls, to counter the model simply
  agreeing with you, and to keep spending predictable.
- **Sanitized context**:
  - never sent: video, screenshots, window titles, URLs or paths;
  - removed from task titles: URLs, e-mail addresses, CTF flags, credentials, paths and key-like tokens;
  - optional: redacting task titles, and sending commit messages (off by default).

  *Why:* a public repo and a personal activity log; only the minimum should leave the PC, and only on request.
- **Notifications** from a separate `LifeReplay.CoachTray.exe`:
  - behind, drift and overcommitted triggers;
  - quiet hours, cooldown, a daily maximum, snooze, "quiet for today", and dismissed tasks not named again.

  *Why:* reminders must not depend on a model, and a coach crash must never touch the recorder.
- **Outcomes and calibration:** an hour after a "focus on X" suggestion, the window log shows whether you did.
  The planned vs. actual time of finished tasks gives your estimate ratio (14 days).
  *Why:* to judge the coach by results, and to plan with your real pace.
- **Private ledger** of finished work with its evidence and a "show" switch, off by default.
  *Why:* the basis for showing what you can do, without publishing anything by accident.
- **ChatGPT copy/paste bridge:** copy a context pack, refine the plan in the ChatGPT website (no API credits),
  then paste the validated plan block back into the todo list after a click, with a backup.
- **Schema v5:** `coach_requests`, `coach_interventions`, `coach_feedback`, `coach_outcomes`,
  `coach_evaluations`, `coach_ledger`.
- **Tests:** 54 coach tests, 155 in total.

## 0.4.0 — 2026-10-29

### Removed
- **The standalone iPhone app** (`ios/`: ReplayKit broadcast extension, segment upload, `docs/ios.md`, the
  segment-metadata schema) and the segment/thumbnail/gap upload endpoints with their ingest code.
  *Why:* it was never compiled (it needs a Mac and a developer account), and recording the phone's screen wasn't the
  goal. Knowing which phone apps take time was. It also kept an upload API open that nothing used.

### Added
- **iPhone app-usage tracking through Apple Shortcuts.** Personal automations ("App is opened / is closed") call a
  shared "LifeReplay Log" shortcut that POSTs `{device_id, source, app_name, event_type, timestamp, event_id}` to
  the recorder. Setup: docs/iphone-shortcuts.md.
  *Why:* Shortcuts is the only built-in way to react to app opens without an App Store app or Screen Time access.
- **`POST /api/v1/app-events`** accepts one event, an array, or JSON Lines. Every event is validated (device id
  charset, event type, ISO 8601 timestamp with offset, not in the future or older than 30 days) and deduplicated by
  `(device, event_id)`. A missing id is derived from app, type and second.
  *Why:* Shortcuts can't retry in the background, so the shortcut keeps a queue file and resends all of it. The
  server must therefore accept batches and ignore repeats. Lines are checked one by one so a bad line can't block
  the rest.
- **Schema v4: `app_events` table**, cleaned up with the same retention as recordings.
- **Session rebuilding** (`AppSessions`): deterministic rules for missing closes, missing opens, double triggers,
  duplicate opens, out-of-order delivery, cross-midnight sessions and phone clock skew. Every session carries a
  confidence: Complete, EndInferred, StartInferred or Unknown. Unknown sessions are never added to totals.
  *Why:* automations sometimes don't fire, and the totals should say so rather than silently invent time. No language
  model is involved: this is plain rule-based code, unit-tested case by case.
- **Viewer:**
  - a 📱 iPhone apps timeline lane, colour per app, with inferred and missing-event markers and the last-sync time
    (⚠ after 12 h);
  - hover details: start, end, duration, source and confidence;
  - **Summary → iPhone**: per-app totals, a missing-events list, and Windows + iPhone combined (including overlap).
  *Why:* to see phone time in the same place as PC time for the same day.

### Changed
- **The endpoint listens on 127.0.0.1 by default** and is reached through **Tailscale Serve**
  (`tailscale serve --bg https+insecure://127.0.0.1:47800`).
  *Why:* Shortcuts rejects self-signed certificates. Tailscale gives a trusted certificate that only your own devices
  can reach, and nothing is exposed to the LAN or the internet.
- **"Set up iPhone" window** shows the endpoint URL (read from Tailscale) and the `Bearer` token with Copy buttons,
  plus a QR code of the token. Tray menu renamed to "iPhone app tracking".

### Known issues: iPhone integration not done
The Windows side is implemented and tested. The iPhone side has **not been set up or tested on a real device**.
Until it is, the iPhone lane only contains test data sent with `curl`.
- Not done:
  - Tailscale on the PC (MagicDNS + HTTPS, `tailscale serve`) and on the iPhone;
  - the "LifeReplay Log" shortcut and the per-app automations;
  - the real-device test from docs/iphone-shortcuts.md, including offline resend;
  - a comparison of one day against Screen Time.
- Unknown until tested on a device:
  - whether "Is Closed" fires on lock or when switching through the app switcher;
  - whether "Append to Text File" exists on the current iOS, and whether the Korean action names match;
  - whether a failed request (PC off) shows a notification on every run;
  - whether a multi-app automation can tell which app triggered it.

## 0.3.1 — 2026-10-29

### Added
- **Fixed groups in Topics: YouTube, Social, Other.** YouTube and social/messaging windows are always counted in
  their own groups (decided from the app and window title); everything else is "Other", sorted by topic as before.
  Totals for the three are shown at the top.
  *Why:* feedback on time that isn't part of the day's work, while keeping the rest topic-oriented: study and work
  are interchangeable here, so they aren't split into fixed buckets.

## 0.3.0 — 2026-10-29

### Added
- **Topics view**: the day's windows grouped automatically by meaning with a small multilingual language
  model (paraphrase-multilingual-MiniLM-L12-v2, int8 ONNX, CPU only), via average-linkage clustering.
  *Why:* work varies day to day (coursework PDFs, coding, random papers on topology or domain theory), so
  you can't define keyword rules in advance. Grouping by meaning shows what you actually did with no setup,
  and works across Korean and English titles.
- **Plan view with a Markdown todo list** (`- [ ] task ~1h`, optional `match:` / `progress:` / `hours:`):
  time per task, time left (from your pace when progress is given), and ahead/behind schedule for today.
  *Why:* to know during the day whether the plan is on track, not only in hindsight.
- **Matching in layers**: your manual choices → keywords → learned from your choices → meaning → context
  (a window between two windows of the same task). Unmatched windows get an "Assign to…" box.
  *Why:* rules alone miss varied work, and meaning alone mislabels context-free windows such as a bare
  terminal; each layer covers the others' blind spots. Thresholds were calibrated on real titles (correct
  tasks scored 0.41–0.90; unrelated titles stayed below about 0.37).
- **Embedding cache and manual labels** (database schema v3), so each title goes through the model only once.
- **Pinned model download** (exact revision + SHA-256) into `<data dir>\models`.

### Fixed
- Adding the app name ("… (Google Chrome)") to every title made all tabs of one browser look alike and
  pulled unrelated tabs into one topic; it's now added only to short generic titles ("Inbox", "YouTube").

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
