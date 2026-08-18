# Security policy

## Supported versions

Until versioned maintenance releases exist, security fixes target the current
`main` branch. Older snapshots are not maintained separately.

## Reporting a vulnerability

Do not open a public issue containing exploit details, Twitch tokens, viewer
data, database contents, or private logs. Use the repository's private GitHub
Security Advisory reporting flow (`Security` → `Advisories` → `Report a
vulnerability`) once the repository is published. If private reporting is not
available, contact the maintainer through a private channel listed on the
maintainer's GitHub profile and share only the minimum reproduction data.

Include:

- affected commit/version;
- whether the issue requires a local process, browser page, or remote network;
- reproduction steps using mock mode where possible;
- expected impact and any known workaround;
- sanitized logs with tokens and viewer identifiers removed.

## Security boundary

DigiChat is designed for one streamer on one Windows machine:

- HTTP binds to loopback on port 5170.
- The admin API is intentionally unauthenticated inside that loopback boundary.
- Allowed-host, loopback-bind, Origin, CORS, WebSocket-origin, anti-framing, and
  UI-command-header checks reduce hostile browser access to the local service.
- Twitch uses a Public client and requests `user:read:chat`; no client secret
  exists.
- OAuth tokens are encrypted with Windows DPAPI for the current user.
- Live and mock histories use separate LocalDB files. The chat-simulation
  endpoints exist only in Development, which is also the only environment that
  reasserts the mock connection string, so the two cannot come apart.

These controls do not defend against a process already running as the streamer.
Native requests without an Origin are intentionally accepted for local tooling,
and a same-user process can issue admin commands. DPAPI protects token material
at rest from other user contexts; it is not a defense after the current Windows
account is compromised.

For session start and undo, the browser admin supplies its expected current
session or last undoable transition. The server compares that snapshot inside
the mutation gate and refuses stale/double UI commands before mutation. Native
clients may omit these optional headers. They are not authentication, general
replay protection, idempotency keys, or a durable command ledger.
The admin times out command response/body reads and its follow-up status pull,
then releases its controls. A backend mutation that already entered the gate
may still finish, so the refreshed server status is authoritative after a
client timeout.

Input length checks, a 500-participant default session cap, a 256-message Twitch
admission queue, mock-bulk clamping, and read rate limits bound several known
amplification paths. They are not a general local denial-of-service defense. A
dequeued Twitch admission gets at most three total attempts (immediate, then
after 250 ms and 1 second). Retry exhaustion abandons that message, and retry
delays occupy the single consumer. A full admission queue drops new chat and
logs/surfaces the condition. Neither path has a disk spool, and Twitch does not
replay the message.

Do not bind the service to a LAN/public interface, place it behind a tunnel or
reverse proxy, run it as a Windows service, or weaken origin/host checks without
a new threat model and authentication design. “It is only localhost” is a
security boundary here, not a deployment suggestion.

## Sensitive local data

These files are gitignored and must never be attached to public reports or
commits:

- `src/DigiChat.Api/appsettings.Local.json`
- `src/DigiChat.Api/twitch-tokens.json`
- `data/db/`
- `logs/` for normal repo-root launches (published builds write relative to
  their launch working directory)

Logs contain Twitch display names and user IDs. Database files contain viewer,
generation, lineage, session, and transition history. DPAPI encryption reduces
token exposure but does not make the encrypted file appropriate to publish.

User-supplied Digimon sprites are also excluded from the repository for IP
reasons. `.gitignore` protects source commits only. Public publish now uses a
content allowlist, omits the overlay's local `public/` tree, and runs
`scripts/Test-PublicArtifact.ps1`; retain and inspect the resulting artifact as
described in `docs/runbooks/VERIFY.md`. That targeted checker is not a generic
secret, license, or malware scanner.

## Safe testing

Use `docs/runbooks/MOCK-OPERATION.md`. Never reproduce a report against live
Twitch or the live database when mock mode can demonstrate it. Database
recovery and backups must follow `docs/runbooks/DATABASE-RECOVERY.md`.
