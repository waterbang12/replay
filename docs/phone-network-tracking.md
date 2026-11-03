# Phone app tracking without Shortcuts (network lookups)

The easiest way to see when you used which phone app. There are no Shortcuts and no per-app setup. The PC becomes
a DNS server for your phone over Tailscale. Every time an app looks up one of its servers (YouTube's video servers,
Instagram's image servers…), LifeReplay notes the time. A burst of lookups becomes an estimated session on the
📱 lane.

## How it works

```
iPhone (Tailscale on) ──DNS query──► Tailscale ─┬─► this PC, 100.x.x.x:53 (LifeReplay) ──► PC's DNS servers
                                                └─► Cloudflare (DoH)                        + 8.8.8.8 + 1.1.1.1
```

- Tailscale asks all configured nameservers **at the same time** and uses the first answer. With Cloudflare
  added as well, the phone keeps working when the PC is off or LifeReplay isn't running.
- LifeReplay forwards every query and sends the answer back unchanged. It asks the PC's current DNS servers plus
  8.8.8.8 and 1.1.1.1 at once, because some networks (e.g. campus Wi-Fi) block outside DNS servers.
- **Stored:** only lookups whose name belongs to an app on the list in
  [NetworkApps.cs](../desktop/src/LifeReplay.Core/NetworkApps.cs), as device, time, name and app. Everything else
  is forwarded and forgotten. Lookups from this PC itself are never stored. The data follows the same retention
  as the recordings.
- **Sessions:** lookups for one app at most 3 min apart form a session, which needs at least 3 lookups (in
  different seconds; one or two are usually a notification). It runs from the first lookup to 1 min after the last.
  If an app also has Shortcuts automations, their exact sessions are used instead.

## Setup (once)

1. **PC:** tray → **iPhone app tracking** → **Track phone apps without Shortcuts (network)…**
   - Tick **켜기 (on)**.
   - Click **방화벽 허용하기 (allow in firewall)** and answer **Yes** to the admin prompt. This adds one inbound
     rule, "LifeReplay phone DNS": UDP port 53, only to LifeReplay.exe, only from Tailscale addresses
     (100.64.0.0/10, i.e. your own devices). To remove it, run this in an admin terminal:
     `netsh advfirewall firewall delete rule name="LifeReplay phone DNS"`.
2. **Tailscale admin console** (the window's button opens https://console.tailscale.com/admin/dns):
   - Global nameservers → **Add nameserver → Custom** → the PC address shown in the window (100.x.x.x) → Save.
   - **Add nameserver → Cloudflare**.
   - Turn on **Override DNS servers**.
3. **iPhone:** keep Tailscale on, or turn on VPN On Demand in the Tailscale app. Watch YouTube for a minute. The
   window's status line should show the request.

## Limits

- **Estimates, not exact times.** The phone caches answers for a while (often 1–5 min), and apps also connect in
  the background (prefetching, notifications). Sessions are drawn faded and hatched and counted as "≈".
- **Only while Tailscale is connected on the phone.** With Tailscale off, nothing is seen.
- **Only listed apps.** Apps that use shared servers (Google, Meta's fbcdn, Akamai…) can't be told apart by name.
  Add domains to `NetworkApps.Apps` if you need more.
- **Encrypted DNS in an app** (an app or browser that uses its own DNS over HTTPS, or iCloud Private Relay in
  Safari) bypasses this.
- **Every tailnet device** using Tailscale DNS goes through the PC. Their known-app lookups are stored under
  their Tailscale name. The PC's own lookups are not stored.

## Troubleshooting

| Symptom | Fix |
|---|---|
| "시작 못 함: Tailscale isn't running…" | Start Tailscale on the PC and sign in; LifeReplay retries every minute. |
| "port 53 … already used" | Another DNS program is bound to the Tailscale address. LifeReplay only needs that one address; the Windows sharing service on 0.0.0.0:53 is fine. |
| No requests arrive | Check the firewall step, "Override DNS servers", and that Tailscale is on on the phone. On the PC: `nslookup www.youtube.com 100.x.x.x` should answer. |
| Phone internet slow or broken | Make sure Cloudflare (or another public server) is listed too, next to the PC. |
