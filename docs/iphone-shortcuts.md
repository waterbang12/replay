# iPhone app tracking with Shortcuts

LifeReplay can log **which iPhone apps you open and close** and show that next to your Windows time. It uses
Apple's built-in Shortcuts app: a personal automation per app runs when the app opens or closes and sends one small
event to the recorder on your PC. Nothing is recorded on the phone's screen, and no App Store app is needed.

> **Status:** the PC side (endpoint, storage, session rebuilding, viewer) is implemented and tested with `curl`.
> The steps on the iPhone below were written from Apple's documentation and community reports and have **not been
> run on a real iPhone yet**. Shortcuts labels change between iOS versions; if a label differs, look for the
> closest equivalent. Please report what you find (see "Test on a real device").

## How it works

```
App opened ─► Automation "Instagram · opened" ─► Run shortcut "LifeReplay Log" {app, event}
                                                   1. build a JSON line with the current time
                                                   2. append it to LifeReplay/queue.txt (iCloud Drive › Shortcuts)
                                                   3. POST the whole queue to https://<pc>.<tailnet>.ts.net/api/v1/app-events
                                                   4. if the PC answered, delete the queue
```

If the PC is off or the phone is offline, step 3 fails, the queue file stays, and the next event sends everything
again. The PC ignores events it already has, so sending the same line twice is harmless.

## Why Tailscale

Shortcuts only talks to HTTPS servers with a **publicly trusted certificate**. It rejects LifeReplay's own
self-signed certificate, and installing a private root certificate on iOS is unreliable. Tailscale Serve gives the
PC a real certificate for `<pc>.<tailnet>.ts.net`, reachable **only by your own devices** (your tailnet). It works on
the same Wi-Fi, on mobile data, and on other networks. It is free for personal use.

Not supported on purpose: plain HTTP, port forwarding, `tailscale funnel` (that would put the endpoint on the
public internet).

## 1. Windows setup

In PowerShell:

```powershell
winget install --id Tailscale.Tailscale -e      # then sign in from the Tailscale tray icon
```

