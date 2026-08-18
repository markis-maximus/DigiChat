// Roster name checker.
//
// data/lineages.json is the authoritative roster, and its 150 form names are
// what viewers actually read on screen. This verifies them against a vendored
// list of known Digimon (data/digimon-names.json) and against the house naming
// convention, which is:
//
//   * the English release name where one exists (Gatomon, not Tailmon;
//     Growlmon, not Growmon), otherwise the standard romanization for species
//     that never left Japan (Zubamon, Ryudamon, Liollmon);
//   * compound names in CamelCase with no spaces (MetalGreymon, LadyDevimon);
//   * the asset key is that name lowercased, so renaming a form can orphan a
//     sprites/ folder — checked here too.
//
// Any finding exits non-zero so verification and CI stop for review. The
// reference list is broad but not authoritative about spelling (see
// data/digimon-names.json _sources), so the checker identifies where a human
// must look; it does not decide the correction.
//
// Run: node tools/check-names.mjs [--refresh]
//      --refresh re-downloads the reference list (needs internet).
import { readdirSync, readFileSync, writeFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(here, "..", "..", "..");
const rosterPath = join(repoRoot, "data", "lineages.json");
const referencePath = join(repoRoot, "data", "digimon-names.json");
const spritesDir = join(here, "..", "public", "assets", "sprites");

const STAGES = ["fresh", "inTraining", "rookie", "champion", "ultimate"];

/**
 * Digimon whose names really are shouted, because they are initialisms. Any
 * other run of capitals is worth a human look: DORUmon sat in this roster for
 * a while purely because that is how the Japanese source romanizes it.
 */
const KNOWN_INITIALISMS = /^(EBE|BEM|JES|XV-|DORU)/;

const SOURCES = {
  japanese: "https://digi-api.com/api/v1/digimon?pageSize=2000",
  english: "https://digimon-api.vercel.app/api/digimon",
};

/** Must match LineageSeeder.Slugify. */
const slugify = (name) =>
  [...name.toLowerCase()]
    .map((c) => (/[a-z0-9]/.test(c) ? c : "-"))
    .join("")
    .replace(/-+/g, "-")
    .replace(/^-|-$/g, "");

/** Loose key: ignores case and punctuation, so "Metal Greymon" == "MetalGreymon". */
const looseKey = (name) => name.toLowerCase().replace(/[^a-z0-9]/g, "");

async function refresh() {
  console.log("Downloading reference lists...");
  const japanese = (await (await fetch(SOURCES.japanese)).json()).content.map((d) => d.name).sort();
  const english = (await (await fetch(SOURCES.english)).json()).map((d) => d.name).sort();
  const existing = existsSync(referencePath) ? JSON.parse(readFileSync(referencePath, "utf8")) : {};
  writeFileSync(
    referencePath,
    JSON.stringify(
      { ...existing, fetchedUtc: new Date().toISOString().slice(0, 10), japanese, english },
      null,
      2,
    ) + "\n",
  );
  console.log(`  ${japanese.length} Japanese-primary + ${english.length} English names -> ${referencePath}\n`);
}

if (process.argv.includes("--refresh")) await refresh();

const reference = JSON.parse(readFileSync(referencePath, "utf8"));
const roster = JSON.parse(readFileSync(rosterPath, "utf8"));
const known = new Map();
for (const n of reference.japanese) known.set(looseKey(n), { name: n, list: "japanese" });
for (const n of reference.english) known.set(looseKey(n), { name: n, list: "english" });
// Hand-verified English names the sources only carry under a Japanese spelling.
for (const [en, ja] of Object.entries(reference.englishOnly ?? {}))
  known.set(looseKey(en), { name: en, list: "englishOnly", counterpart: ja });

const folders = existsSync(spritesDir)
  ? new Set(readdirSync(spritesDir, { withFileTypes: true }).filter((e) => e.isDirectory()).map((e) => e.name))
  : new Set();

const unknown = [];
const shouted = [];
const spaced = [];
const duplicated = [];
const artless = [];
const seen = new Map();
const usedKeys = new Set();
let total = 0;

for (const lineage of roster.lineages) {
  for (const stage of STAGES) {
    const name = lineage.forms[stage];
    if (!name) continue;
    total++;
    const where = `#${lineage.orderIndex} ${lineage.slug} / ${stage}`;

    if (seen.has(name)) duplicated.push(`${name} — ${seen.get(name)} and ${where}`);
    else seen.set(name, where);

    if (!known.has(looseKey(name))) unknown.push(`${name} (${where})`);
    if (/[A-Z]{2,}/.test(name) && !KNOWN_INITIALISMS.test(name)) shouted.push(`${name} (${where})`);
    else if (/[A-Z]{2,}/.test(name)) shouted.push(`${name} (${where}) — known initialism, but check the English form`);
    if (/[\s_]/.test(name)) spaced.push(`${name} (${where})`);

    const key = slugify(name);
    usedKeys.add(key);
    if (folders.size && !folders.has(key)) artless.push(`${name} -> sprites/${key}/ (${where})`);
  }
}

const orphaned = [...folders].filter((f) => !f.startsWith("_") && !usedKeys.has(f));
const handVerified = [...seen.keys()].filter((n) => known.get(looseKey(n))?.list === "englishOnly").length;

const section = (title, items, note) => {
  if (!items.length) return;
  console.log(`\n${title} (${items.length}):`);
  if (note) console.log(`  ${note}`);
  for (const i of items) console.log(`  - ${i}`);
};

console.log(
  `Checked ${total} form names in ${roster.lineages.length} lineages against ` +
    `${known.size} known Digimon (reference fetched ${reference.fetchedUtc}).` +
    (handVerified ? ` ${handVerified} rely on the hand-verified list in data/digimon-names.json.` : ""),
);

section("NOT IN THE REFERENCE", unknown,
  "Either a misspelling, or a name the reference lists differently — check wikimon.net.");
section("SHOUTED CAPITALS", shouted,
  "Japanese romanizations often shout; the English form usually does not.");
section("SPACES OR UNDERSCORES", spaced, "House style is CamelCase with no spaces.");
section("DUPLICATE NAMES", duplicated, "Every form slot must be a distinct species.");
section("NO ART FOLDER", artless, "Renaming a form changes its asset key — the art has to move with it.");
section("ORPHANED ART FOLDERS", orphaned, "No form slugifies to these; likely left behind by a rename.");

const problems = unknown.length + shouted.length + spaced.length + duplicated.length + artless.length + orphaned.length;
console.log(problems ? `\n${problems} thing(s) to look at.\n` : "\nNothing to look at.\n");

// Findings exit non-zero so the verification script and CI fail visibly. They
// still require human judgment: the reference can flag an unknown or oddly
// styled name, but it cannot decide the authoritative English presentation.
if (problems) process.exitCode = 1;
