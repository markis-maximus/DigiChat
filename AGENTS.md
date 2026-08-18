# DigiChat — orientation for agents

Twitch chatters rendered as Digimon in an OBS Browser Source. A local Windows
app, run by one streamer on their own machine, **live in front of an audience**
— a crash or a visual glitch happens on stream and cannot be quietly retried.

Read `CLAUDE.md` for project working rules, `docs/DECISIONS.md` for why
things are the way they are, and `docs/HANDOFF.md` for dated workstation
status. Canonical operational procedures live in `docs/runbooks/`; they are
tool-neutral and take precedence over platform-specific skill copies.

## The shape of it

```
Twitch EventSub (WebSocket)  ──►  AdmissionService   ──┐
   or /api/dev/chat (Development only)                 │
                                                       ▼
  Admin SPA  ──►  /api/admin/*  ──►  TransitionService ──►  SQL Server LocalDB
  (vanilla TS)                       (gate + undo)          (EF Core, .mdf
                                                       │     attached in place)
                                                       ▼
                                            SignalR hub /hub
                                                       │
                                                       ▼
                                   Overlay (Phaser 4) in OBS Browser Source
```

| Project | What it is |
|---|---|
| `src/DigiChat.Domain` | Entities, enums, and the view records both frontends mirror |
| `src/DigiChat.Infrastructure` | EF Core, migrations, seeding, Twitch client, all services |
| `src/DigiChat.Api` | Minimal API host, SignalR hub, serves both frontends |
| `src/DigiChat.Overlay` | Phaser 4 overlay (Vite/TS) + the asset pipeline in `tools/` |
| `src/DigiChat.Admin` | Admin control panel (Vite/TS, no framework) |
| `tests/` | xUnit; services run against in-memory SQLite seeded from the real roster |

Frontends build into `src/DigiChat.Api/wwwroot/` (gitignored).

## Data is data, not code

- `data/lineages.json` — the roster: 30 lineages × 5 stages = 150 species.
  Seeded on every startup, upserted by slug. Edit freely; no code changes.
- `data/layout.json` — platform geometry in **OBS canvas pixels**.
- `data/digimon-names.json` — vendored reference for the name checker.
- `src/DigiChat.Overlay/public/assets/overrides.json` — hand-tuned per-sprite
  corrections (committed).
- `src/DigiChat.Overlay/public/assets/manifest.json` — generated from the art,
  gitignored, never hand-edited.

## Runnable things

| Task | Command | Skill |
|---|---|---|
| First clone / full verify | `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Verify-Repository.ps1` | — |
| Run in mock mode | `dotnet run --project src/DigiChat.Api` | `mock-testing` |
| Build / test | `dotnet build` · `dotnet test` | — |
| Rebuild a frontend | `npm run build --prefix src/DigiChat.Overlay` | — |
| Check roster names | `node src/DigiChat.Overlay/tools/check-names.mjs` | `check-names` |
| Import sprite art | `node src/DigiChat.Overlay/tools/import-assets.mjs`, then `npm run build --prefix src/DigiChat.Overlay` | `import-assets` |
| Keep a verified public artifact | add `-KeepPublish` to the full-verify command | — |
| Fix a database error | see `docs/runbooks/DATABASE-RECOVERY.md` | `db-recovery` |
| Fix a sprite's size/facing | edit `overrides.json` | `tune-sprites` |

Claude adapters live in `.claude/skills/`. Other agents do not need Claude's
preview tools: follow the linked runbooks with the process/browser controls
available in your environment. A fresh clone has no built frontends; complete
the setup in `docs/runbooks/VERIFY.md` before expecting `/admin/` or `/overlay/`.

URLs: admin http://localhost:5170/admin/ · overlay http://localhost:5170/overlay/
(this is the OBS Browser Source URL) · REST `/api/` · SignalR `/hub`.

## Things that will bite you

- **One process at a time.** The `.mdf` is attached exclusively and port 5170 is
  single-occupancy. A named mutex rejects a second DigiChat in the same Windows
  session before database initialization, but you must still stop the process
  you own afterwards. Do not stop the shared LocalDB instance as routine
  cleanup; follow the mock runbook through shutdown.
- **Never start live mode.** `dotnet run` defaults to Development and startup
  reasserts both mock Twitch mode and the canonical mock database after all
  other configuration sources. Live mode talks to real Twitch and the local
  real history.
- **Browser commands have a guard.** Native tools without an Origin can call the
  local API. Browser-issued POSTs must come from a trusted Origin and include
  `X-DigiChat-Command: 1`; do not weaken this for test automation.
- **Pending migrations back up first.** Existing, connectable LocalDB catalogs
  get a sensitive `data/db/backups/*-pre-migration-*.bak`, followed by
  `RESTORE VERIFYONLY WITH CHECKSUM`; failure blocks the migration. Follow the
  database runbook before moving, restoring, or deleting database material.
- **A verified publish is not a generic secret scan.** Use the repository
  verification script, inspect its retained artifact, and keep public release
  packaging separate from the local runtime/art tree.
- **The art is Bandai's IP** and is gitignored. Never commit anything under
  `sprites/`, `sheets/`, or `manifest.json`.
- **Only one stage is ever on screen** — `AppState.CurrentStage` is global.
  There is no per-participant stage ladder.
- **History is append-only.** Undo stamps `UndoneUtc`; nothing is deleted.
  Reincarnation is deliberately final.
- **Layout is discussed in OBS terms only** —
  never Phaser or physics terms.
