# AI coach

The coach helps you follow your plan and records evidence of what you finished. It's built in two layers:

1. **Deterministic, always local**: a schedule evaluator that counts only *verified outputs*, notification rules,
   outcome measurement and calibration. No model involved; everything is unit-tested.
2. **Optional model call**: a suggestion written by a model from a compact, sanitized summary. It is sent only
   after you approve the exact payload. The answer is validated before it's shown; if anything goes wrong you get
   the rule-based suggestion instead.

The flow:

```
todo.md + window log + git ──► ScheduleEvaluator ──► state: on_track / ahead / behind / insufficient_evidence
                                     │
                                     ├─► InterventionPolicy ──► tray notification (rules only, no model)
                                     │
                                     └─► ContextBuilder (sanitized) ──► preview ──► you click Send
                                                                              │
                                              OpenAI Responses API (BAZE) ◄───┘
                                                     │  strict JSON schema
                                                     ▼
                                           SuggestionValidator ── reject ──► 1 retry with the reasons ──► fallback
                                                     │ ok
                                                     ▼
                                   suggestion + evidence ──► Accept / Not now / Dismiss
                                                     ▼
                                   outcome measured an hour later from the window log
```

## Using it

**Viewer → Summary → Coach** (or the coach tray icon → Open coach):
- **Evaluation:** the day's state, confidence, each task's verified output, and warnings. It warns when the
  rest of the plan doesn't fit the working time left, and shows YouTube/social time, goals and their deadlines,
  and calibration.
- **Requests:** **Morning plan**, **Check now** and **End-of-day review** show the request preview; nothing is
  sent until you click **Send**.
- **Switches:** **AI suggestions** and **Notifications**. Notifications are handled by a small tray app,
  `LifeReplay.CoachTray.exe`, started with Windows.
- **Copy context for ChatGPT / Paste plan back…** lets you refine the plan in the ChatGPT website without API
  credits. Its final plan block is validated, then added to your todo list only if you click.
- **Sent requests…** is the audit log: every payload, status, attempt count and credit cost.
- **Finished work** is the private ledger, with a "show" switch per item that is off by default.

### What counts as progress

Time in an app is shown, but it is never progress. Progress comes from these lines in `todo.md`:

```md
goal: Land a research lab internship by 2026-12-15     ← goals with deadlines (anywhere in the file)

# 2026-10-30
- [ ] Read domain theory paper ~2h
  progress: 3/12 pages          ← fraction done (keep it current)
- [ ] LifeReplay coach ~3h
  repo: C:\Users\lee\life_replay ← today's commits count as evidence (count only, no fraction)
- [x] Fix iPhone docs ~45m       ← ticked = 100%
```

## Deterministic evaluation (`ScheduleEvaluator`)

- **Expected progress:** the share of the working window that has passed. The window comes from `hours:` in
  todo.md, else the recording hours, else 09:00–18:00.
- **Task fraction:** a ticked task counts 1. A `progress: x/y` line counts x/y. Commits are evidence without a
  fraction.
- **Task state:**
  - no fraction → `insufficient_evidence`;
  - done before the end of the window → `ahead`;
  - otherwise `ahead` or `behind` when the fraction differs from the day's elapsed share by more than 15
    points, else `on_track`.
- **Day state:** computed over the tasks that have a planned time *and* a fraction.
  - If those cover less than 50% of the planned time → `insufficient_evidence`.
  - Otherwise verified minutes (planned × fraction) are compared with expected minutes, with a tolerance of
    max(15 min, 10%).
- **Confidence:** the share of planned time with evidence × the average confidence per task.
  - Per task: ticked 1.0, progress line 0.7, commits only 0.3.
- **Overcommitted:** the planned work remaining is more than the working time left.
- **Time spent per task** comes from the existing matcher (`DayAnalysis.Match`, also used by the Plan view):
  - the viewer uses the full language model;
  - the tray app uses `CachedPlanMatcher`, which uses keywords, your manual labels and vectors already cached in
    the database, so it doesn't need to load the model.

## Notification rules (`InterventionPolicy`)

The rules are checked in order, and the first one that says no wins:
1. notifications off;
2. quiet hours (default 22:00–08:00);
3. outside the working window;
4. the daily maximum (default 3);
5. the cooldown since the last notification (default 60 min);
6. snoozed (default 60 min);
7. "quiet for today";
8. the day's evidence confidence below 0.5.

When nothing blocks, the first trigger that applies fires:
- **behind schedule:** the notification names the task furthest behind, unless you dismissed that task today;
- **more than 30 min of YouTube/social in the last hour;**
- **the plan doesn't fit the time left.**

The drift and overcommitted notifications fire at most once a day. A notification never calls the model.

## Model requests

