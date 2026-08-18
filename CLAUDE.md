# DigiChat — agent instructions

Twitch chatters rendered as Digimon in an OBS Browser Source. Local Windows app,
run by one streamer on their own machine. The original design document remains
on this workstation and is unavailable to agents; committed code and docs
must be sufficient for every working rule. Read docs/HANDOFF.md for dated local
status and docs/DECISIONS.md for why things are the way they are.

## Working style

- **Prefer surgical edits.** A large rewrite is hard to review and hard to
  bisect when it breaks; a small diff against working code is neither.
- **Don't re-verify what already passed.** Re-run a check when something it
  covers has changed, not out of habit.
- **Rebuild only what a change actually affects.** The frontends, the asset
  manifest and the published artifact each have their own trigger.

## Commands

- First clone / full verification:
  `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Verify-Repository.ps1`
  (canonical details and result interpretation: `docs/runbooks/VERIFY.md`).
  **Never while the app or OBS is serving** — it rebuilds
  `src/DigiChat.Api/wwwroot/` in place.
- Retain the verified public artifact: add `-KeepPublish`; output is
  `artifacts/public-verify`
- Run (mock, no Twitch): `dotnet run --project src/DigiChat.Api`; you can
  double-click `start-digichat-mock.bat`. Follow
  `docs/runbooks/MOCK-OPERATION.md`, including shutdown.
- Run (live Twitch): `start-digichat.bat`
- Build backend: `dotnet build` — Tests: `dotnet test` (LocalDB tests auto-skip
  if LocalDB is missing; a skip is not production-path verification)
- Check roster names: `check-names.bat` (verifies data/lineages.json against
  data/digimon-names.json; naming convention is in docs/DECISIONS.md)
- Rebuild a frontend: `npm run build --prefix src/DigiChat.Overlay` or
  `npm run build --prefix src/DigiChat.Admin` (output lands in
  src/DigiChat.Api/wwwroot/, which is gitignored)
- New EF migration: `dotnet ef migrations add <Name> --project src/DigiChat.Infrastructure --startup-project src/DigiChat.Infrastructure`

URLs: admin http://localhost:5170/admin/ · overlay http://localhost:5170/overlay/
(OBS Browser Source URL) · REST under /api/, SignalR hub at /hub.

## Gotchas learned the hard way

- Solution targets **net10.0-windows** (DPAPI) and requires the SDK selected by
  `global.json`: 10.0.302 or later within .NET 10. `rollForward` is
  `latestFeature`, so a newer 10.0.4xx SDK is accepted but an older one and any
  11.x are refused.
  Production-path work and integration tests require SQL Server 2022 LocalDB.
  Confirm before any system-level install; MSI installs require elevation.
- The database is a file attached in place: `data/db/DigiChat.mdf`, via
  `AttachDbFilename=%DBDIR%\DigiChat.mdf` in the connection string; `%DBDIR%` is
  resolved by `DatabaseLocation` (walks up to the folder holding DigiChat.sln).
  Nothing may be written to the user profile. Only one process can attach it at
  a time — **if any automated session runs the app, stop that exact process
  afterwards** and confirm port 5170 is free. Normal shutdown disposes its
  database connections. Do not stop the entire shared LocalDB instance as
  cleanup; other projects may be using it. Do not assume `sqlcmd` exists; the
  portable PowerShell inspection path and targeted-detach safeguards are in
  `docs/runbooks/DATABASE-RECOVERY.md`.
- Agent sessions and the launcher bat files share the **same** MSSQLLocalDB
  instance (verified). Attachment by filename lets a later clean start
  re-attach the same file, but do not detach underneath a running app. A named
  mutex now refuses a second DigiChat in the same Windows session before it can
  initialize the database; port and LocalDB remain backstops. Stop the process
  you own; detach only the exact inspected DigiChat catalog when a recovery
  operation genuinely requires moving its files.
- Mock mode has its own database, `data/db/DigiChat.Mock.mdf` (connection string
  override in appsettings.Development.json). Test chatters must never land in
  the real stream history; before that split they did.
- The .mdf on disk and its registration in the LocalDB instance drift apart in
  BOTH directions, and each breaks startup differently: a file with no
  registration gives SQL 5170 ("cannot create file … already exists"), a
  registration with no file gives SQL 1801 ("database already exists").
  `DatabaseLocation.DropStaleRegistrationAsync` handles the second at startup
  only when the catalog's primary file is this checkout's exact expected path
  and every registered data/log file is absent. Another checkout or any
  surviving physical file is a hard stop.
- Before pending migrations are applied to an existing, connectable LocalDB
  database, startup creates a `COPY_ONLY`/`CHECKSUM` backup under
  `data/db/backups/` and runs `RESTORE VERIFYONLY WITH CHECKSUM`. Failure blocks
  migration. It is sensitive viewer data and is not automatically copied
  off-project or test-restored; use the recovery runbook.
- `DatabaseInitializer` runs EF **migrations on SQL Server** but
  **EnsureCreated on SQLite** (tests) — don't "unify" this, EF flags model
  drift otherwise.
- Phaser 4 tint API: `setTint(color).setTintMode(Phaser.TintModes.FILL)`;
  `setTintFill(color)` no longer exists.
- Browsers/OBS cache hard: HTML is served with no-cache (Program.cs); hashed
  bundles handle the rest. If the overlay looks stale, it's HTML cache.
- `dotnet run` defaults to Development (mock mode) via launchSettings; the
  both bat files pass `--no-launch-profile` and set ASPNETCORE_ENVIRONMENT
  explicitly (Production for live, Development for mock).
- appsettings.Local.json (gitignored) holds the Twitch ClientId. Never commit
  it; never log tokens. Development now reasserts both
  `Twitch:MockMode=true` and the canonical `DigiChat.Mock.mdf` connection after
  all other configuration providers, so stale overrides cannot point ordinary
  `dotnet run` at Twitch or live history. The local file should still not carry
  mode or connection-string keys because they make Production behavior
  ambiguous. twitch-tokens.json is DPAPI-encrypted, also gitignored.
- `dotnet publish` is an explicit allowlist: it builds the overlay in public
  mode without the local `public/` art tree and runs
  `scripts/Test-PublicArtifact.ps1`. Prefer the full verification script with
  `-KeepPublish`; the artifact checker is not a generic secret scanner, so
  inspect retained output before distribution.
- Platform geometry is DATA: data/layout.json in OBS canvas pixels (real
  values are in there now; `debug: true` draws surfaces/walls/spawn zone).
- Layout questions are answered in OBS terminology only, never Phaser or
  physics terms. Confirm before system-level installs.
