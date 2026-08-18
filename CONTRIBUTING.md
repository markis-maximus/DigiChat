# Contributing to DigiChat

DigiChat is a Windows-only local application that runs live in front of a
streamer's audience. Reliability, recoverability, and clear operator behavior
matter more than architectural novelty.

## Start here

Read these before changing code or data:

1. `AGENTS.md` — architecture, boundaries, and high-risk constraints
2. `docs/DECISIONS.md` — rationale for non-obvious behavior
3. `docs/runbooks/VERIFY.md` — clean-clone setup and the complete verification
   checklist
4. `docs/HANDOFF.md` — dated workstation status, not a universal runbook

The authoritative private concept document is not part of the repository.
`spec §n` comments are provenance markers only. If a rule matters to a change,
it must be understandable from committed code and documentation.

## Development environment

Required:

- Windows 10/11
- the .NET SDK selected by `global.json` (currently the 10.0.302 feature band)
- Node.js 20+ (CI pins 24.18.0)
- Windows PowerShell 5.1
- SQL Server 2022 LocalDB for production-path integration testing

Follow the clean-clone commands in `docs/runbooks/VERIFY.md`. Both frontend build
outputs are gitignored, so a backend-only build does not create the admin or
overlay pages.

The repository does not currently carry a local `dotnet-ef` tool manifest. Only
contributors creating a migration need the EF CLI; confirm `dotnet ef --version`
matches EF Core 10 before using the migration command in `CLAUDE.md`. Ask before
installing system-level tools.

## Sources of truth

- `data/lineages.json` is the roster. Do not duplicate roster order, names, or
  asset keys in a tool when they can be derived from this file.
- `data/layout.json` is OBS canvas geometry. Discuss it in OBS terminology with
  a human.
- `src/DigiChat.Overlay/public/assets/overrides.json` contains committed human
  visual judgments.
- `src/DigiChat.Overlay/public/assets/manifest.json` is generated and must not be
  hand-edited or committed.
- `docs/runbooks/` contains canonical operational procedures. Vendor-specific
  skills are adapters, not competing copies.

## Safety rules

- Never use live Twitch mode for development or verification.
- Keep `appsettings.Local.json` limited to the documented local Twitch values.
  Development reasserts both mock mode and the canonical mock database after
  every other configuration source, so stale overrides cannot reach Twitch or
  live history through normal `dotnet run`. Do not put mode or connection-string
  keys there anyway; they make Production behavior ambiguous.
- Only one DigiChat process may own port 5170 and the attached LocalDB file.
- If you start mock mode, follow `docs/runbooks/MOCK-OPERATION.md` through its
  shutdown section.
- History is append-only. Do not delete transition records to repair state.
- Reincarnation is deliberately final.
- Do not reset, detach, replace, or drop the live database without explicit
  human approval and a verified backup.
- Do not commit viewer logs, databases, local Twitch configuration, or tokens.

## Art and generated files

Digimon artwork is user-supplied Bandai IP and is never part of the source
repository. Do not commit anything under the ignored sprite or sheet folders.
Raw asset-package reports and mapping files are local inputs, not public assets.

After a roster rename, preserve the corresponding sprite folder and every
hand-tuned override, then run the name checker and importer as described by the
repository skills. A missing override can silently undo hours of visual tuning.

## Verification

Run the shared local/CI entry point, not only the project you edited:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Verify-Repository.ps1
```

Stop DigiChat first: the run rebuilds `src/DigiChat.Api/wwwroot/` in place, and
it cannot build while a running app holds its own DLL. Its complete behavior and
result interpretation are in `docs/runbooks/VERIFY.md`. For visual changes, also perform the relevant
mock/OBS check. Report LocalDB skips explicitly.

For a distributable package, add `-KeepPublish` and inspect the retained
`artifacts/public-verify` tree. The publish allowlist and artifact checker block
the repository's known local/proprietary file classes, but they are not a
generic secret or license scanner.

Keep changes surgical and avoid unrelated formatting churn. Update durable docs
when behavior, paths, data contracts, prerequisites, or operator steps change.
Do not cite an unreachable pre-squash commit as the only explanation for a rule.

## Commit identity and privacy

Git commits permanently expose their author and committer names and emails.
Before committing, configure the repository-local identity intended for the
public GitHub account and verify it with:

```text
git config --local user.name
git config --local user.email
git log --all --format=fuller
```

Use the account's GitHub privacy/noreply address when email privacy is desired.
Do not push a commit containing a private address and assume a later squash will
erase an earlier reachable commit.

## Pull-request checklist

- [ ] The change is understandable without the private concept document.
- [ ] Backend build/tests, both frontend builds, and the roster checker pass.
- [ ] The public-mode publish and artifact check pass.
- [ ] LocalDB integration skips, if any, are called out.
- [ ] Relevant mock behavior was inspected visually.
- [ ] No secret, viewer data, DB, log, generated output, or proprietary art is tracked.
- [ ] Operator and recovery documentation matches the new behavior.
- [ ] The owned server process and port 5170 were released after manual testing;
      the shared LocalDB instance was not stopped as routine cleanup.
