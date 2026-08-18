# DigiChat

Your Twitch chat, as a screen full of Digimon.

Everyone who talks in chat gets their own Digimon. It drops into your scene with
their name over its head and starts wandering around, hopping between platforms.
You decide when the whole cast digivolves — Fresh, In-Training, Rookie, Champion,
Ultimate — and everyone evolves together into the next form of their own lineage.

When a generation has run its course you kill it. Everyone dies where they stand
and holds there as a dark silhouette until you are ready. Then you reincarnate
them: the corpses become eggs, the eggs hatch, and a brand-new cast starts over
as Fresh with freshly dealt lineages. It is themed around a Digimon World 1
playthrough.

> **Unofficial fan project.** Digimon and all related names, characters, and
> media are trademarks of Bandai. This is a non-commercial fan project, not
> affiliated with, sponsored by, or endorsed by Bandai or Toei Animation, and it
> claims no ownership of their material. **No Digimon artwork is included in
> this repository** — you supply your own sprites, and the folders they live in
> are gitignored. Out of the box, chatters appear as procedurally drawn
> placeholder blobs.

This started as a weekend vibecoded project for one streamer's own channel and
grew from there. It has been hardened well past what a weekend usually produces
— there is a real test suite, a one-command verification gate, and runbooks for
when the database misbehaves — but that is where it came from, and it is worth
knowing before you judge a design decision too harshly.

## What you need

- **Windows 10 or 11.** Windows-only by design: it uses DPAPI to encrypt your
  Twitch token and SQL Server LocalDB to store history.
- **OBS Studio 29+** — the overlay is a Browser Source.
- **.NET 10 SDK** — the exact version is pinned by `global.json`.
- **Node.js 20+** — only needed to build the two frontends.
- **SQL Server 2022 LocalDB** — ships with SQL Server Express, or
  `winget install Microsoft.SQLServer.2022.LocalDB`.
- A Twitch account, plus a free application registration (walked through below).

## Quick start

```
dotnet build
cd src/DigiChat.Overlay && npm ci && npm run build
cd ../DigiChat.Admin   && npm ci && npm run build
cd ../..
dotnet run --project src/DigiChat.Api
```

Then open **http://localhost:5170/admin/** and click **Start New Overlay
Session**.

The database creates itself on first run — there is no migration step and no
`dotnet ef` tool to install.

