---
name: check-names
description: Verify the Digimon names in data/lineages.json against a vendored reference of ~1500 known species, and rename a form safely. Use for "run check-names", "check the roster", "is that the right Digimon name", "rename a Digimon", or whenever data/lineages.json form names are edited.
---

# Checking and renaming Digimon names

## Run it

```bash
node src/DigiChat.Overlay/tools/check-names.mjs
```

(`check-names.bat` at the repo root does the same and pauses when run by double-click. `--refresh` re-downloads the reference list — needs
internet, and rewrites `data/digimon-names.json`.)

Six checks: names absent from the reference, shouted capitals, spaces or
underscores, duplicate species, forms whose art folder is missing, and orphaned
art folders left behind by a rename. Clean output ends with "Nothing to look at."

## The naming convention

- **English release name** where one exists — Gatomon, not Tailmon; Growlmon,
  not Growmon; Chirinmon, not Tyilinmon. For species that never left Japan
  (Zubamon, Ryudamon, Liollmon) the romanization *is* the English name.
- **CamelCase compounds, no spaces**: MetalGreymon, LadyDevimon, DoruGreymon.
- Asset key = the name lowercased with non-alphanumerics turned to `-`
  (`LineageSeeder.Slugify`, mirrored in the tools).

## What the reference can and cannot tell you

`data/digimon-names.json` vendors two public lists. The big one (1,488 entries)
is **Japanese-primary** — it holds Piyomon, not Biyomon — and inserts its own
spaces ("Metal Greymon"), so it proves a name **exists** and says nothing about
how it should be styled. The English list is only 209 entries with whole
evolution lines missing, so absence from it proves nothing.

English names neither list carries are recorded under `englishOnly`, each
mapped to the Japanese counterpart it was verified against. **Add to that map
rather than weakening a check** when you confirm a new English name.

## Renaming a form (the part that bites)

A rename changes the asset key, and the art does not follow by itself. All of
this, or the sprite silently disappears:

1. `data/lineages.json` — the form name, and any mention in that lineage's `notes`.
2. `src/DigiChat.Overlay/public/assets/sprites/<oldkey>/` → `<newkey>/`.
3. `src/DigiChat.Overlay/public/assets/overrides.json` — the key, **with its
   hand-tuned values** (`scaleMultiplier`, `facesLeft`, …). Losing these
   silently undoes hours of the hand-tuned values; verify they survived.
4. `docs/ASSET-TUNING.md` — the numbering tables.
5. Regenerate and rebuild from the repository root:
   `node src/DigiChat.Overlay/tools/import-assets.mjs`, then
   `npm run build --prefix src/DigiChat.Overlay`.
6. Re-run this checker: it should report no orphaned or missing folders.

The seeder overwrites form names and asset keys from `data/lineages.json` on
every startup, so a rename reaches existing databases automatically — but only
on the **next launch**. A running app still shows the old name.