In the Tailscale admin console (https://login.tailscale.com/admin/dns):
- **MagicDNS**: on (it usually is by default).
- **HTTPS Certificates**: Enable.

Then:

```powershell
tailscale status                                         # this PC should be listed with a name
tailscale serve --bg https+insecure://127.0.0.1:47800    # forward https://<pc>.<tailnet>.ts.net → LifeReplay
tailscale serve status                                   # shows the https://….ts.net address
```

(`https+insecure` means "the local target uses HTTPS with a self-signed certificate". That hop never leaves the
PC. If `tailscale serve` says access is denied, run it once from an administrator terminal. `--bg` keeps it on
across restarts. `tailscale serve reset` removes it.)

In LifeReplay: tray icon → **iPhone app tracking** → **Enable iPhone app tracking**, then **Set up iPhone (new
token)…**. The window shows:
- the **URL** (`https://<pc>.<tailnet>.ts.net/api/v1/app-events`, read from Tailscale);
- the **Authorization value** (`Bearer <token>`), shown only this once;
- a QR code with the token.

Keep the window open for step 3.

Test from the PC (replace the URL and token):

```powershell
curl.exe -s https://<pc>.<tailnet>.ts.net/api/v1/hello -H "Authorization: Bearer <token>"
# → {"server":"<PC name>","apiVersion":2}
```

## 2. iPhone: install Tailscale

Install **Tailscale** from the App Store, sign in with the same account, and turn it on. To keep it connected,
use the **VPN On Demand** option in the Tailscale app's settings, if your version has it. Without it, events queue
up whenever Tailscale is off and are sent the next time it's on.

Check: Safari → `https://<pc>.<tailnet>.ts.net/api/v1/hello`. It should show a blank page or "401", not a
certificate warning. 401 is correct here, because Safari sends no token.

## 3. iPhone: the shared shortcut "LifeReplay Log"

Shortcuts app → **Shortcuts** tab → **+** → name it exactly `LifeReplay Log`. Add these actions in order (search for
each name in the action search bar):

1. **Get Dictionary Value**: Get **Value** for key `app` in **Shortcut Input**.
   Then **Set Variable**: name `App`, input **Dictionary Value**.
2. **Get Dictionary Value**: Get **Value** for key `event` in **Shortcut Input**.
   Then **Set Variable**: name `Event`.
3. **Current Date**, then **Format Date**: Date Format **ISO 8601**, turn **Include ISO 8601 Time** on.
   Then **Set Variable**: name `Stamp`. (The result looks like `2026-10-29T14:00:00+09:00`. The UTC offset is required.)
4. **Text**, with the variables inserted where shown (tap in the text, then pick the variable):
   ```
   {"device_id":"iphone","source":"ios_shortcuts","app_name":"[App]","event_type":"[Event]","timestamp":"[Stamp]","event_id":"[App]-[Event]-[Stamp]"}
   ```
   It must be one line. Don't use `"` in app names.
5. **Append to Text File**: append **Text** to file path `LifeReplay/queue.txt` (in the Shortcuts folder), with
   **Make New Line** on. If your iOS has no "Append to Text File", skip steps 5–6 and 8, and send **Text** in step 7
   instead. That version has no offline retry: events made while the PC is unreachable are lost.
6. **Get File from Folder**: folder **Shortcuts**, path `LifeReplay/queue.txt`, **Error If Not Found** off.
7. **Get Contents of URL**:
   - URL: the URL from the setup window
   - Method: **POST**
   - Headers: `Authorization` = `Bearer <token>` (paste the whole value from the setup window); `Content-Type` = `text/plain`
   - Request Body: **File** → the **File** from step 6
8. **If** **Contents of URL** **contains** `accepted`:
   - **Delete Files**: **File** (from step 6), with **Confirm Before Deleting** off.
   - **End If**

Test it: temporarily add a **Dictionary** action at the top (`app` = `Test`, `event` = `opened`) and pass it in, or
run it from an automation (step 4). On the first run iOS asks whether "LifeReplay Log" may connect to
`<pc>.<tailnet>.ts.net`. Tap **Always Allow**, otherwise background runs will fail. Remove the test Dictionary afterwards.

**The token is stored in this shortcut.** Don't share the shortcut. If the phone is lost, use tray → iPhone app
tracking → **Revoke all paired iPhones**.

## 4. iPhone: one automation per app and event

Shortcuts → **Automation** tab → **+** (or "New Automation") → **App**:

1. **Choose** → select one app, e.g. **Instagram**.
2. Tick **Is Opened** only.
3. Select **Run Immediately**, and turn **Notify When Run** off.
4. **Next** → **New Blank Automation** → add **Dictionary**: `app` = `Instagram`, `event` = `opened`.
5. Add **Run Shortcut** → **LifeReplay Log**, input **Dictionary**.

Repeat with **Is Closed** and `event` = `closed`, then for each app you care about (e.g. Instagram, KakaoTalk,
YouTube, Safari). That is 2 automations per app.

**Why not one automation for all apps?** You can pick several apps in a single "App" automation. But I found no
evidence that Shortcuts tells the automation *which* app triggered it; there is no "triggering app" variable.
So the app name must be written into each automation. If you find a way on your iOS version, one automation per
event type would be enough: the server only needs `app_name`.

`.shortcut` files: importing one requires a file signed by Apple's tooling (on macOS, `shortcuts sign`) or an iCloud
share link. This repository can't provide a signed file, so the shortcut has to be built by hand as above.

## 5. Look at it

Viewer → the **📱 iPhone apps** lane appears under the screens once the first event arrives. Use **Summary → iPhone**
for per-app totals, missing events, and "Windows + iPhone" combined. The lane label shows the last sync time, with a
⚠ after 12 h without events.

## Test on a real device

1. Set up two apps (4 automations), e.g. Instagram and KakaoTalk.
2. Write down the exact times you do this:
   1. open Instagram (~2 min);
   2. switch to KakaoTalk via the app switcher (~1 min);
   3. go to the home screen;
   4. open Instagram again and lock the phone while it's open.
3. On the PC: `curl.exe -s https://<pc>.<tailnet>.ts.net/api/v1/hello -H "Authorization: Bearer <token>"` still
   works, and `%LOCALAPPDATA%\LifeReplay\logs\` shows lines like `Sync: app events from 127.0.0.1: 1 new, 0 duplicate`.
4. Viewer → refresh (F5) → check that the lane and Summary → iPhone match your notes. Expect a **Complete** session for
   each open→close pair. Check whether "Is Closed" fires when the phone locks or when you use the app switcher; if
   not, the session shows as inferred or unknown.
5. **Offline:** turn Tailscale off on the phone, open and close an app, then turn Tailscale on and open any tracked
   app. The earlier events should arrive with their original times (`… 3 new` in the log).
6. **Compare with Screen Time** (Settings → Screen Time → See All App & Website Activity → Day) at the end of a day.
   Screen Time counts differently (e.g. picture-in-picture, audio, widgets), so expect differences of minutes. Large
   gaps usually mean an automation didn't run. Look at the "Missing events" list in Summary → iPhone.
7. Note anything that differs from this guide (labels, permission prompts, failures) in an issue.

## Limitations (iOS)

- **Only apps with automations** are tracked. Nothing is read from Screen Time; Apple offers no API for its data
  outside Apple's own frameworks.
- **Automations can be late or not run**: Low Power Mode, a locked phone, Focus or iOS deciding not to run them can
  delay or skip events. The viewer shows these as inferred or unknown sessions rather than guessing.
- **"Is Closed"** may fire later than you leave the app, or not at all in some cases (locking the phone, app switcher).
  Verify on your iOS version.
- **Each run does a network request.** When the PC is unreachable, iOS may show a failure notification for the
  automation. The queue keeps the events and sends them with the next successful run, and only then. There is no
  timed background retry.
- **The token lives in the shortcut** (Shortcuts has no secure storage). Anyone who can open your Shortcuts can read it.
- **Queue file limit:** if the PC is unreachable for a very long time and the queue grows over 2 MB (about 10,000
  events), the PC answers 413. Delete `Shortcuts/LifeReplay/queue.txt` in the Files app.
- **Times come from the phone's clock.** If the phone's clock is more than 2 minutes ahead of the PC, sessions get a
  "phone clock ahead" note.
- **No signed `.shortcut` file** (see above), so setup is manual.

## Troubleshooting

| Symptom | Check |
|---|---|
| Nothing arrives, the log shows nothing | Tailscale on? `tailscale serve status` on the PC? Tray → iPhone app tracking enabled? |
| Log: `rejected request … (bad token)` | Header must be exactly `Authorization: Bearer <token>`. Each "Set up iPhone" creates an additional token (older ones keep working); "Revoke all paired iPhones" invalidates every token. |
| Log: `rejected … timestamp must be ISO 8601 with a UTC offset` | In step 3, Format Date must be **ISO 8601** with time included. |
| Only "opened" events arrive | The "Is Closed" automations are missing or have Run Immediately off. |
| Safari shows a certificate warning for the ts.net URL | HTTPS Certificates not enabled in the Tailscale admin console. |