Two things to expect on a fresh clone: nothing appears until someone chats, and
when they do they will be **placeholder blobs**, not Digimon. See
[Adding your own art](#adding-your-own-art). To try it without Twitch at all,
skip to [Trying it without Twitch](#trying-it-without-twitch).

## Twitch setup

1. Go to https://dev.twitch.tv/console/apps and **Register Your Application**.
   - Name: anything, e.g. `DigiChat overlay`
   - OAuth Redirect URL: `http://localhost` (required by the form, unused here)
   - Category: Broadcaster Suite
   - **Client Type: Public** — this one matters. It enables the device code
     flow, so there is no client secret to look after.

2. Create `src/DigiChat.Api/appsettings.Local.json` (it is gitignored) and put
   your Client ID in it:

```json
{
  "Twitch": { "ClientId": "your_client_id_here" }
}
```

Keep that file limited to the Twitch values. Do not add mode or
connection-string keys — they make live behaviour ambiguous, and the two
launchers already choose live versus mock for you.

3. Start the app. The console prints something like
   `TWITCH AUTHORIZATION REQUIRED: open https://www.twitch.tv/activate and enter code ABCD-EFGH`.
   Do that once, signed in as **the broadcaster account**.

Your token is stored DPAPI-encrypted in `twitch-tokens.json` and refreshed
automatically, so this is a one-time step. DigiChat requests `user:read:chat`
and nothing else.

If the app is disconnected when someone chats, Twitch does not replay it — that
chatter simply gets their Digimon on their next message.

## OBS setup

Add a **Browser** source to your scene:

| Setting | Value |
|---|---|
| URL | `http://localhost:5170/overlay/` |
| Width | your canvas width, e.g. 1920 |
| Height | your canvas height, e.g. 1080 |
| FPS | 30 (tick "Use custom frame rate") |
| Shutdown source when not visible | **off** |

**Set Width and Height in the source's own properties. Do not resize it by
dragging or scaling the transform.** The platform positions in `data/layout.json`
are written in OBS canvas pixels, so the overlay only lines up when the source
renders at canvas size. A stretched source puts Digimon in the wrong places and
makes them blurry. If that has already happened: right-click the source →
Transform → Reset Transform, then set Width and Height in Properties.

The page is transparent — only Digimon and their names render — so position the
source to cover the whole canvas.

**Start DigiChat before OBS.** If the Browser Source loads while the backend is
down, it shows a dead page and stays dead; its retry logic lives inside the page,
so it cannot help if the page never loaded. If you ever open a stream to an empty
overlay, refresh the source once and it comes back.

### Every stream, in this order

1. Run `start-digichat.bat` and wait for the admin panel to read
   **Connected (listening to #yourname)**.
2. Open OBS, or refresh the Browser Source if OBS was already running.
3. Click **Start New Overlay Session…**.

Step 3 is not optional. Restarting the app deliberately does *not* start a new
session — that way a mid-stream crash and restart never wipes your roster. The
trade-off is that until you click it, the previous stream's session is still
open and its chatters reappear the moment the overlay loads. Starting a session
clears the screen so everyone has to chat again, which is what you want at the
top of a stream.

## Running a generation

The five stage buttons digivolve everyone at once. The rest of the loop follows
Digimon World 1:

- **Kill All…** — every Digimon on screen dies. They hold as dark silhouettes,
  at whatever stage they died at, for as long as you like.
- **Reincarnate…** — the corpses become eggs and hatch as a brand-new
  generation with lineages dealt fresh. Locked until you kill.
- **Undo** — takes back a death, and everyone stands back up with the lineage
  they never actually lost. It will **not** take back a reincarnation. That one
  is final by design, because the new generation's lineages may already have
  been handed to viewers who arrived afterwards.

Anyone who chats while the generation is dead is recorded but held off screen —
no lineage, no sprite — and walks in when the new generation hatches, so nobody
appears among the corpses. The admin panel shows how many are waiting.

There are 30 lineages, so 30 chatters can be on screen at once. The 31st is
recorded and gets a Digimon at the next reincarnation.

## Adding your own art

Sprites go in one folder per form, named after the Digimon in lowercase:

```
src/DigiChat.Overlay/public/assets/sprites/
  agumon/
    idle.png        <- the only file that is required
    walk.png
    airborne.png    <- covers jump and fall
```

Then double-click **`import-assets.bat`**. It measures every PNG, writes the
asset manifest, and rebuilds the overlay. Refresh your Browser Source and the
art is live.

Only `idle.png` is required. Everything else falls back to it, and a form with a
single static PNG still walks, jumps, digivolves and hatches, because those
effects are code-driven rather than frame animation.

Two things worth knowing:

- The art is Bandai's and is **gitignored**. Never commit anything under
  `sprites/` or `sheets/`.
- If a Digimon renders too big, too small, or facing the wrong way, fix it in
  `public/assets/overrides.json` — never by editing the generated manifest. See
  [docs/ASSET-TUNING.md](docs/ASSET-TUNING.md).

## Where the Digimon walk

`data/layout.json` holds your platform surfaces in OBS canvas pixels. The shipped
values describe one specific 1920×1080 scene, not neutral defaults — measure
yours and replace them. No code changes needed.

Set `"debug": true` in that file to draw the collision surfaces and spawn zone
on screen while you dial it in, then set it back to `false` before streaming.

## Trying it without Twitch

`start-digichat-mock.bat` runs the whole app with Twitch replaced by a
**Dev / Mock chat** panel in the admin page, so you can fake chatters, digivolve
them, kill them and reincarnate them without going live.

Mock mode uses a **separate database**, so test chatters never end up in your
real stream history. The full procedure, including how to shut it down cleanly,
is in [docs/runbooks/MOCK-OPERATION.md](docs/runbooks/MOCK-OPERATION.md).

## Troubleshooting

- **Overlay shows nothing** — did you start an overlay session? Chatters only
  appear after their first message *in the current session*. Check
  http://localhost:5170/api/overlay/state: if people are listed there, the
  backend is fine and the problem is the OBS source.
- **Digimon in the wrong places, blurry, or off-screen** — the Browser Source is
  not rendering at canvas size. See [OBS setup](#obs-setup).
- **Everything vanished after an OBS reload** — expected for a moment. The
  overlay reconnects and restores the current state without replaying
  animations.
- **Twitch status stuck on "Authenticating…"** — finish the device-code prompt.
  Codes expire after about 15 minutes; restart the app for a fresh one.
- **Reincarnate is locked** — it only follows a death. Use **Kill All…** first.
  And once you have reincarnated, Undo will not roll it back; that is
  deliberate.
- **Buttons greyed out** — a transition is animating. They unlock after a few
  seconds.
- **A bot keeps getting a Digimon** — put its Twitch user ID in
  `Twitch:IgnoredUserIds` in `appsettings.Local.json` and restart.
- **"DigiChat is already running for this Windows session"** — close the copy
  you own. Only one instance can hold the database and port 5170.
- **Any database error** — SQL 5170, SQL 1801, "being used by another process",
  or a failure during the pre-migration backup — stop the app and follow
  [the database recovery runbook](docs/runbooks/DATABASE-RECOVERY.md). An
  existing `.mdf` may be real stream history; preserve it before any reset.

Logs live in `logs/digichat-*.log`. Tokens are never logged, but chatter display
names and Twitch user IDs are — treat them as viewer data, and redact before
pasting them anywhere public.

## Under the hood

One ASP.NET Core process on `http://localhost:5170` hosts everything: the admin
API, a SignalR hub, and both built pages. The backend is authoritative — the
overlay only mirrors state and re-pulls it on every reconnect, so OBS reloads
and crashes always rebuild cleanly.

Roster, geometry and sprite corrections are all data — `data/lineages.json`,
`data/layout.json`, `public/assets/overrides.json` — and none of them need a code
change.

| Where | What |
|---|---|
| [AGENTS.md](AGENTS.md) | Architecture map and the constraints that bite |
| [docs/DECISIONS.md](docs/DECISIONS.md) | Why things are the way they are |
| [docs/runbooks/](docs/runbooks/) | Verification, mock operation, database recovery |
| [docs/ASSET-TUNING.md](docs/ASSET-TUNING.md) | Sprite sizing and facing |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Setup, conventions, verification gate |
| [SECURITY.md](SECURITY.md) | Security boundary and private reporting |

### Adding a lineage

`data/lineages.json` is the roster, seeded at startup and matched by `slug`.
Entries removed from the file are disabled, never deleted. One entry:

```json
{
  "slug": "agumon-family",
  "name": "Agumon Family",
  "orderIndex": 1,
  "enabled": true,
  "forms": {
    "fresh": "Botamon",
    "inTraining": "Koromon",
    "rookie": "Agumon",
    "champion": "Greymon",
    "ultimate": "MetalGreymon"
  }
}
```

Startup validates the file and refuses to run if it is wrong, so a mistake costs
a restart, not data. `slug` must be unique, `orderIndex` must be unique and
between 1 and 500, and all five stage names are required — copying an entry as a
template and forgetting to change both keys is the usual first mistake. Run
`check-names.bat` afterwards to verify the names and their art folders.

## Contributing

Issues and pull requests are welcome. Please read
[CONTRIBUTING.md](CONTRIBUTING.md) first; the short version is that everything
goes through one verification command, and a few things are deliberately out of
scope — anything that ships artwork, or that makes DigiChat non-local.

**Filing an issue?** Logs contain chatter display names and Twitch user IDs, and
database files contain viewer history. Redact before attaching, and reproduce in
mock mode where you can. Security issues go through the private advisory flow on
the Security tab, not a public issue.

## License and IP

The repository is not uniformly licensed, because not all of it is mine to
license:

| What | License |
|---|---|
| DigiChat code, scripts, configuration, workflows, documentation | MIT — see [LICENSE](LICENSE) |
| `data/digimon-names.json` | Curated name reference; [Wikimon](https://wikimon.net/) portions and adaptations are [CC BY-SA 3.0](https://creativecommons.org/licenses/by-sa/3.0/) — see [LICENSE](LICENSE) and the file's own metadata |
| `data/lineages.json` | MIT as to curation and structure; the names within are Bandai trademarks |
| Digimon sprite art | **Not included.** Bandai's, gitignored, supplied by you |

The MIT licence covers the code and does not restrict commercial use. Separately
— and this is practical risk guidance, not a licence term — fan projects are
tolerated in proportion to how little they look like competing with the rights
holder. Keeping a fork non-commercial, shipping no artwork, and packaging no
official logos or wordmarks is what keeps it in that territory. Those choices
are yours, and the consequences are between you and Bandai.
