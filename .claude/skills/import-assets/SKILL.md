---
name: import-assets
description: Import or replace Digimon sprite art and regenerate the overlay asset manifest. Use for "import assets", "add art", "the new sprites aren't showing", "regenerate the manifest", or after any file under public/assets/sprites/ is added, replaced or renamed.
---

# Importing sprite art

## Run it

```bash
node src/DigiChat.Overlay/tools/import-assets.mjs
```

then rebuild so the overlay picks it up:

```bash
npm run build --prefix src/DigiChat.Overlay
```

(`import-assets.bat` at the repo root does both; double-click it.)

## The convention

```
src/DigiChat.Overlay/public/assets/sprites/<assetKey>/idle.png
                                                     walk.png
                                                     airborne.png   (jump + fall)
                                                     land.png       (optional)
                                                     getup.png      (optional)
                                           _egg/idle.png            (shared egg)
```

`<assetKey>` is the form name from `data/lineages.json`, lowercased, with every
non-alphanumeric character turned into `-`. Only `idle.png` is required —
everything else falls back (`fall` → `airborne` → `jump` → `idle`, `walk` →
`idle`), and a single static PNG still walks, jumps, digivolves and hatches
because those effects are code-driven.

## Generated facts vs. human judgement

This is the project's governing idea, and it is worth preserving:

- **`manifest.json` is generated and gitignored.** Everything in it is a fact
  discoverable from the files — dimensions, the alpha bounding box of the
  visible pixels, mtimes for cache-busting. Never hand-edit it; it is
  overwritten on every import.
- **`overrides.json` is hand-written and committed.** Anything needing human
  eyes lives here: `scaleMultiplier`, `facesLeft`, `footAnchorY`, collision
  multipliers, and the `_settings` block (stage target heights, size variance,
  snap tolerance). The importer merges it on top.

If a form looks wrong, the fix almost always belongs in `overrides.json`.

## Reading the output

The importer reports every form and warns when art cannot be scaled by a whole
factor within the snap tolerance (12%) — those sprites render with a fractional
scale and slightly soft pixels. That is a *quality* warning about the source
art, not an error; a human decides whether to re-export. Sizing rules and the
tuning workflow are in `docs/ASSET-TUNING.md`.

## Gotchas

- The art is Bandai's IP and is **gitignored** — never commit files under
  `sprites/` or `sheets/`, and never commit `manifest.json`.
- Phaser 4 tint API: `setTint(color).setTintMode(Phaser.TintModes.FILL)`.
  `setTintFill()` no longer exists.
- OBS and browsers cache hard. HTML is served no-cache and bundles are hashed;
  art URLs carry an mtime `?v=`. If it still looks stale, it is HTML cache —
  refresh the Browser Source.
