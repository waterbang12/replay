# LifeReplay local sync protocol (v1)

The iPhone app uploads finished recording segments to the Windows recorder over the local network.
Sync is **off by default** and only runs after the user enables it in the tray menu and pairs a phone.

Implementation: `desktop/src/LifeReplay.Sync/SyncServer.cs` (server), `ios/LifeReplay/SyncClient.swift` (client).
Tests: `desktop/tests/LifeReplay.Tests/SyncTests.cs`.

## Security model

| Threat | Mitigation |
|---|---|
| Eavesdropping on Wi‑Fi | HTTPS only (TLS 1.2+, TLS 1.3 negotiated in testing). There is no plain-HTTP listener. |
| Fake server (MITM) | The phone pins the SHA‑256 fingerprint of the PC's self-signed certificate, delivered in the pairing QR code. Any other certificate is refused, even one signed by a public CA. |
| Unauthorized uploads / reads | Every route requires `Authorization: Bearer <token>`. The 256-bit token is created at pairing and shown once in the QR code. The PC stores only `SHA-256(token)`; the phone keeps it in the Keychain (`AfterFirstUnlockThisDeviceOnly`). |
| Token guessing | Fixed-time comparison; 20 failed attempts from one IP within 10 minutes → HTTP 429 for that IP. |
| Path tricks in uploaded metadata | All ids must be GUIDs; file names are built by the server. |
| Corrupted uploads | Body SHA‑256 must match the metadata; the MP4 must pass the integrity check. |
| Recordings leaking out | The server has **no download or listing routes**. Recordings can be written, never read, over the network. |

The certificate's private key lives in the current user's Windows certificate store (DPAPI-protected, non-exportable).
"Revoke all paired iPhones" in the tray menu deletes all token hashes.

## Pairing

The tray shows a QR code containing:

```
lifereplay://pair?h=<PC LAN IP>&p=<port>&t=<token>&f=<cert sha256 hex>&n=<PC name>
```

Scanning it with the iPhone Camera app opens LifeReplay, which asks for confirmation and then calls `GET /api/v1/hello`.

## Endpoints

All requests: `Authorization: Bearer <token>`. Default port 47800 (`sync.port` in config.json).

### `GET /api/v1/hello`
`200 {"server": "<PC name>", "apiVersion": 1}`

### `PUT /api/v1/segments/{segmentId}`
- Header `X-LifeReplay-Metadata`: base64 of a UTF‑8 JSON object matching [`segment-metadata.schema.json`](segment-metadata.schema.json). `segmentId` must equal the URL.
- Body: the MP4 file.
- `201 {"status":"created"}`, or `200 {"status":"exists"}` if the segment is already stored (safe to retry),
  `400` invalid metadata, `422` hash mismatch or invalid MP4, `401` bad token.

The server files it under `recordings/YYYY-MM-DD/iphone/<deviceId[0..8]>/HH-mm-ss_<segmentId[0..8]>.mp4`
(local date/time of the segment start) and inserts device, display (`ios-<deviceId>`), session and segment rows in one transaction.

### `PUT /api/v1/segments/{segmentId}/thumbnails/{utcMs}`
Body: JPEG (≤ 2 MB). The segment must already be uploaded. `201`.

### `POST /api/v1/devices/{deviceId}/gaps`
Body: `[{"startUtcMs": 0, "endUtcMs": 0, "detail": "broadcast paused"}]`. Stored as timeline gaps with reason
`ios-interrupted`. Duplicate (start, end) pairs are ignored. `200`, or `400` for invalid JSON.

## Timestamps

All times are UTC Unix milliseconds. The phone converts ReplayKit host-clock timestamps to wall-clock time
when recording; the viewer converts to the PC's local time zone for display.
