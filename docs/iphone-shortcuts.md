# iPhone app tracking with Shortcuts (iCloud log)

LifeReplay can log **which iPhone apps you open and close** and show that next to your Windows time. Apple's
Shortcuts app runs a personal automation when an app opens or closes. The automation adds one line to a text file
in your iCloud Drive, and the recorder on the PC reads that file. Nothing is recorded on the phone's screen, no App
Store app is needed, and nothing listens on the network.

## How it works

```
iPhone: "YouTube" opens
  → automation: append "YouTube opened 2026-11-03T16:21:50+09:00" to LifeReplay.txt (iCloud Drive, Shortcuts folder)
  → iCloud syncs the file to the PC (iCloud for Windows)
  → the recorder checks LifeReplay*.txt every 30 s and stores new lines (app_events)
  → the viewer pairs opened/closed into sessions (📱 lane, Summary → iPhone)
```

- **The phone writes the time.** Events made while the PC is off, asleep or offline wait in iCloud and arrive
  later with their real times.
- **Re-reading is harmless.** Event ids come from app + event + second, so lines already read are duplicates.
- **Accepted lines:** `<app> opened|closed <date>`. "열림/닫힘" work too. The date can be ISO 8601 with an offset,
  or the phone's Korean format (`2026. 11. 3. 오후 4:40:05`, read as the PC's local time). Other lines are skipped,
  and the tray shows the first one it didn't understand.

## Setup

1. **iPhone:** Settings → your name → iCloud → **iCloud Drive** on.
2. **PC:** the **iCloud** app (iCloud for Windows) → sign in with the same Apple ID → **iCloud Drive** on. It
   appears as `%USERPROFILE%\iCloudDrive`; the Shortcuts folder shows as `iCloud~is~workflow~my~workflows`.
3. **Each app gets two automations** (opened and closed). Shortcuts → Automation → **+** → **App** → choose the
   app → **Is Opened** only → **Run Immediately** → Next → **New Blank Automation**, then add one action,
   **Append to Text File** (텍스트 파일에 추가):
   - **Text:** `YouTube opened ` followed by the **Current Date** variable. Tap it → Date Format **ISO 8601**, and
     turn **Include Time** on.
   - **File:** `LifeReplay.txt` in the Shortcuts folder. Expand the action and turn **Make New Line** on.

   Then do the same with **Is Closed** and `YouTube closed `.
4. If the file doesn't appear on some iOS versions (Append doesn't create a missing file), create it once: a
   shortcut with **Text** `start` → **Save File** → `LifeReplay.txt`, with "Ask Where to Save" off.
5. **Check:** open and close the app, wait 1–3 minutes, then look at tray → **iPhone app tracking** →
   "iCloud log: read HH:mm". Press F5 in the viewer.

## How sessions are rebuilt

- An "opened" followed by that app's "closed" is a **complete** session.
- No "closed", but another app opened within 30 min: the session ends then (**end inferred**).
- A "closed" with no "opened" before it: it starts at the previous event if that's within 30 min
  (**start inferred**), otherwise the duration is **unknown** and isn't counted.
- **Duplicates are dropped:**
  - the same app and event within 2 s (double triggers);
  - an "opened" for an app that's already open (the first start is kept);
  - a "closed" for an app that's **already closed**. For example, a Screen Time limit makes iOS send "closed"
    again a few seconds later.

## Limitations (iOS)

- **Only apps with automations** are tracked. Apple offers no way to read Screen Time data on Windows.
- **Delay:** iCloud sync usually takes a few minutes, longer in Low Power Mode.
- **Automations can be late or skipped**: Low Power Mode, Focus, or iOS deciding not to run them. The viewer shows
  those sessions as inferred or unknown rather than guessing.
- **"Is Closed"** may not fire when you lock the phone. The session then ends when the next app opens.
- The file keeps growing (about 50 bytes per event, a few MB a year). Deleting it in the Files app is safe; events
  already read stay in LifeReplay.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Tray says "iCloud Drive not found" | Turn on iCloud Drive in the iCloud app on the PC. |
| Tray says "waiting for LifeReplay.txt" | Check Files → iCloud Drive → Shortcuts on the phone; create the file (Setup step 4). |
| "⚠ … not understood" | Usually the date: tap the Current Date variable → ISO 8601 with time. |
| Only "opened" events arrive | The "Is Closed" automations are missing or have Run Immediately off. |
| A "closed" arrives together with every "opened" | That app's "closed" automation has **Is Opened** ticked too. |
