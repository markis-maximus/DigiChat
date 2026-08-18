# Handoff — project state as of 2026-08-16

This is a dated snapshot of this workstation, including gitignored
databases, Twitch configuration, built frontends, and locally supplied art. A
fresh public clone does not contain those local artifacts. Durable procedures
live in `docs/runbooks/`; do not treat a machine-state note here as a runbook.

## Latest: security and robustness hardening, reproducible public builds

A security and robustness review pass has been folded into the current
working tree. The five concrete 1.0.1 candidates previously listed in
`docs/DECISIONS.md` are implemented: actor colliders are removed on resync,
visual windows are reserved inside the mutation gate, deferred notifications
are ordered, failed post-commit sends trigger best-effort authoritative resync,
and overlay state pulls retry plus reconcile every 30 seconds while connected.
Focused tests now cover the visual-window race, notification failure,
repeat-redelivery, held-user death undo and migration backfill, invalid numeric
stages, stale session-start and undo snapshots, Twitch admission retry success,
exhaustion and cancellation, session capacity, cross-checkout LocalDB
registration safety, and the verified SQL-backup path.

The broader runtime pass adds a pre-database single-instance guard,
Development-mode mock enforcement, loopback/origin/WebSocket/browser-command
guards, optimistic stale-snapshot checks for browser session-start and undo,
bounded admissions and reads, validated roster/layout/options, a bounded Twitch
admission queue with three-attempt processing, atomic token replacement,
persistent held-user state with database constraints, and safer stale-catalog
handling. These remain local-app defenses rather than authentication; the exact
residual boundaries are recorded in `docs/DECISIONS.md` and `SECURITY.md`.

Builds now pin the .NET SDK feature band and dependency versions/lockfiles. The
Windows CI and local release gate share `scripts/Verify-Repository.ps1`, which
audits both NuGet and npm graphs at low severity or higher, builds/tests, and
then publishes through a public allowlist. Public-mode overlay builds omit the
local art tree, and a post-publish checker rejects known
secret/database/log/raw-art/generated-manifest paths. Keep a distributable with
`-KeepPublish`; see `docs/runbooks/VERIFY.md`. The checker is targeted, not a
generic secret or license scan.

Existing, connectable LocalDB databases now receive a
`data/db/backups/*-pre-migration-*.bak` before pending migrations, followed by
`RESTORE VERIFYONLY WITH CHECKSUM`. Backup or verification failure blocks
migration, but the file is not copied off-project or test-restored. The database
runbook documents that limitation and the exact stale-registration safeguards.
Treat the full verification script's current output—not this dated snapshot—as
the release result.

## Earlier on 2026-08-16: death and reincarnation became two steps

A generation now **dies** first — corpses hold on screen, dark and frozen, at
the stage they fell at — and reincarnation is a separate, deliberate act
afterwards. Undo revives a death; reincarnation is final. Chatters arriving
during a death are held off screen until the new generation hatches. Verified
end-to-end in mock mode, including a reload mid-death restoring the silhouettes.

Alongside it, the 1.0 release pass: MIT LICENSE with a Bandai IP notice and
CC BY-SA attribution for the vendored name list, a cross-origin guard on every
mutating endpoint (a drive-by POST could previously kill the generation on
stream), the admin panel's reconnect policy fixed so it no longer gives up
after ~42 seconds, Twitch status changes now pushed to the panel, and
tool-neutral runbooks with `.claude/skills/` adapters for agent-driven work.

## Previously: local real art is in, and all five stages are sized and signed off

The overlay renders the DIGIMON UP V1 roster — 30 lineages, 150 unique species,
no species repeated. Local art goes in
`src/DigiChat.Overlay/public/assets/sprites/<assetKey>/idle.png` and
`import-assets.bat` does the rest; see README "Adding real Digimon art".

Sizing is automatic and then hand-corrected per stage. The importer measures each
PNG's **alpha bounding box**, so transparent padding affects nothing — not
display size, not ground contact, not the collision box. Each form is fitted to
its stage and snapped to a whole-number scale factor where that stays close, so
pixel edges survive.

Fresh and In-Training use `stageSizeVariance` 1, meaning every form is rendered
at the stage's target height — right for those stages, whose species really are
the same size. Rookie, Champion and Ultimate use 0.4, which lets the source
art's own size differences through: at Rookie the native art spans a 15x range,
and flattening it made a ferret as tall as a humanoid fox *and* caused every
chunky sprite at that stage. Blending fixed both at once.

All five stages have been through the review loop and approved.
**docs/ASSET-TUNING.md is the manual for that loop** — the method, what can and
cannot be automated (measured against the recorded labels), their 1-30
numbering, and every lesson that cost a round trip. Read it before touching
sizes.

Nine forms remain on chunky scale factors, all tiny sources: pafumon, popomon,
caprimon, dorimon, kokomon, falcomon, lopmon, impmon, skullmeramon. They are the
re-export candidates; no amount of scaling fixes a 41px Ultimate.