- **Interface:** `ICoachingProvider`. The implementation is `OpenAiResponsesProvider`, which sends
  `POST {baseUrl}/responses`:
  - strict structured output (`text.format = json_schema`, `strict: true`);
  - `store: false`;
  - `reasoning.effort: low`, to keep hidden reasoning tokens down;
  - a timeout per request.

  It was tested on 2026-10-29 against `https://api.baze.co/v1/gateway` with `gpt-6-astra`.
- **Retries:** at most `retries` (default 1) extra attempts, only after a timeout, network error, 429 or 5xx. A
  rejected request (401/400) is not retried.
- **Validation:** checked by `SuggestionValidator` before anything is shown. An answer is rejected if it has:
  - evidence ids or task ids that aren't in the context;
  - any number in the message or counterpoint that doesn't appear in the context (minutes it proposes are
    allowed);
  - minutes out of range, or more than 3 actions;
  - a missing or copy-paste counterpoint;
  - no evidence cited without `insufficient_evidence` being set.

  A rejected answer gets **one** retry with the reasons, then the rule-based fallback.
- **Against the model just agreeing with you:** the instructions require a counterpoint ("the strongest honest
  reason your advice could be wrong"), forbid praise, and allow "insufficient evidence". The validator enforces
  the counterpoint. Each suggestion's outcome is measured afterwards, so how good the suggestions are shows up in
  the data.
- **No LLM in the deterministic parts:** evaluation, rules, outcomes, calibration and validation are plain code.
- **Proposals only:** nothing is submitted, deleted or put on a calendar. The todo file changes only when you
  click "Append to todo list", and a backup is kept in `todo.md.bak`.

## Privacy

**Never sent:**
- video and screenshots;
- window titles, URLs and file paths;
- the window log itself.

**Sent, after you approve the preview:**
- today's task titles (or "Task 1…" with `redactTaskTitles`);
- minutes per task;
- verified outputs ("3/12 pages", "7 commits today"; subject lines only with `includeCommitMessages`);
- the deterministic evaluation;
- goals;
- your last few suggestions with your responses.

Free text (task titles, goals, commit subjects) passes through `ContextBuilder.Sanitize`. It removes:
- URLs, e-mail addresses and CTF flags (`pwn.college{…}`);
- `password:`/`token=`-style credentials;
- Windows and Unix paths;
- key-like tokens and numbers with 6 or more digits.

Tests check that window titles, URLs, paths, flags and passwords never appear in the payload.

**Audit log:** every request is stored in `coach_requests` with the exact payload, its SHA-256, its size, status,
attempts and credits. You can view it in the viewer under **Sent requests…**.

**API key:** read from `BAZE_API_KEY`, then `OPENAI_API_KEY`, then `%LOCALAPPDATA%\LifeReplay\secrets.json`. That
file is outside the repository, and `secrets.json`, `.env` and `*.key` are also git-ignored. The key is never logged
or stored anywhere else.

## Costs

- **BAZE:** charges per request in credits, and `creditsPerRequest` (default 5) should match what your gateway
  charges.
- **Budget caps** (in `coach.json`): `monthlyCreditCap` (default 600) and `dailyRequestCap` (default 8 attempts).
  A request that would go over either is not sent, and you get the rule-based suggestion instead.
- **Typical use:** morning plan + one check + end-of-day review is 3 requests (15 credits) a day, about 450 a month.
  Retries cost the same again.
- **Measured on 2026-10-29:**
  - request size: about 3.5–3.9 KB of payload;
  - response time: 10–11 s with `gpt-6-astra`;
  - cost: 5 credits each, as configured; BAZE's actual charge wasn't checked against the account.
- **Your own OpenAI key instead:** set `baseUrl` to `https://api.openai.com/v1` and pick a model. Each request is
  roughly 1.5–2k input and 0.3–1k output tokens.

## Database (schema v5)

Existing tables the coach **reads**:

| Table | Use |
|---|---|
| `focus_events` | window log: minutes per task, YouTube/social time |
| `title_labels` | your manual window → task assignments (the matcher uses them) |
| `text_embeddings` | cached title vectors (`CachedPlanMatcher`) |

The rest of the schema (`devices`, `displays`, `sessions`, `segments`, `gaps`, `thumbnails`, `app_events`) is not
used by the coach.

New tables, written only by the coach:

| Table | Holds |
|---|---|
| `coach_requests` | audit log + budget: payload, sha256, bytes, status, attempts, credits, response, error |
| `coach_interventions` | suggestions and notifications: kind, source (model/fallback/rule/user), state, message, actions, evidence, counterpoint |
| `coach_feedback` | accepted / snoozed / dismissed |
| `coach_outcomes` | measured an hour later: focus on the suggested task, followed or not |
| `coach_evaluations` | day snapshots (at most every 10 min) |
| `coach_ledger` | finished tasks with evidence; `showable` defaults to 0 |

**Migration plan:**
- v5 is additive and forward-only (`PRAGMA user_version`), like v1–v4, and runs on the next start of any
  LifeReplay program.
- An older build refuses to open a v5 database ("schema is newer than this app"). Update the recorder, viewer and
  coach tray together; `build.ps1` publishes all three.
- WAL mode and pooled connections with busy retries let the recorder keep writing while the coach reads. The coach
  never writes to recording tables.

## Isolation

- The coach is a separate library (`LifeReplay.Coach`) plus a separate process (`LifeReplay.CoachTray.exe`). The
  recorder doesn't reference either, so a coach crash can't stop recording.
- In the tray app every timer tick is wrapped: an error is logged and the next tick (5 min) tries again.
- In the viewer, coach errors are caught and shown in the panel.

## Running locally

```powershell
# one-time: the key (outside the repo)
Set-Content "$env:LOCALAPPDATA\LifeReplay\secrets.json" '{ "bazeApiKey": "<your key>" }' -Encoding utf8

# evaluate today without sending anything
desktop\bin\Debug\LifeReplay.CoachTray.exe --once
# show the exact request; add --yes to approve and send it
desktop\bin\Debug\LifeReplay.CoachTray.exe --once --send check
desktop\bin\Debug\LifeReplay.CoachTray.exe --once --send review --yes
# tests (synthetic activity streams, fake provider, stub HTTP; no network)
dotnet test desktop\tests\LifeReplay.Tests
```

(`--once` writes to the console. From PowerShell, redirect it, e.g.
`Start-Process … -NoNewWindow -Wait -RedirectStandardOutput out.txt`, because the tray app is a windowed program.)

`coach.json` (created on first save from the viewer):

```json
{
  "enabled": false, "baseUrl": "https://api.baze.co/v1/gateway", "model": "gpt-6-astra", "reasoningEffort": "low",
  "timeoutSeconds": 60, "retries": 1, "maxOutputTokens": 1200,
  "creditsPerRequest": 5, "monthlyCreditCap": 600, "dailyRequestCap": 8,
  "notifications": false, "quietHours": "22:00-08:00", "cooldownMinutes": 60, "maxNotificationsPerDay": 3, "snoozeMinutes": 60,
  "redactTaskTitles": false, "includeCommitMessages": false
}
```

## Tests

`CoachTests.cs`: 54 tests.
- **Synthetic days:** ahead, behind, idle, time without output, unrelated tabs (YouTube/Gmail), task completed
  early, conflicting priorities (overcommitted), commits only, no planned times, goals.
- **Privacy:** titles, URLs, e-mail, flags, passwords and paths never reach the payload; title redaction; commit
  messages opt-in.
- **Validator:** unknown evidence or task id, made-up number, empty counterpoint, minutes out of range, too many
  actions, nothing cited, not JSON; numbers in derived forms (%, hours) are allowed.
- **Service with a scripted fake provider:**
  - stored with the audit payload;
  - a made-up answer retried once and then accepted;
  - repeated made-up answers → fallback;
  - API timeout, no connectivity, a 401 not retried;
  - disabled or no key → nothing sent;
  - monthly and daily caps;
  - outcome measurement, calibration, the ledger "show" switch kept.
- **Notification rules:** each rule that blocks a notification, dismissal, drift once a day, cooldown in the
  service.
- **Provider with a stub HTTP handler:**
  - the request body has a strict JSON schema, the Bearer token, `reasoning` and `store:false`;
  - 500, 429 and 401 are classified correctly, as are timeouts and network errors;
  - incomplete and refused responses are errors.
- **ChatGPT bridge:** a valid block round-trips through the todo parser; past dates, bad minutes and a missing block
  are rejected; overload warnings; the backup on append.
- **Migration:** schema v5 has the coach tables.

**Real end-to-end check (2026-10-29), on a separate test data folder.** Two approved requests went to BAZE
`gpt-6-astra`:
- **check:** "You have 366 minutes planned but only 268 minutes left…", citing ev1/ev4/ev6;
- **review:** cited ev1–ev6, with the counterpoint "this review is provisional…".

Both passed the validator on the first attempt. Payloads were 3.5 and 3.9 KB, and contain no paths, URLs or window
titles.

## Not done yet

- A morning plan or review that runs on a schedule. Today they are buttons, because every model call needs your
  approval.
- Showing the private ledger as a portfolio page. The "show" switch is stored, but there's no export yet.
- The quiz loop (questions on what you read) and pwn.college debriefs.
- A blind critic on a second model for high-stakes requests (aims, 14-day plans).
- Notifications use the classic Windows balloon. They weren't checked on screen in testing, because a test run
  would have shown them on your desktop.
