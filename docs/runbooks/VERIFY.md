# Verification and public-publish runbook

This is the canonical, tool-neutral verification procedure for DigiChat. Run
commands from the repository root. Platform-specific agent skills should point
here rather than carrying a second copy of the rules.

## Prerequisites

- Windows 10/11
- the .NET SDK selected by `global.json` (currently the 10.0.302 feature band,
  with latest-patch roll-forward)
- Node.js 20+, with npm (CI pins Node 24.18.0)
- Windows PowerShell 5.1 (`powershell.exe`)
- SQL Server 2022 LocalDB for the production-database integration tests

Confirm the tools the current shell will use:

```text
dotnet --info
node --version
npm --version
sqllocaldb info
```

`sqllocaldb` may not be on `PATH`. SQL Server 2022 normally installs it at
`C:\Program Files\Microsoft SQL Server\160\Tools\Binn\SqlLocalDB.exe`.

## One-command clean-clone and release verification

From a fresh clone, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Verify-Repository.ps1
```

That script is also the GitHub Actions entry point. It performs, in order:

1. locked NuGet restore from committed `packages.lock.json` files, with NuGet
   audit enabled for all dependency levels at low severity or higher;
2. `npm ci` for both frontends from their committed lockfiles;
3. `npm audit --audit-level=low` for both frontend dependency graphs;
4. TypeScript/Vite builds for the overlay and admin;
5. roster-name validation;
6. Release build and the complete .NET test suite;
7. a public-mode publish into `artifacts/public-verify`; and
8. independent inspection of that artifact for required files and known
   private/proprietary file classes.

On success, the temporary publish directory is removed. On failure, it is left
in place for inspection. To retain a verified artifact deliberately:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Verify-Repository.ps1 -KeepPublish
```

The retained output is `artifacts/public-verify`. The **public-mode publish
phase** stages its frontend builds in `artifacts/public-overlay` and
`artifacts/public-admin`, so that phase never touches
`src/DigiChat.Api/wwwroot/` or strips the local sprite art. The full verification
script still begins with ordinary frontend builds; those empty and rebuild the
served `wwwroot/overlay` and `wwwroot/admin` directories. Never run repository
verification while DigiChat or its OBS Browser Source is serving/on stream.
All of `artifacts/` is gitignored.

Those ordinary builds include `public/`, so a completed run leaves the served
overlay whole — manifest and every sprite folder back in place. The hazard is
the window *during* the run, not the state after it. The script builds the
overlay first and the admin second, so an interrupted run can leave either one
incomplete. Rebuild both before streaming:

```powershell
npm run build --prefix src\DigiChat.Overlay
npm run build --prefix src\DigiChat.Admin
```

`-SkipDependencyInstall` is only for a workspace whose two `node_modules` trees
were already created by `npm ci` from the current lockfiles; it does not skip
frontend builds.

## Interpret the result

- The service tests use in-memory SQLite and should run everywhere.
- The integration tests exercise migrations, constraints, persistence, and
  cross-checkout catalog safety against LocalDB. They skip when LocalDB is
  unavailable. A skip is not proof that the production database path works;
  release verification must run them on Windows with LocalDB.
- Frontend `build` runs TypeScript checking before Vite emits files. The Admin
  build additionally runs `tools/test-command-guard.mjs` first, so a failure in
  the admin's pre-status command guard stops verification before any output is
  written.
- Dependency audits query current advisory data, so an npm audit can fail—or a
  NuGet restore can report a new audit warning—after a locked graph was
  previously clean. Inspect and resolve or explicitly review the advisory; do
  not silently ignore it because the source tree did not change.
- The roster checker should end with `Nothing to look at.`
- The artifact check proves only the explicit conditions in
  `scripts/Test-PublicArtifact.ps1`; read its limits under “Public artifacts”
  below.

If behavior visible in OBS or the admin panel changed, continue with
[mock operation](MOCK-OPERATION.md). Automated tests do not verify canvas
rendering, OBS caching, or transition timing.

## Targeted inner-loop commands

These are useful while developing, but they do not replace the one-command
release verification:

```text
dotnet build
dotnet test
npm run build --prefix src/DigiChat.Overlay
npm run build --prefix src/DigiChat.Admin
node src/DigiChat.Overlay/tools/check-names.mjs
```

Use `npm ci`, not `npm install`, to reconstruct dependencies for an unchanged
checkout. It installs exactly what the committed lockfiles describe and fails
if a lockfile has drifted from its package manifest.

## Repository and identity hygiene

Before committing, inspect both tracked changes and ignored local state:

```text
git status --short
git status --short --ignored
```

Expected ignored material includes `node_modules`, `bin`, `obj`, built
`wwwroot`, `data/db`, logs, `appsettings.Local.json`, `twitch-tokens.json`, the
generated asset manifest, and user-supplied sprite art. None belongs in a
source commit.

For a final source-release and identity check, inspect the exact tracked set and
every reachable commit rather than assuming `.gitignore` or a later squash will
hide something:

```text
git ls-files
git log --all --format=fuller
git config --local user.name
git config --local user.email
```

The tracked set must not contain Twitch tokens, local configuration, databases,
logs, generated frontend output, raw asset-pack drops, or Digimon artwork. Every
reachable public commit must use the intended public author/committer identity.

## Public artifacts

The preferred distributable build is the retained artifact from the full
verification command above. For a custom output directory, ordinary publish is
also guarded:

```text
dotnet publish src/DigiChat.Api/DigiChat.Api.csproj --configuration Release --output artifacts\public
```

The project publish target runs `npm ci`, builds the overlay in public mode
(which disables Vite's local `public/` tree), builds the admin, publishes only
allowlisted configuration/data, and then invokes
`scripts/Test-PublicArtifact.ps1`. Do not set `SkipFrontendRestore=true` for an
ordinary manual publish; that property exists for the repository verification
script after it has already run both `npm ci` steps.

The artifact checker requires the API, both frontend entry points and bundles,
`data/lineages.json`, and `data/layout.json`. It rejects known token/local-config
names, database and log files, sprite/sheet directories, the generated sprite
manifest, raw asset-package inputs, and unexpected files under `data/` or the
overlay asset output.

That is a strong allowlist and regression guard, not a generic secret scanner,
license audit, malware scan, or proof that arbitrary text inside an otherwise
allowed file is safe. Inspect the retained artifact and its configuration before
distribution. A source-only GitHub push and a downloadable runtime package are
different release operations.

## Do not verify against live mode

Never use `start-digichat.bat` or `ASPNETCORE_ENVIRONMENT=Production` for tests.
Live mode connects to Twitch and real stream history. Use the mock runbook,
then stop the exact process and confirm port 5170 is free. Leave the shared
LocalDB instance running.
