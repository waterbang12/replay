# LifeReplay for iPhone

> **Status: written, not built or tested.** This repository was developed on Windows without a Mac, Xcode or
> an Apple developer account, so the iOS code has never been compiled or run. Expect to fix small compile
> errors on first build. The Windows side of the iPhone path (upload endpoint, ingest, timeline) *is* tested:
> 8 automated ingest/pairing/HTTPS tests plus an end-to-end upload against the real recorder, using `curl` in place of the phone.

## What it does

- A **ReplayKit Broadcast Upload Extension** (`ios/BroadcastExtension`) receives screen frames while you broadcast.
  It writes 5-minute H.264 MP4 segments (hardware encoder via AVAssetWriter, ≤ 10 fps, long side ≤ 1280 px,
  no audio) plus a JSON sidecar per segment and a thumbnail every 30 s into a shared App Group folder.
- The **app** (`ios/LifeReplay`) shows whether a broadcast is running, lists segments, and syncs them to the paired PC
  over Wi‑Fi (HTTPS with certificate pinning; see `shared/sync-protocol.md`). By default it deletes each segment
  from the phone after a successful upload.
- Pause/resume and unexpected ends are reported to the PC as timeline gaps.

## Build

Requirements: a Mac with Xcode 15+, [XcodeGen](https://github.com/yonaskolb/XcodeGen), an Apple developer team,
and an iPhone on iOS 16+.

1. Pick an App Group id you own, e.g. `group.com.yourname.lifereplay`, and put it in:
   - `ios/Shared/Shared.swift` → `AppGroup.appGroupId`
   - `ios/LifeReplay/LifeReplay.entitlements` and `ios/BroadcastExtension/BroadcastExtension.entitlements`
2. In `ios/project.yml`, set `DEVELOPMENT_TEAM` and change `com.example.lifereplay` to your bundle id prefix.
   The extension's id must be `<app bundle id>.BroadcastExtension` (the app's picker uses that name).
3. `cd ios && xcodegen generate && open LifeReplay.xcodeproj`
4. In Signing & Capabilities, enable the App Group for both targets, then run on a device.
   ReplayKit broadcasts don't work in the Simulator.

## Use

1. On the PC: tray icon → **iPhone sync** → **Enable iPhone sync**. Allow LifeReplay through Windows Firewall
   on private networks only. Then **Pair an iPhone…** to show the QR code.
2. On the iPhone: scan the QR code with the Camera app, open LifeReplay, confirm, and allow Local Network access.
3. Tap the record button in the app (or long-press Screen Recording in Control Center → LifeReplay → Start Broadcast).
4. Open the app on the same Wi‑Fi to sync. Recordings appear in the PC viewer in an "iPhone" lane.

## iOS limitations (by design of iOS — not fixable with public APIs)

LifeReplay uses only public APIs: no private frameworks, MDM tricks or jailbreak techniques.

| Limitation | Effect |
|---|---|
| **Recording must be started by the user, every time.** No app can start a screen broadcast on its own, at login, after a reboot, or on a schedule. | There is no "always on" mode. Expect gaps whenever you forget to start it. |
| **iOS shows a recording indicator** (red status pill / Dynamic Island) for the whole broadcast. | There is no stealth recording on iPhone (LifeReplay doesn't want one anyway). |
| **The extension can be terminated at any time**, e.g. when it exceeds its ~50 MB memory limit, under memory pressure, or by system policy. | The current segment is cut off. Segments are fragmented MP4 (a fragment every 10 s), so the app trims the file to its last complete fragment, uploads it as "interrupted", and reports a gap. Up to ~10 s can be lost. |
| **The broadcast stops when you stop it** from the indicator or Control Center, and may stop or pause on some system events (e.g. device lock, calls, low power). Exact behavior varies by iOS version and has **not** been verified here. | Recorded as gaps on the PC timeline. |
| **Protected content is not captured**: DRM video (most streaming apps) appears black; apps can detect capture (`UIScreen.isCaptured`) and hide sensitive screens (banking, password managers, some messaging apps). | Those parts look black or blank in the replay. |
| **Background upload is limited.** Sync runs while the app is in the foreground on Wi‑Fi. The extension itself doesn't upload (to stay under its memory limit). | Open the app now and then (e.g. in the evening) to sync. Segments wait on the phone until then. |
| **Time sync**: frame times come from the phone clock (ReplayKit host time converted to wall-clock time). | iOS keeps the clock NTP-synced, usually within well under a second of the PC. |
| **No audio** is recorded (microphone button hidden; app-audio buffers ignored). | — |

## Not implemented on iOS (yet)

- Background uploads with `BGProcessingTask` / background `URLSession`.
- On-phone replay. Replay happens in the Windows viewer.
- Automatic retention on the phone beyond "delete after upload" and manual swipe-to-delete.
