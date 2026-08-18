// Builds a self-contained page showing every form's idle art at rest, so the
// direction each sprite is DRAWN can be judged in one pass instead of chased
// around the overlay. Tick the ones facing left and copy the result straight
// into overrides.json.
//
// From the repository root:
//   node src/DigiChat.Overlay/tools/facing-sheet.mjs [outputPath]
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(here, "..", "..", "..");
const assets = join(here, "..", "public", "assets");
const out = process.argv[2] ?? join(assets, "facing-check.html");

const manifest = JSON.parse(readFileSync(join(assets, "manifest.json"), "utf8"));
const roster = JSON.parse(readFileSync(join(repoRoot, "data", "lineages.json"), "utf8"));
const overrides = JSON.parse(readFileSync(join(assets, "overrides.json"), "utf8"));

/** Must match LineageSeeder.Slugify and the other asset tools. */
const slugify = (name) =>
  [...name.toLowerCase()]
    .map((c) => (/[a-z0-9]/.test(c) ? c : "-"))
    .join("")
    .replace(/-+/g, "-")
    .replace(/^-|-$/g, "");

const STAGES = [
  ["fresh", "Fresh"],
  ["inTraining", "In-Training"],
  ["rookie", "Rookie"],
  ["champion", "Champion"],
  ["ultimate", "Ultimate"],
];
const lineages = [...roster.lineages].sort((a, b) => a.orderIndex - b.orderIndex);
const orderIndexes = new Set();
for (const lineage of lineages) {
  if (!Number.isInteger(lineage.orderIndex) || orderIndexes.has(lineage.orderIndex))
    throw new Error(`Invalid or duplicate orderIndex ${lineage.orderIndex} in data/lineages.json`);
  orderIndexes.add(lineage.orderIndex);
}

const dataUri = (file) => {
  const path = join(assets, file);
  if (!existsSync(path)) return null;
  return `data:image/png;base64,${readFileSync(path).toString("base64")}`;
};

let cards = "";
for (const [stage, label] of STAGES) {
  cards += `<h2>${label}</h2><div class="grid">`;
  lineages.forEach((lineage) => {
    const formName = lineage.forms?.[stage];
    if (!formName)
      throw new Error(`Lineage ${lineage.slug} is missing its ${stage} form in data/lineages.json`);
    const key = slugify(formName);
    const art = manifest.digimon[key]?.states?.idle;
    if (!art) return;
    const src = dataUri(art.file);
    const checked = overrides[key]?.facesLeft ? " checked" : "";
    cards +=
      `<label class="card"><input type="checkbox" data-key="${key}"${checked}>` +
      `<span class="art">${src ? `<img src="${src}" alt="${key}">` : "?"}</span>` +
      `<span class="name">${lineage.orderIndex}. ${key}</span></label>`;
  });
  cards += `</div>`;
}

writeFileSync(
  out,
  `<!doctype html><meta charset="utf-8"><title>Which sprites face left?</title>
<style>
 body{background:#15171c;color:#e8eaf0;font:14px system-ui,sans-serif;margin:0;padding:24px 28px 120px}
 h1{font-size:20px;margin:0 0 4px} p{color:#9aa1b0;margin:0 0 20px;max-width:70ch;line-height:1.5}
 h2{font-size:15px;color:#7aa2ff;margin:28px 0 10px;border-bottom:1px solid #2a2e38;padding-bottom:6px}
 .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(120px,1fr));gap:10px}
 .card{background:#1c1f27;border:2px solid #262a34;border-radius:8px;padding:10px 6px 8px;
   display:flex;flex-direction:column;align-items:center;gap:6px;cursor:pointer}
 .card:has(input:checked){border-color:#ffb454;background:#2a2318}
 .card input{position:absolute;opacity:0}
 .art{height:96px;display:flex;align-items:flex-end;justify-content:center}
 .art img{image-rendering:pixelated;max-height:96px;max-width:104px}
 .name{font-size:11px;color:#9aa1b0;text-align:center;word-break:break-word}
 .card:has(input:checked) .name{color:#ffb454}
 footer{position:fixed;left:0;right:0;bottom:0;background:#0f1116;border-top:1px solid #2a2e38;
   padding:12px 28px;display:flex;gap:14px;align-items:center}
 button{background:#7aa2ff;color:#0f1116;border:0;border-radius:6px;padding:9px 16px;font-weight:600;cursor:pointer}
 #outbox{flex:1;background:#15171c;color:#e8eaf0;border:1px solid #2a2e38;border-radius:6px;
   padding:8px;font:12px ui-monospace,monospace;height:56px}
</style>
<h1>Which sprites face left?</h1>
<p>Every form's idle art, exactly as drawn — no mirroring, nothing moving. Click the ones
whose artwork faces <b>left</b>. Skip anything drawn front-on; those look right either way.
Then hit copy and paste the result back to me.</p>
${cards}
<footer><button onclick="copyOut()">Copy the list</button><textarea id="outbox" readonly></textarea></footer>
<script>
function copyOut(){
  const keys=[...document.querySelectorAll("input:checked")].map(i=>i.dataset.key);
  const text=keys.length?keys.join(" "):"(none selected)";
  document.getElementById("outbox").value=text;
  navigator.clipboard?.writeText(text);
}
</script>`,
);
console.log(`wrote ${out}`);
