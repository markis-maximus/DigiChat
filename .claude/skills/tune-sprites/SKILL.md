---
name: tune-sprites
description: Adjust how big Digimon sprites render, how they face, or where their feet sit. Use for "this sprite is too big/small", "they're facing backwards", "the sprite floats above the floor", or any per-form visual correction in the overlay.
---

# Tuning sprite appearance

Every correction here goes in
`src/DigiChat.Overlay/public/assets/overrides.json`, then
`node src/DigiChat.Overlay/tools/import-assets.mjs` followed by
`npm run build --prefix src/DigiChat.Overlay` from the repository root.
Never hand-edit `manifest.json` — it is regenerated from the files.

The full manual, including the 1–30 lineage numbering tables used for
visual review rounds, is `docs/ASSET-TUNING.md`. Read it before a tuning pass.

## How a size is decided

1. Each form's **visible** height is measured from the alpha bounding box, not
   the canvas — source padding varies wildly and would otherwise shrink a form
   and float it above the floor.
2. The stage's target height applies: fresh 42, inTraining 53, rookie 84,
   champion 115, ultimate 146 px.
3. `stageSizeVariance` decides how strictly that target is enforced — `1` makes
   every form the same height, lower values let the source art's own size
   differences through. Currently 1 / 1 / 0.4 / 0.4 / 0.4 by stage.
4. The scale snaps to a **whole factor** when it is within `snapTolerance`
   (12%), because integer scaling is what keeps pixel art crisp.
5. `scaleMultiplier` on a form is the human override applied last.

## Rules that must hold

- **A Digimon must never shrink when it digivolves.** After any size change,
  check the whole lineage, not just the form you touched.
- **Wide sprites read as large even at the correct area.** When something looks
  too big despite matching heights, cut by width.
- Prefer **one notch** of adjustment at a time. Dropping to the lowest legal
  whole factor has overshot badly before (a rookie fell to 40% of its stage).

## Facing

Art drawn facing left needs `"facesLeft": true` — the overlay mirrors sprites
to their direction of travel and assumes art faces right, so without it they
moonwalk. `_settings.lockFacing: true` is a diagnostic that disables all
mirroring so every form shows the direction it is actually drawn in; use
`node src/DigiChat.Overlay/tools/facing-sheet.mjs` to review a contact sheet,
then turn it back off. The tool derives its ordering and asset keys from the
authoritative `data/lineages.json` roster.

Do not judge facing from moving sprites in the overlay — it is unanswerable.

## What can and cannot be judged automatically

Measured empirically and recorded in `docs/ASSET-TUNING.md`:

- **Pixelation is auto-detectable** — 7/8 correct, zero false alarms. Trust the
  importer's soft-scaling warnings.
- **Apparent size is not** — 3/10. Sizing needs human eyes. Present
  numbered review rounds and ask; do not silently "fix" sizes.

When a review comes in by number, change only what they name. If a change was
not requested, do not bundle it in.
