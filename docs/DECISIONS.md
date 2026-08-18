# Architecture decisions

Dated notes on the non-obvious choices.

`spec §n` markers throughout the code and docs cite the original private concept
document ("1. Core concept.md"), which is **not in this repository and cannot be
looked up**. Treat them as provenance — a note that the rule came from the
design, not from someone's preference — never as a reference you can follow. Any
rule that actually matters should be written out here; if you find one that
isn't, write it down rather than citing the marker.

## 2026-08-10 — Verified third-party facts

- **Twitch EventSub**: WebSocket transport at `wss://eventsub.wss.twitch.tv/ws`;
  subscriptions on a WebSocket **must** use a user access token. Limits: 3
  connections, 300 subscriptions, cost ≤ 10 per token. Keepalive default 10s
  window (we request 30s); server `session_reconnect` messages carry a URL and
  the old socket stays open until the new one's welcome arrives. Twitch does
  **not** replay messages missed while disconnected.
- **channel.chat.message v1**: condition `{broadcaster_user_id, user_id}`;
  reading your own chat with your own token needs only `user:read:chat`.
  Payload carries `source_broadcaster_user_id` for Shared Chat, which we use to
  drop foreign-channel messages (spec §17).
- **OAuth**: Device Code Grant flow is Twitch's recommendation for local/
  standalone apps and supports **Public clients — no client secret at all**.
  Device-flow refresh tokens are single-use and expire after 30 days idle;
  worst case the streamer redoes the twitch.tv/activate step.
- **Phaser**: current stable is v4.2.x. v4 retains the Phaser 3 scene/arcade
  API this project uses; the spec's "Phaser 3, current stable version" is
  interpreted as Phaser v4 latest.

## Project layout: 3 backend projects, not 5

Domain (pure), Infrastructure (EF + services + Twitch), Api (host). The spec's
suggested Admin/Overlay "projects" exist as npm/Vite frontends under `src/`,
built into `DigiChat.Api/wwwroot`. Fewer .NET projects = fewer boundaries with
no lost separation: Domain still has zero dependencies, and application
services live in Infrastructure so tests can exercise them without booting the
web host.

## Orchestration services live in Infrastructure

Classic Clean Architecture would add an Application layer. For a single-host
local app that is ceremony without payoff; `AdmissionService`,
`SessionService`, `TransitionService` orchestrate EF directly and are tested
through their public API against a real (SQLite) database, which also
exercises the LINQ-to-SQL translation.

## Concurrency: one global async mutex (`TransitionGate`)

