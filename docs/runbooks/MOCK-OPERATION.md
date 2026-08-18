# Mock-operation runbook

This is the canonical procedure for running DigiChat without Twitch. Development
startup reasserts both Twitch mock mode and the canonical
`data/db/DigiChat.Mock.mdf` connection after every local/environment/command-line
configuration source. A normal `dotnet run` therefore cannot reach the
Twitch connection or live database through a stale override.

## Before starting

1. Complete the clean-clone steps in [VERIFY.md](VERIFY.md). A .NET server can
   start without the gitignored frontend builds, but `/admin/` and `/overlay/`
   will then be missing.
2. Confirm DigiChat is not already running. A per-Windows-session
   process guard, port 5170, and the attached LocalDB file all enforce one-copy
   operation, but they do not authorize stopping somebody else's process.
3. Keep the gitignored `src/DigiChat.Api/appsettings.Local.json` limited to the
   documented local Twitch identity/config values. Development ignores stale
   mode and connection-string overrides by reasserting both safety values, but
   those keys still make Production configuration ambiguous and do not belong
   in the file.
4. Never use `start-digichat.bat` or set the environment to `Production`.

On Windows, this read-only check shows a process already listening on the port:

```powershell
Get-NetTCPConnection -LocalPort 5170 -State Listen -ErrorAction SilentlyContinue
```

If it returns a listener, identify it and stop only the DigiChat process you own.
Do not kill an unknown process to make the port available.

## Start mock mode

You can double-click `start-digichat-mock.bat`. From a terminal or an agent
process runner, use:

```text
dotnet run --project src/DigiChat.Api
```

The launch profile sets `ASPNETCORE_ENVIRONMENT=Development`. Startup reasserts
the canonical `DigiChat.Mock.mdf` connection and mock mode for that environment
after local, environment-variable, and command-line providers. Before driving
the app, confirm the log identifies the Development environment and includes:

- `Dev/mock chat endpoints enabled at /api/dev/chat`
- `Database folder: ...\data\db`

`src/DigiChat.Api/appsettings.Development.json` documents the same mapping. If
the environment is not Development, stop immediately rather than probing it
with a mock request.

URLs:

- Admin: <http://localhost:5170/admin/>
- Overlay: <http://localhost:5170/overlay/>
- Overlay state: <http://localhost:5170/api/overlay/state>
- Admin status: <http://localhost:5170/api/admin/status>

Start a new **overlay session** from the admin before expecting chatters to
appear. This is local session state; it does not start a Twitch stream.

## Drive it

The admin page's Dev / Mock chat panel is the simplest manual path. For scripted
checks, these examples use Git Bash syntax:

```bash
curl -sS -X POST http://localhost:5170/api/admin/session/start
curl -sS -X POST http://localhost:5170/api/dev/chat \
  -H 'Content-Type: application/json' \
  --data '{"login":"alice","displayName":"Alice"}'
curl -sS -X POST 'http://localhost:5170/api/dev/chat/bulk?count=8'
curl -sS -X POST http://localhost:5170/api/admin/stage/champion
curl -sS -X POST http://localhost:5170/api/admin/kill
curl -sS -X POST http://localhost:5170/api/admin/reincarnate
curl -sS -X POST http://localhost:5170/api/admin/undo
```

These native `curl` requests intentionally have no browser `Origin` and are
accepted for automation. A browser `fetch` carries an Origin and must also send
`X-DigiChat-Command: 1`; the committed admin UI does this. Do not weaken the
server check to make an ad-hoc browser script work.

For session start, the admin UI also sends `X-DigiChat-Expected-Session` from
the status response's `sessionNumber` (or `0` when there is no session). For
undo, it sends `X-DigiChat-Expected-Undo` from `lastUndoableTransitionId` when
one exists. The backend compares these optimistic tokens inside the mutation
gate and returns an unsuccessful result rather than changing newer state when a
snapshot is stale. The native examples above intentionally omit the optional
headers and therefore do not get that stale-snapshot protection. A script that
opts in should fetch `/api/admin/status` immediately before the command and
refresh status after any result; these headers are not reusable idempotency
keys or a durable command ledger.

Always send a distinct `login`. When `userId` is omitted it becomes
`mock-<login>`; requests with only `displayName` collapse into the same
`mock-user` and make spawning appear broken. Sending the same login twice is the
correct returning-viewer test and must not create a second sprite.

The bulk endpoint clamps `count` to 1–100, and a session admits at most
`Admissions:MaxParticipantsPerSession` unique viewers (500 by default). Those
bounds are expected safety behavior, not partial success to retry around.

Stages are `fresh`, `inTraining`, `rookie`, `champion`, and `ultimate`.

Transition visual windows intentionally reject another transition for about
4 seconds after a stage change or death and 12 seconds after reincarnation.
Wait for the window instead of treating that response as a transient request to
retry aggressively.

## What to inspect

- Open the overlay as a rendered page. It is a canvas, so DOM text inspection is
  not a substitute for a screenshot or visible browser check.
- Check the browser console for asset, layout, SignalR, or Phaser errors.
- Reload the overlay and confirm it reconstructs final state without replaying
  entrance animations.
- Exercise death, a chatter arriving during death, reincarnation, and undo when
  those paths are in scope.
- Placeholder blobs are correct in a clean public clone. Real Digimon art is
  user-supplied and gitignored.

## Shut down completely

1. Stop the exact server you started (`Ctrl+C`, or the stop operation belonging
   to the process runner).
2. Confirm port 5170 is no longer listening.
3. Leave the shared `MSSQLLocalDB` instance running. Normal application shutdown
   disposes DigiChat's connections and releases its file use; stopping the
   entire instance can interrupt unrelated LocalDB projects owned by the same
   Windows user.

Do not detach a catalog as routine cleanup. If a file remains locked or the next
startup reports a database error, use
[DATABASE-RECOVERY.md](DATABASE-RECOVERY.md) to inspect the exact catalog and
physical paths before considering a targeted detach.

Do not leave a background server running on someone else's behalf. The successful end state
of a mock session is: no DigiChat process and no port-5170 listener. The shared
LocalDB instance may still be running, which is normal.
