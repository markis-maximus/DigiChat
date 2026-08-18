// Convention-first asset importer.
//
//   public/assets/sprites/<assetKey>/idle.png
//                                   walk.png
//                                   airborne.png   (covers jump + fall)
//                                   land.png       (optional)
//                                   getup.png      (optional)
//
// Everything above is a *fact* about files on disk — including the bounding box
// of each PNG's visible pixels — so this script discovers it and writes
// public/assets/manifest.json. Anything needing human eyes goes in
// public/assets/overrides.json, which is hand-written and merged on top.
//
// Run via import-assets.bat at the repo root, or: node tools/import-assets.mjs
import { readdirSync, readFileSync, writeFileSync, existsSync, statSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { readPng } from "./png-bounds.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const assetsDir = join(here, "..", "public", "assets");
const spritesDir = join(assetsDir, "sprites");
const manifestPath = join(assetsDir, "manifest.json");
const overridesPath = join(assetsDir, "overrides.json");
const lineagePath = join(here, "..", "..", "..", "data", "lineages.json");

/** States the overlay renders. `airborne` is shorthand for jump + fall. */
const STATES = ["idle", "walk", "airborne", "jump", "fall", "land", "getup"];
/** Folder name reserved for the shared reincarnation egg. */
const EGG_KEY = "_egg";
/** Fallbacks when overrides.json has no _settings block. */
const DEFAULT_SETTINGS = {
  snapTolerance: 0.15,
  stageTargetHeights: { fresh: 30, inTraining: 38, rookie: 60, champion: 82, ultimate: 104 },
};
/** Collision box as a fraction of the visible artwork (mirrors actors.ts). */
const COLLISION_W = 0.8;
const COLLISION_H = 0.92;
/** Detail blocks are printed per form up to this many; beyond it, only flagged ones. */
const DETAIL_LIMIT = 20;

const snapScale = (d) => (d >= 1 ? Math.max(1, Math.round(d)) : 1 / Math.max(1, Math.round(1 / d)));
/** Mirrors AssetLibrary.fitScale so the report predicts what the overlay does. */
const fitScale = (d, tolerance) => {
  const snapped = snapScale(d);
  return Math.abs(snapped - d) / d <= tolerance ? snapped : d;
};
/** Must match LineageSeeder.Slugify. */
const slugify = (name) =>
  [...name.toLowerCase()]
    .map((c) => (/[a-z0-9]/.test(c) ? c : "-"))
    .join("")
    .replace(/-+/g, "-")
    .replace(/^-|-$/g, "");

function readJson(path, fallback) {
  if (!existsSync(path)) return fallback;
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch (err) {
    console.error(`! ${path} is not valid JSON — ${err.message}`);
    process.exit(1);
  }
}

/** assetKey -> stage name, so the report can predict each form's scale. */
function stageIndex() {
  const doc = readJson(lineagePath, null);
  const map = {};
  for (const lineage of doc?.lineages ?? [])
    for (const [stage, formName] of Object.entries(lineage.forms ?? {}))
      map[slugify(formName)] = stage;
  return map;
}

if (!existsSync(spritesDir)) {
  console.error(`No sprite folder yet. Create one and drop art in:\n  ${spritesDir}\\<assetKey>\\idle.png`);
  process.exit(1);
}

const overrides = readJson(overridesPath, {});
const settings = {
  snapTolerance: overrides._settings?.snapTolerance ?? DEFAULT_SETTINGS.snapTolerance,
  stageTargetHeights: {
    ...DEFAULT_SETTINGS.stageTargetHeights,
    ...(overrides._settings?.stageTargetHeights ?? {}),
  },
  stageSizeVariance: { ...(overrides._settings?.stageSizeVariance ?? {}) },
  ...(overrides._settings?.lockFacing ? { lockFacing: true } : {}),
};
const stages = stageIndex();
const warnings = [];
const rows = [];
const details = [];
const digimon = {};
const scanned = [];
let egg;

for (const key of readdirSync(spritesDir).sort()) {
  const dir = join(spritesDir, key);
  if (!statSync(dir).isDirectory()) continue;

  const states = {};
  for (const file of readdirSync(dir)) {
    if (!file.toLowerCase().endsWith(".png")) {
      warnings.push(`${key}/${file}: not a .png, ignored`);
      continue;
    }
    const state = file.slice(0, -4).toLowerCase();
    if (!STATES.includes(state)) {
      warnings.push(`${key}/${file}: unknown state, expected one of ${STATES.join(", ")}`);
      continue;
    }
    try {
      const path = join(dir, file);
      const png = readPng(path);
      if (png.empty) {
        warnings.push(`${key}/${file}: every pixel is transparent, ignored`);
        continue;
      }
      if (!png.measured)
        warnings.push(
          `${key}/${file}: could not measure visible bounds (interlaced, or no alpha channel) — sized from the full canvas instead`,
        );
      states[state] = {
        file: `sprites/${key}/${file}`,
        width: png.canvas.width,
        height: png.canvas.height,
        visible: png.visible,
        // mtime doubles as a cache-buster: replacing art changes the URL.
        v: Math.round(statSync(path).mtimeMs),
      };
    } catch (err) {
      warnings.push(`${key}/${file}: ${err.message}`);
    }
  }

  if (!states.idle) {
    warnings.push(
      Object.keys(states).length
        ? `${key}/: no idle.png — every other state falls back to idle, so this form stays a placeholder`
        : `${key}/: no usable art, skipped`,
    );
    continue;
  }

  // Human overrides are merged last and always win. `states` merges per state
  // rather than replacing the lot, so a hand-written frame list survives the
  // next import instead of wiping the discovered files.
  const formOverride = overrides[key] ?? {};
  const mergedStates = { ...states };
  for (const [state, extra] of Object.entries(formOverride.states ?? {}))
    if (mergedStates[state]) mergedStates[state] = { ...mergedStates[state], ...extra };
  const entry = { ...formOverride, states: mergedStates };
  if (key === EGG_KEY) egg = entry;
  else digimon[key] = entry;

  scanned.push({ key, states });
}

// A stage's reference height is the median of its forms' native artwork. It is
// the anchor for stageSizeVariance below 1: forms drawn larger than the median
// stay larger, rather than everything being stretched to one height.
const nativeByStage = {};
for (const { key, states } of scanned) {
  const stage = stages[key];
  if (!stage) continue;
  (nativeByStage[stage] ??= []).push(states.idle.visible.height);
}
settings.stageReferenceHeights = {};
for (const [stage, heights] of Object.entries(nativeByStage)) {
  const sorted = heights.sort((a, b) => a - b);
  settings.stageReferenceHeights[stage] = sorted[Math.floor(sorted.length / 2)];
}

/** Mirrors AssetLibrary.idealScale. */
const idealScale = (stage, height) => {
  const target = settings.stageTargetHeights[stage];
  const variance = settings.stageSizeVariance?.[stage] ?? 1;
  const reference = settings.stageReferenceHeights[stage];
  if (variance >= 1 || !reference) return target / height;
  return target / (height ** variance * reference ** (1 - variance));
};

for (const { key, states } of scanned) {
  const idle = states.idle;
  const vis = idle.visible;
  const stage = stages[key];
  const target = settings.stageTargetHeights[stage];
  const ov = overrides[key] ?? {};
  const formWarnings = [];
  let fit = "";
  let detail = null;

  if (key === EGG_KEY) fit = "(egg)";
  else if (!stage) {
    fit = "?";
    formWarnings.push("no form with this name in data/lineages.json — check the folder name");
  } else {
    const ideal = idealScale(stage, vis.height);
    const snapped = snapScale(ideal);
    const scale = fitScale(ideal, settings.snapTolerance) * (ov.scaleMultiplier ?? 1);
    const usedSnap = Math.abs(fitScale(ideal, settings.snapTolerance) - snapped) < 1e-9;
    const off = (Math.abs(snapped - ideal) / ideal) * 100;
    fit = `${stage} -> x${scale.toFixed(3)} = ${Math.round(vis.height * scale)}px ${usedSnap ? "snapped" : "EXACT"}`;

    if (!usedSnap)
      formWarnings.push(
        `visible height ${vis.height}px is ${off.toFixed(1)}% from a whole-factor fit to the ${stage} target ` +
          `(${target}px), past the ${(settings.snapTolerance * 100).toFixed(0)}% tolerance — using the exact scale, ` +
          `so pixels will be slightly soft. Re-export nearer ${target}px (or a whole multiple) to avoid it.`,
      );
    const padBelow = idle.height - (vis.y + vis.height);
    if (idle.height - vis.height > idle.height * 0.35)
      formWarnings.push(`${idle.height - vis.height}px of the ${idle.height}px canvas is empty — harmless, but trimming keeps files small`);

    const feetLine = ov.footAnchorY != null ? idle.height * ov.footAnchorY : vis.y + vis.height;
    detail = [
      `  ${key}  (${stage})`,
      `    source canvas    ${idle.width} x ${idle.height} px   ${idle.file}`,
      `    visible bounds   x=${vis.x} y=${vis.y}  ${vis.width} x ${vis.height} px` +
        `   padding L${vis.x} R${idle.width - (vis.x + vis.width)} T${vis.y} B${padBelow}`,
      `    stage target     ${target} px visible height` +
        `${(settings.stageSizeVariance?.[stage] ?? 1) < 1 ? `, variance ${settings.stageSizeVariance[stage]} around a ${settings.stageReferenceHeights[stage]}px reference` : ""}`,
      `    scale            x${scale.toFixed(3)}  ${usedSnap ? `SNAPPED (whole factor, ${off.toFixed(1)}% off ideal)` : `EXACT (snap x${snapped.toFixed(3)} was ${off.toFixed(1)}% off, tolerance ${(settings.snapTolerance * 100).toFixed(0)}%)`}` +
        `${ov.scaleMultiplier != null ? `  x${ov.scaleMultiplier} override applied` : ""}`,
      `    renders at       ${Math.round(vis.width * scale)} x ${Math.round(vis.height * scale)} px visible`,
      `    foot baseline    y=${feetLine} of ${idle.height}  (originY ${(feetLine / idle.height).toFixed(3)})` +
        `${ov.footAnchorY != null ? "  from override" : "  from visible bottom"}`,
      `    collision        ${Math.round(vis.width * COLLISION_W * (ov.collisionWidthMultiplier ?? 1) * scale)} x ` +
        `${Math.round(vis.height * COLLISION_H * (ov.collisionHeightMultiplier ?? 1) * scale)} px on screen`,
      `    warnings         ${formWarnings.length ? formWarnings.length : "none"}`,
    ].join("\n");
  }

  for (const w of formWarnings) warnings.push(`${key}/idle.png: ${w}`);
  if (detail) details.push({ key, text: detail, flagged: formWarnings.length > 0 });

  rows.push({
    key,
    states: STATES.filter((s) => states[s]).join(", "),
    canvas: `${idle.width}x${idle.height}`,
    visible: `${vis.width}x${vis.height}`,
    fit,
    overridden: Object.keys(ov).join(", "),
  });
}

const manifest = {
  _comment:
    "GENERATED by tools/import-assets.mjs — do not hand-edit. Put art in assets/sprites/<assetKey>/<state>.png and human tuning in overrides.json, then re-run the importer.",
  generatedUtc: new Date().toISOString(),
  settings,
  digimon,
  ...(egg ? { egg } : {}),
};
writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n");

const pad = (s, n) => String(s).padEnd(n);
console.log(
  `\n${pad("asset key", 16)}${pad("states", 26)}${pad("canvas", 11)}${pad("visible", 11)}${pad("fitted", 34)}overrides`,
);
console.log("-".repeat(110));
for (const r of rows)
  console.log(`${pad(r.key, 16)}${pad(r.states, 26)}${pad(r.canvas, 11)}${pad(r.visible, 11)}${pad(r.fit, 34)}${r.overridden}`);

for (const k of Object.keys(overrides).filter((k) => !k.startsWith("_") && !rows.some((r) => r.key === k)))
  warnings.push(`overrides.json has "${k}", but no sprites/${k}/ folder exists`);

const shown = details.length <= DETAIL_LIMIT ? details : details.filter((d) => d.flagged);
if (shown.length) {
  console.log(
    `\ndetail${details.length > DETAIL_LIMIT ? ` (only forms with warnings; ${details.length} forms total)` : ""}:`,
  );
  for (const d of shown) console.log(d.text);
}

if (warnings.length) {
  console.log("\nwarnings:");
  for (const w of warnings) console.log(`  - ${w}`);
}
console.log(
  `\nsettings (from overrides.json _settings): snap tolerance ${(settings.snapTolerance * 100).toFixed(0)}%, ` +
    `stage targets ${Object.entries(settings.stageTargetHeights).map(([k, v]) => `${k} ${v}px`).join(", ")}`,
);
console.log(
  `\n${rows.length} form(s) written to ${manifestPath}` +
    `\nRebuild the overlay (npm run build) and refresh the OBS Browser Source.\n`,
);
