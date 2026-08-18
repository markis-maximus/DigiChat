---
name: mock-testing
description: Run DigiChat locally in mock mode and drive fake chatters through it safely. Use for "start mock testing", "run the app", "test the overlay", "try it with fake chatters", or manual verification of overlay/admin behavior.
---

# Claude adapter: mock testing

The canonical procedure is
`docs/runbooks/MOCK-OPERATION.md`. Read and follow it completely, including
clean-clone setup and shutdown. Do not maintain a second set of API commands or
timings here.

## Claude tool mapping

When preview tools are available:

1. Use `preview_start` with `{name: "digichat"}` from `.claude/launch.json`.
2. Read `preview_logs` and confirm Development/mock mode before sending any
   requests. Development reasserts the canonical mock database and Twitch mode
   after all other configuration sources; stop if the environment is not
   Development.
3. Use the browser tools to inspect both `/admin/` and the rendered `/overlay/`.
   The overlay is a canvas, so capture the visible result and browser console;
   `read_page` alone cannot verify it.
4. Use `preview_stop` with the returned server ID.
5. Confirm the owned process and port 5170 are gone. Do not stop the shared
   `MSSQLLocalDB` instance or detach a catalog as routine cleanup.

If preview tools are unavailable, use the tool-neutral `dotnet run` path in the
runbook. Never substitute live mode.

The one-process rule is absolute. If another process already owns
port 5170, do not start a second server and do not kill an unknown process.
