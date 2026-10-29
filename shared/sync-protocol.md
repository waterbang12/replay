# iPhone app-usage event protocol

Apple Shortcuts automations on the iPhone ("App is opened" / "App is closed") send small JSON events to the
Windows recorder. There is no iPhone app and no screen recording on the phone.

Implementation: `desktop/src/LifeReplay.Sync/SyncServer.cs` (HTTP), `desktop/src/LifeReplay.Core/AppEvents.cs`
(validation, storage), `desktop/src/LifeReplay.Core/AppSessions.cs` (sessions). Setup on the phone:
[docs/iphone-shortcuts.md](../docs/iphone-shortcuts.md).

## Network path

```
iPhone Shortcuts ──HTTPS (valid *.ts.net certificate)──► Tailscale Serve on the PC ──HTTPS──► 127.0.0.1:47800 (LifeReplay)
```

- LifeReplay listens on **127.0.0.1 only** (default `sync.bindAddress`), so nothing on the LAN or internet can reach it.
- Tailscale Serve (`tailscale serve --bg https+insecure://127.0.0.1:47800`) publishes it **inside your tailnet only**
  with a publicly trusted Let's Encrypt certificate for `<pc>.<tailnet>.ts.net`. That matters because Shortcuts'
  "Get Contents of URL" refuses self-signed certificates, and installing a custom root CA on iOS is unreliable.
- Do **not** use `tailscale funnel`: that would expose the endpoint to the public internet.
- Plain HTTP is not offered.

## Authentication

Every route requires `Authorization: Bearer <token>`. The 256-bit token is created by tray → iPhone app tracking →
"Set up iPhone (new token)…", shown once, and pasted by you into the shortcut. The PC stores only `SHA-256(token)`
(`sync/pairing.json`). 20 bad tokens within 10 minutes from one address → `429` for the rest of the window
(behind Tailscale Serve all requests come from 127.0.0.1, so this is a global limit). "Revoke all paired iPhones"
deletes every token hash. No secret is in the repository or in the build.

## `GET /api/v1/hello`

`200 {"server": "<PC name>", "apiVersion": 2}`. Useful to test the URL and token.

## `POST /api/v1/app-events`

Body (≤ 2 MB): a single JSON object, a JSON array of objects, **or JSON Lines** (one object per line, which is what
the Shortcuts queue file contains). `Content-Type` is ignored.

```json
{"device_id":"iphone","source":"ios_shortcuts","app_name":"Instagram","event_type":"opened",
 "timestamp":"2026-10-29T14:00:00+09:00","event_id":"Instagram-opened-2026-10-29T14:00:00+09:00"}
```

| Field | Rule |
|---|---|
| `device_id` | required, `[A-Za-z0-9._-]{1,64}` |
| `source` | optional, same charset, default `ios_shortcuts` |
| `app_name` | required, 1–80 characters, control characters removed |
| `event_type` | `opened` or `closed` (also accepts `open`, `close`, `is opened`, `is closed`) |
| `timestamp` | ISO 8601 **with** a UTC offset (`Z`, `+09:00` or `+0900`). Stored as UTC ms + the offset. Rejected if more than 10 min in the future or older than 30 days. |
| `event_id` | optional, ≤ 100 characters. Missing → derived from device + app + type + timestamp second. |

camelCase keys (`deviceId`, `appName`, …) are also accepted.

Response: `200 {"accepted": n, "duplicates": n, "rejected": n, "errors": ["#3: …"]}`. If the body contained nothing
usable the same JSON comes back with `400`. `401` (bad token), `413` (too large) and `429` have no body. The shortcut
clears its queue only when the response contains `"accepted"`, so a single malformed line can't block the queue forever.

**Duplicates**: `UNIQUE(device_id, event_id)`. Resending the whole queue after a lost response is safe.
**Order**: events are stored as they arrive; sessions are rebuilt from timestamps, so late and out-of-order events are fine.

## Storage

`app_events` table (schema v4): `device_id, source, app, event_type, ts_utc, tz_offset, event_id, id_derived,
received_utc`. Cleaned up by the same `retentionDays` as recordings.

## Session reconstruction (`AppSessions.Reconstruct`)

Deterministic rules, per device, after sorting by timestamp (a "closed" sorts before an "opened" at the same instant):

| Situation | Result |
|---|---|
| opened → closed (same app) | **Complete** session |
| same app + type twice within 2 s | second one dropped (double trigger) |
| opened twice without closed | first start kept, noted "duplicate open" |
| app A open, app B opens (A never closed) | A ends when B opens: **EndInferred** if ≤ 30 min, else **Unknown** |
| closed without opened | starts at the previous event: **StartInferred** if ≤ 30 min, else **Unknown** |
| opened, nothing after it | **Unknown** ("no closed event yet") |
| event timestamp > 2 min after it was received | noted "phone clock ahead of PC" |

Unknown sessions have no duration and are counted separately, never added to totals. Daily totals clip sessions at
local midnight, so a 23:50–00:10 session counts 10 minutes on each day.