All state mutations serialize through a single `SemaphoreSlim`. Load is tiny
(one streamer's chat), so throughput is irrelevant and total ordering buys
correctness for free: a first-message arriving during reincarnation waits for
the commit, then assigns from the new generation (spec §14). Separately, a
time-based **visual window** (config: `Transitions:*Seconds`) defers spawn
*broadcasts* (not DB writes) while an animation plays, and the admin API
refuses new transitions during it. Windows are short, so a wedged flag can't
survive a crash — the window lives only in memory by design.

The service that commits a transition opens its visual window while it still
holds that mutex. Post-commit SignalR work goes through a separate ordered
in-memory queue. A send failure is logged and followed by a best-effort pull
from authoritative state, but this is deliberately not a transactional outbox:
the database commit is the success boundary and a process exit can discard
queued notifications. Each send and reconciliation attempt has a 10-second
deadline; cooperative cancellation plus a bounded wait prevents one wedged hub
operation from permanently blocking the ordered tail.

## Idempotency: unique index on EventSub message ID

`ProcessedChatEvent.MessageId` (the EventSub delivery ID, spec §15) with a
unique index; check-then-insert inside the gate, constraint as backstop. Rows
older than 24h are pruned at startup and at most hourly while the app remains
open — Twitch redelivery happens within seconds, a day is generous. Repeat
messages from someone already in the current session are no-ops and do not grow
the ledger.

No-session, repeat-participant, and capacity-refused messages intentionally do
not create durable ledger rows. A bounded in-process cache remembers up to
16,384 message IDs for 10 minutes so a quick redelivery cannot become a new
admission after the operator starts another session. Restarting the process
clears that cache; durable dedupe remains only for messages that reached the
admission write. This trades a small, explicit redelivery edge after restart for
bounded database growth during busy chat.

## Undo: append-only marks, never deletes

`TransitionRecord` is append-only; undo stamps `UndoneUtc`. A stage change
reverts to `FromStage`; a death clears `Generation.DiedUtc` and existing
participants stand back up with the lineages they never lost. A viewer first
seen while dead is assigned an available lineage as part of that undo. This
trivially extends to multi-level undo later (spec §13's "deeper history
welcome").

## Death and reincarnation are two steps, and only the first is undoable

Digimon World 1's shape: a generation *dies* — corpses stay on screen, dark and
still, at the stage they fell at — and reincarnation is a separate, deliberate
act afterwards. `ReincarnateAsync` refuses while `DiedUtc` is null.

The death is a mistake worth being able to take back, so undo revives. The
reincarnation is not undoable, and returns "Reincarnation is final — a new
generation cannot be undone." Rolling one back would mean reopening a
generation whose lineages have since been redrawn and possibly handed to
viewers who joined after it; the state the undo restored would contradict what
those viewers were told they are. Sealing it is cheaper than reconciling it.

Chatters who arrive during the death are recorded but held by a persisted
`HeldForReincarnation` flag — they get no lineage and no sprite until the new
generation hatches (or the death is undone), so nobody walks in among the
corpses. The overlay skips them exactly as it skips awaiting-lineage
participants.

## Awaiting-lineage overflow = assignment row with NULL lineage

The 31st simultaneous viewer gets a participant + assignment row with
`LineageId = NULL` (filtered unique index allows many NULLs). They are listed
in admin with a warning and get no overlay sprite (or keep their egg after a
reincarnation) until a lineage frees up in a later generation.

## Sessions/generations are only created by explicit admin actions

`DatabaseInitializer` is idempotent and never creates a `StreamSession`
(spec §7: a crash/restart must not). It bootstraps only Generation 1 and the
`AppState` singleton on a virgin database.

## Frontends: Vite + vanilla TS

The admin page is one HTML file + one TS module over REST + SignalR; a
framework would be pure overhead. The overlay is Phaser with three concerns
kept separate: `assets.ts` (manifest + placeholder generation + fallback
rules), `actors.ts` (per-Digimon state machine + effects), `scene.ts`
(platforms, orchestration, serial event queue).

## Renderer authority model

The overlay holds no durable state. On connect/reconnect it invokes
`GetOverlayState` and rebuilds the final state without entrance animations
(spec §19). Live events (`spawn`, `stageChanged`, `reincarnation`) run through
a serial promise queue so sequences never interleave. Chatter entrances run in
parallel instead of holding that queue per actor. Before a global transition,
the scene waits up to 1.5 seconds for all active entrances together, then
grounds any malformed/slow remainder so Death cannot freeze a corpse mid-air.
An entrance settles only after its landing tween completes; forced grounding
or a transition cancels and invalidates that tween so a stale callback cannot
overwrite a Death or stage-change freeze.

State pulls retry while SignalR remains connected, and the overlay also pulls
authoritative state every 30 seconds. This repairs most missed or failed live
notifications without replaying their animations. It cannot repair an OBS
Browser Source that never loaded the page at all; when the backend was down at
initial page load, the operator must refresh that source after DigiChat starts.

## Platform geometry

`data/layout.json` in **OBS canvas pixels** (spec §21/§22): per platform the
`left`/`right`/`top` edges, plus spawn X range and edge margin. The overlay
converts OBS coordinates to physics bodies itself (one-way collision from
above, jump velocity derived from height difference + gravity). Shipped values
are verified 1920×1080 geometry and carry `"placeholder": false`.
Other streamers must measure their own composition and replace those values.
`"debug": true` draws the surfaces, wall, walk bounds, and spawn zone while
tuning. If the layout request fails entirely, the overlay uses a built-in
placeholder and logs the fallback rather than crashing.

## Digimon names: English release name, CamelCase, no spaces

Form names in `data/lineages.json` are what viewers read on screen, so they
follow one convention:

- **English release name** where one exists — Gatomon, not Tailmon; Growlmon,
  not Growmon; Crowmon, not Yatagaramon. For species that never left Japan
  (Zubamon, Ryudamon, Liollmon, Ludomon) the standard romanization *is* the
  English name.
- **CamelCase compounds, no spaces**: MetalGreymon, LadyDevimon, WarGrowlmon.
- The asset key is that name lowercased (`LineageSeeder.Slugify`), so renaming
  a form moves its `sprites/` folder and its `overrides.json` key with it.

The roster originally carried `DORUmon > DORUgamon > DORUguremon` — Bandai's
Japanese romanization, which really does shout, but which read as jarring next
to 147 English names. Renamed to `Dorumon > Dorugamon > DoruGreymon`
(2026-08-16); the first two kept their asset keys, the third moved
`doruguremon/` to `dorugreymon/`. `Tyilinmon` (#28) went the same way in the
same pass — US releases use **Chirinmon** — moving `tyilinmon/` to
`chirinmon/` along with its `scaleMultiplier` override.

`tools/check-names.mjs` (or `check-names.bat`) enforces all of this against
`data/digimon-names.json`, a vendored list of ~1,500 known Digimon. Its limits
are real and documented in that file: the broad source is Japanese-primary and
inserts its own spaces, so it can verify that a name **exists** but never how
it should be styled. The English names neither source carries are listed
under `englishOnly` with the Japanese counterpart each was checked against.

## 2026-08-16 — Adversarial-review hardening boundaries

The pre-release attacker/defender review led to a defense-in-depth pass. These
are useful safeguards, not a claim that a localhost app has become a hardened
multi-user service:

- A named `Local\DigiChat.SingleInstance` mutex is acquired before database
  initialization. It stops a second DigiChat in the same Windows session from
  migrating the attached file before discovering the occupied HTTP port. It is
  not a machine-wide, cross-user lock; port binding and LocalDB remain the
  backstops outside that session. `MSSQLLocalDB` is also shared with unrelated
  projects for the same Windows user, so routine shutdown stops only the owned
  DigiChat process; instance-wide stop is not a cleanup step.
- Development reasserts both `Twitch:MockMode=true` and the canonical
  `DigiChat.Mock.mdf` connection after local, environment-variable, and
  command-line configuration is loaded. A normal `dotnet run` therefore cannot
  connect to Twitch or point at the live database through a stale override.
  `appsettings.Local.json` should still remain limited to documented local
  Twitch identity/configuration values so Production behavior stays explicit.
- Startup rejects non-loopback bind URLs. ASP.NET host filtering, CORS, Origin
  checks, WebSocket allowed origins, anti-framing headers, and a UI-only command
  header reduce hostile-web-page and DNS-rebinding attacks. The admin API is
  still intentionally unauthenticated: native requests without an Origin are
  accepted, so any process running as the streamer can issue commands.
- For session start and undo, the committed admin UI also sends the session or
  last-undoable-transition value from its current status snapshot in
  `X-DigiChat-Expected-Session` or `X-DigiChat-Expected-Undo`. The service checks
  that value inside the mutation gate and refuses a stale command before it
  changes state. Native clients may omit these headers. They are an optimistic
  stale/double-click guard for the browser UI, not authentication, a general
  idempotency key, replay protection, or a durable command ledger.
- The admin bounds command receipt (including response-body parsing) at 15
  seconds and its follow-up status pull at 5 seconds, then always releases its
  button latch. Once a request entered the mutation gate the backend may still
  finish after the browser timeout, so the refreshed server state—not the
  timeout message—is authoritative.
- Inputs are bounded at several layers: EventSub identifiers/names have length
  limits, a session admits at most `Admissions:MaxParticipantsPerSession`
  viewers (500 by default), mock bulk admission is clamped to 100, state reads
  are rate-limited, transition durations are validated, layout and roster data
  are validated, and stage/transition enum ranges have application and database
  checks. These bounds limit accidental and browser-driven amplification; they
  are not authentication or a general local denial-of-service defense.
- Request cancellation applies while waiting to enter the mutation gate. Once
  admitted, session/admission/transition services finish their database unit and
  enqueue reconciliation with a non-cancelled operation token. A disconnected
  HTTP caller therefore cannot strand an accepted mutation between commit and
  notification; abrupt process termination can still do so.
- Twitch socket reads feed a bounded 256-message admission queue so slow
  database work does not block keepalives. Once dequeued, an admission failure
  gets at most three total attempts: the first immediately, then retries after
  250 ms and 1 second. Host cancellation stops the policy immediately; final
  failure is logged and that admission is abandoned. When the outer queue is
  full, the new message is deliberately dropped and the admin status/log
  reports the backlog. Twitch does not replay missed WebSocket chat.
- Token replacement is same-directory and write-through before the old
  DPAPI-encrypted file is replaced. DPAPI protects tokens at rest for the
  current Windows user; it does not make the file public-safe or protect it
  from a compromise of that user account.
- Before applying pending migrations to an existing, connectable SQL Server
  database, startup writes a `COPY_ONLY`/`CHECKSUM` backup under
  `data/db/backups/`, runs `RESTORE VERIFYONLY WITH CHECKSUM`, and confirms a
  non-empty file; any failure blocks the migration. Verification is stronger
  than a size check but is not a test restore, so the backup remains sensitive
  operational recovery material rather than a guarantee of recoverability.
- Public publish uses an explicit content allowlist, builds the overlay without
  its local `public/` art tree, and runs an artifact checker after publish. The
  checker rejects the known local-secret, database, log, raw-art, generated
  manifest, and unexpected-data paths. It is a release guard, not a generic
  secret scanner or malware scanner; the retained artifact still deserves
  human inspection before distribution.
- Locked NuGet/npm graphs make resolution reproducible. NuGet audit reports
  known low-or-higher advisories during restore, and
  `npm audit --audit-level=low` fails on them for both frontends. Advisory feeds
  can change independently of the source tree; that is expected security input,
  not nondeterministic package resolution.

## 1.0.1 review items resolved on 2026-08-16

All five concrete candidates recorded by the pre-release review were addressed;
the second pass also closed a renderer entrance race:

- actor destruction now destroys and removes its Phaser collider;
- post-commit notifications are ordered, contain send failures, and attempt an
  authoritative overlay/admin resync without changing a committed command into
  a false HTTP failure; bounded notification/reconciliation deadlines keep the
  ordered tail moving after a hung transport;
- the visual-window check and reservation occur within the mutation gate, with
  a non-zero-duration concurrent-transition test;
- deferred spawns share the ordered notification queue, while arrivals during a
  persisted death are held off screen and revalidated by reincarnation/undo;
- overlapping entrances settle as one bounded group before global transitions,
  so raid throughput does not create mid-air deaths;
- initial and post-reconnect overlay state pulls retry, with an additional
  30-second reconciliation pull while connected.

The visual-window, notification-failure/timeout, repeat-redelivery,
held-participant undo/migration-backfill, stale session-start/undo snapshots,
Twitch admission retry success/exhaustion/cancellation, invalid-stage,
admission-cap, cross-checkout LocalDB, and verified SQL-backup cases now have
focused tests. Browser rendering, OBS caching, Twitch reconnect/token rotation,
and restoring a real backup still require operator-level validation; unit tests
do not prove them.

## Pending / deferred

- **No durable event outbox or browser acknowledgement.** A process exit after
  a database commit can lose an in-memory notification. Periodic state pulls
  converge a loaded overlay, but the animation itself is not replayed and a
  failed initial OBS page load still needs a manual refresh.
- **Backpressure is intentionally lossy at the outer bound.** The EventSub
  admission queue holds 256 messages; retries occupy its single consumer, so
  sustained failures or overload can fill it and drop new chat. Exhausting the
  three-attempt policy also abandons that admission. Both paths are logged, but
  there is no disk spool and Twitch does not replay those messages.
- **No durable admin-command ledger.** The browser's expected-session and
  expected-undo headers reject stale snapshots for those two commands only.
  Native clients can omit them, and they do not deduplicate the other admin
  commands or survive as durable command receipts.
- **Local-only, unauthenticated control plane.** Loopback, browser-origin, and
  host checks are the security boundary. Do not expose DigiChat through a LAN
  bind, tunnel, proxy, or service account without adding authentication and a
  new threat model.
- **Backups need operational proof.** Migration backups block schema changes on
  write failure, but they are not automatically copied off-project, retention
  managed, or test-restored. `RESTORE VERIFYONLY WITH CHECKSUM` runs at creation
  but does not prove the operational restore procedure.
- **Client/runtime observability is log-first.** A failed reconciliation is
  logged, but there is no persistent operator alert or metrics service.
- **Species-specific eggs, deeper undo, bot-management UI** — data model
  allows, V1 intentionally omits (spec §17, §32).
