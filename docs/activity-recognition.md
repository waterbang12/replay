# Recognizing what you were doing (design notes, not implemented)

Goal: turn a day of recordings into a summary such as

> **pwn.college** — 1 h 42 m (13:05–14:47), *Program Misuse / privileged mode* · representative frame
> **RL study (ChatGPT, notes)** — 55 m · representative frame
> **YouTube** — 20 m

Each entry needs three things: **what** (a topic label), **how long** (start/end/duration) and **a
representative image**. Below are the ways to get each, cheapest and most reliable first.

## 1. Signals available

| Signal | How | Cost | Strength | Weakness |
|---|---|---|---|---|
| **Foreground window title + process name** | `GetForegroundWindow` + `GetWindowText` every 1–5 s (the recorder already does this for privacy exclusions) | ~0 | Exact app and usually the site/document: Chrome titles read "pwn.college – …", VS Code shows the file | Fails inside a VM, remote desktop or full-screen video; browser titles can be generic |
| **Browser URL** | UI Automation on the address bar, or a tiny browser extension | low | `pwn.college/program-misuse/...` gives the topic directly | Per-browser work |
| **Screen-change activity** (already recorded) | Frames per 10 s from Windows.Graphics.Capture | 0 | Separates active, idle and video playback | Says nothing about *what* |
| **OCR text** ("letter detection") | Windows.Media.Ocr (built in, offline, supports Korean and English) or Tesseract on selected frames | 50–300 ms per 1080p frame | Reads headings, code, terminal output, slide titles; works inside VMs | Needs frame selection to stay cheap; noisy on small fonts |
| **Visual features** (SIFT / ORB / AKAZE) | Keypoints + descriptors on frames; match against reference images | 20–200 ms per frame | Recognizes logos and fixed UI (pwn.college banner, IDE layout) at any position or scale | Needs references per site; weak on plain text |
| **Global image descriptors** (perceptual hash, color histogram, HOG, GIST) | Small fingerprint per frame | < 5 ms | Groups similar screens and detects when the scene changes | No semantics |

## 2. Approaches

### A. Metadata first (recommended starting point)
Log `(time, process, window title, URL)` whenever the foreground window or its title changes, into a new
`focus_events` table. Then:
- **Rules** you define map patterns to topics: `title ~ /pwn\.college/` → "pwn.college", `process = Code.exe && title ~ /rl_/` → "RL project".
- **Duration** = sum of focused time per topic, ignoring idle buckets (no input and no screen change for more than N minutes).
- **Smoothing**: merge runs separated by short interruptions (< 2 min on Discord/KakaoTalk) into one session.

This is very accurate for normal desktop use and costs almost nothing. It's the backbone; the image methods fill its gaps.

### B. OCR on key frames ("letter detection")
1. Pick frames worth reading: the first frame after each big change (perceptual-hash distance above a threshold), plus one per minute while active.
2. Classical text detection, optional: **MSER** or the **Stroke Width Transform** find text regions; **EAST/CRAFT** are the learned equivalents. Windows.Media.Ocr does detection and recognition together.
3. Turn the text into a topic:
   - a keyword dictionary per topic ("buffer overflow", "privilege", "/challenge" → pwn.college);
   - **TF-IDF** across the day's frames gives the distinctive words of each session (e.g. "ret2win", "GDB");
   - headings in large or bold text (bounding-box height) are strong title candidates.
4. Store the text per frame. That also makes the history searchable ("when did I read about SUID?").

### C. Template / feature matching (SIFT, ORB)
For things you want to recognize visually regardless of window title (e.g. a pwn.college workspace inside a VM):
- Keep a few **reference crops** (the pwn.college logo, the dojo sidebar, a specific IDE layout).
- On key frames, detect **SIFT** (patent-free since 2020, in OpenCV) or the faster **ORB/AKAZE** keypoints,
  match with a ratio test, verify with **RANSAC homography**. Enough inliers = present.
- **Normalized cross-correlation template matching** is faster for fixed-size UI elements, but not scale-invariant.
- **Bag of visual words**: cluster SIFT descriptors into a vocabulary and describe each frame as a word histogram
  (or VLAD). Frames from the same site or app cluster together without labels; you name the clusters once.

### D. Segment the day, then label segments
Instead of labeling individual frames:
1. **Change-point detection** on a per-frame feature sequence (perceptual hash, histogram, active window) cuts the
   day into *episodes*.
2. **Cluster** episodes (k-means or agglomerative on the averaged descriptors plus OCR keywords plus window title).
3. Label each cluster once by rule or by hand; new episodes join the nearest cluster.
4. Durations come from episode boundaries. A **minimum-duration / hidden semi-Markov** smoothing step removes flicker.

### E. Layout heuristics (cheap classifiers)
Without any learning, simple image statistics separate broad activity types:
- **Terminal**: dark background, monospace text-line pattern (regular horizontal projections), little color.
- **Reading or docs**: light background, dense text lines, low frame change rate.
- **Video**: high, steady frame change rate in one rectangular region.
- **Coding**: syntax-highlight color distribution plus line-number column.

These make good fallback labels ("terminal work, 40 m") when nothing more specific matches.

## 3. Representative image per session
- **Medoid frame**: the frame whose descriptor is closest to the session's average (the most typical view).
- **Most informative frame**: the one with the most OCR text or highest edge density (sharp, content-rich), avoiding frames mid-scroll (high change).
- **Mosaic**: 3–4 frames at the session's main sub-scene changes for long sessions.
- The recorder already saves 320-px thumbnails every 30 s; the representative frame can be decoded from the MP4 at full resolution (`--export-frame`).

## 4. Recommended plan
1. **Recorder**: log focus events (process, title, URL) to SQLite, with no image work during recording, so no extra lag.
2. **Analyzer**, run on demand from the viewer ("Summarize this day") or overnight: perceptual-hash
   segmentation → OCR on key frames → rules + TF-IDF topics → representative frames → summary written to a
   `sessions_summary` table and shown as labeled bands above the timeline.
3. Add SIFT/ORB template matching only where titles and OCR aren't enough (VMs, remote desktop).
4. Keep it local and private: text and titles stay in the local database, and excluded apps (already blanked) are never analyzed.

Cost estimate for the analyzer: about 1 key frame per 20–60 s of active time → 300–1000 frames per 10-hour day →
1–5 minutes of CPU with Windows OCR, run in the background at low priority.

**Language:** the analyzer can be C# (Windows.Media.Ocr and OpenCvSharp are both available) to stay in one codebase,
or Python + OpenCV + Tesseract as a separate tool if you want to experiment faster. It only needs to read the
MP4 files and the SQLite database.