Sprites are mirrored to face their direction of travel, which assumes the art is
drawn facing right. 39 of the 150 are not, and carry `facesLeft` in
overrides.json. For a new roster, run
`node src/DigiChat.Overlay/tools/facing-sheet.mjs` from the repository root after
importing assets. It reads authoritative `data/lineages.json`, derives the
display order and asset keys, and renders every idle sprite at rest with a
checkbox per form. Use that instead of judging facing from moving sprites in the
overlay. `_settings.lockFacing` disables all mirroring if the same judgement is
ever needed live.

## Earlier (2026-08-10, evening): database moved into the project

A live `start-digichat.bat` launch crashed with SQL error 5170 ("Cannot create
file 'C:\Users\<you>\DigiChat.mdf' because it already exists"): LocalDB had been
defaulting the database into the user profile, and a stale .mdf/.ldf pair was
sitting there with no matching entry in the instance's master, so EF decided the
database did not exist and its CREATE DATABASE collided with the leftover files.

Fixed by giving the database an explicit path inside the repo:
`data/db/DigiChat.mdf`, attached via `AttachDbFilename`. New
`DatabaseLocation` (Infrastructure/Persistence) resolves the `%DBDIR%` token in
the connection string to `<repo root>\data\db` (falls back to a `data\db` folder
next to the executable for a published build) and creates it. Applied to
appsettings.json, `ServiceCollectionExtensions`, and
`DesignTimeDbContextFactory` (so `dotnet ef` uses the same file). `data/db/` is
gitignored; startup logs the folder. The existing database (with its seeded
roster and history) was detached and moved there — no data was lost, no files
remain in the user profile. Verified: Release build clean, mock-mode start
attaches the moved file and seeds idempotently (30 lineages updated, 0 added),
tests green (the LocalDB ones now build their throwaway DBs in data\db too).

Mock mode was then given its **own** database, `data/db/DigiChat.Mock.mdf`
(ConnectionStrings override in appsettings.Development.json), because both modes
had been sharing one file — that is how the mock chatters ended up in the
live state. Verified: a mock start creates the separate file and
bootstraps its own Generation 1 while the live .mdf is untouched.

Terminology note: the admin UI now says "overlay session" where the spec and
the domain say "stream session" (table `StreamSessions`, `SessionService`,
spec §7 — all unchanged). A reader took "Start New Stream Session…" as "go
live on Twitch" and would not click it while testing, so the UI wording, the
confirm dialog and the no-session warning now spell out that it is local-only.
Use the domain term in code and specs, the UI term in anything a person reads.

Also: the admin page used to show its Dev/Mock chat panel in live mode, where
`/api/dev/chat` is not mapped, so every button 404'd. `/api/config/features`
now reports whether the dev endpoints exist and the panel hides itself when
they don't (an older backend without the endpoint leaves the panel visible).

Live Twitch **connection** was confirmed on 2026-08-10 (handoff item 1):
authenticated as the broadcaster account, channel.chat.message subscription
created, EventSub session established, admin reads "Connected (listening to
#<broadcaster>)". Connection only — a real chat message spawning a Digimon,
token refresh and EventSub reconnect are still unexercised; see "What REMAINS".
OBS Browser Source is added (item 2). A live database that has been used for
mock testing will still carry that state, and should be reset to a clean
generation 1 before the first real stream. That reset must follow
`docs/runbooks/DATABASE-RECOVERY.md` with an explicit live-history confirmation
and backup, not wildcard deletion. The DPAPI token file is separate, so a
database reset does not require re-authorizing Twitch.

For any agent (or human) continuing this project. Companion docs: README.md
(user-facing setup), docs/DECISIONS.md (architecture rationale), CLAUDE.md
(project working rules), docs/runbooks/ (canonical operations), and
CONTRIBUTING.md (public contribution path). The original concept document lives
on this workstation, outside this repo.

## What is DONE and verified

- Full backend (domain, EF Core + LocalDB migrations, admission/session/
  transition/undo services, EventSub WebSocket client with device-code OAuth,
  SignalR, Serilog). The full suite was green at this snapshot.
- Overlay (Phaser 4) and admin (vanilla TS) built and served by the API.
- Live mock-mode verification of the whole acceptance flow: admission, dedupe,
  no-op stage clicks, transition locking, arbitrary stage jumps, reincarnation
  to a new generation, undo of stage changes, state persistence across backend
  restart, overlay reconstruction on reload.
- The full kill → hold → reincarnate flow, verified live in mock mode
  (2026-08-16): corpses dark and stationary at the stage they died at, a
  browser-source reload restoring the silhouettes rather than live sprites, a
  chatter arriving mid-death held off screen and then hatching with the new
  generation, stage changes refused while dead, and undo sealed after
  reincarnation.
- **Real OBS geometry is configured and verified** (data/layout.json):
  canvas 1920x1080; left-upper platform x 0–1145 top 208; right-lower platform
  x 1144–1920 top 532; solid wall at the 324px step face (x≈1144.5, y 208–532).
  Traversal: overlap platforms jump vertically; adjacent "step" platforms walk
  to the boundary and jump across (planTraversal in scene.ts). Jump geometry is
  measured from the wall's faces and padded by the actor's body half-width, and
  is direction-dependent: a long run-up when jumping *up* a step, a long
  run-out when jumping *down* one. Symmetric insets cannot work. Verified: 6
  mock actors roaming the real layout, no console errors. The
  direction-dependent reasoning is recorded here because the pre-squash
  development commit that introduced it is no longer part of reachable history.
- Twitch app Client ID configured in src/DigiChat.Api/appsettings.Local.json
  (gitignored): the broadcaster's own Twitch app, Public client, device code flow,
  scope user:read:chat only.

## What REMAINS

1. **Live Twitch smoke test** — initial connection was previously confirmed.
   Still unexercised: a real chat message spawning a Digimon, token refresh
   after the access token expires, an EventSub reconnect, admission recovery or
   exhaustion under a transient failure, and overload status after the bounded
   queue. Log lines exist for these paths, but mock/unit results are not a
   substitute for the live smoke test.
2. **OBS Browser Source add** — DONE.
3. **Real sprite art (this workstation only)** — DONE for idle. 150 forms in,
   all five stages sized and approved. The public clone correctly falls back to
   procedural placeholders. Still open locally, none blocking:
   - the shared reincarnation egg is deliberately left as the procedurally
     drawn one; `sprites/_egg/idle.png` is empty by choice, not oversight
   - `walk.png` / `airborne.png` per form; every state currently falls back to
     idle, which works but means no motion art
   - `data/layout.json` is back to `"debug": false`; turn it on only for a
     deliberate OBS layout-tuning session, then turn it off again
4. Optional polish backlog (none blocking): species egg overrides, deeper
   undo levels, admin auth (currently localhost-only, unauthenticated), nicer
   digivolve/death particle effects, spawn-position dedup so two spawns don't
   overlap visually, and an off-project backup retention/restore drill.

## Operational knowledge

- The app is normally launched from the repo-root bat files. Do not leave a
  server running from an automated session on someone's behalf — it dies with
  the session and leaves the database attached.
- LocalDB: instance MSSQLLocalDB, DB name DigiChat, connection string in
  appsettings.json. `sqllocaldb` CLI is at
  "C:\Program Files\Microsoft SQL Server\160\Tools\Binn\SqlLocalDB.exe" (PATH
  may not include it in fresh shells). The instance is shared with unrelated
  LocalDB projects for this Windows user: normal DigiChat cleanup stops only the
  owned app process. Use targeted catalog inspection/detach from the recovery
  runbook only when a file operation genuinely requires it.
- The admin UI's transition lock is time-based (Transitions:*Seconds config);
  buttons re-enable via a short poll. Backend checks and reserves the window
  inside the same mutation gate, so concurrent commands cannot both commit.
- The admin UI sends its current session number when starting a session and its
  last undoable transition ID when undoing. The backend compares those values
  inside the mutation gate, so a stale/double browser command cannot mutate the
  newer state. Native clients may omit the optional headers; this is not a
  durable command ledger or general command deduplication. Browser command and
  follow-up status reads have bounded timeouts so a stalled loopback response
  cannot leave every control latched forever; refreshed server status remains
  authoritative because an already-admitted backend mutation still finishes.
- Twitch admissions are consumed serially and get at most three total attempts
  (immediate, then after 250 ms and 1 second). Exhaustion abandons that message;
  retries do not make the 256-entry outer queue durable, and a full queue still
  drops new chat.
- A named mutex refuses a second DigiChat in the same Windows session before
  database initialization. It is not a cross-user machine lock; LocalDB and
  port 5170 remain backstops.
- Post-commit SignalR work is ordered and best-effort. Send failure triggers a
  state resync attempt, and a loaded overlay reconciles every 30 seconds, but
  there is no durable outbox. An OBS source that failed to load the page still
  needs a manual refresh.
- Undo semantics: append-only TransitionRecord. Stage changes and deaths undo;
  reincarnation is final by design (see DECISIONS.md). Never delete history to
  "fix" anything. `Generation.UndoneUtc` is legacy — it is still read, but
  nothing sets it now that reincarnation cannot be rolled back.
- The 31st simultaneous viewer intentionally gets no sprite ("awaiting
  lineage", admin warning) — that is spec §9 behavior, not a bug.
- Dev endpoints (/api/dev/chat*) exist **only in Development**, deliberately not
  "Development or MockMode". Only Development reasserts the mock connection
  string, so enabling them on MockMode alone let `Twitch:MockMode=true` in
  appsettings.Local.json plus the Production launcher write invented chatters
  into real stream history.
