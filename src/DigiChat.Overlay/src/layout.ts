import { API_BASE } from "./net";
import type { LayoutConfig } from "./types";

/** Fallback used only if the backend is unreachable at boot. */
const FALLBACK: LayoutConfig = {
  placeholder: true,
  canvas: { width: 1920, height: 1080 },
  platforms: [
    { id: "upper", left: 1180, right: 1880, top: 420 },
    { id: "lower", left: 1180, right: 1880, top: 880 },
  ],
  spawnZone: { minX: 1220, maxX: 1840 },
  edgeMargin: 24,
  debug: false,
};

export async function loadLayout(): Promise<LayoutConfig> {
  try {
    const res = await fetch(`${API_BASE}/api/config/layout`);
    if (!res.ok) throw new Error(`layout fetch ${res.status}`);
    const cfg = validateLayout(await res.json());
    if (cfg.placeholder) {
      console.warn(
        "[layout] Using PLACEHOLDER platform geometry — edit data/layout.json with real OBS values.",
      );
    }
    return cfg;
  } catch (err) {
    console.error("[layout] falling back to built-in placeholder layout", err);
    return FALLBACK;
  }
}

function validateLayout(value: unknown): LayoutConfig {
  if (!isRecord(value) || !isRecord(value.canvas) || !isRecord(value.spawnZone))
    throw new Error("layout must contain canvas and spawnZone objects");

  // Per-axis limits are not enough: 16384 x 16384 is individually "valid" and
  // asks the browser for a ~1 GB canvas, which in OBS means a black or crashed
  // Browser Source mid-stream. Cap the area as well, generously above any real
  // broadcast canvas (8K is 33 MP).
  const width = finiteNumber(value.canvas.width, "canvas.width", 1, 16384);
  const height = finiteNumber(value.canvas.height, "canvas.height", 1, 16384);
  const MAX_CANVAS_PIXELS = 40_000_000;
  if (width * height > MAX_CANVAS_PIXELS)
    throw new Error(
      `canvas ${width}x${height} exceeds ${MAX_CANVAS_PIXELS} pixels; ` +
        "use your real OBS canvas size (e.g. 1920x1080)",
    );
  if (!Array.isArray(value.platforms) || value.platforms.length < 1 || value.platforms.length > 64)
    throw new Error("layout.platforms must contain 1-64 platforms");

  const platforms = value.platforms.map((entry, index) => {
    if (!isRecord(entry) || typeof entry.id !== "string" || !entry.id.trim())
      throw new Error(`platforms[${index}].id must be a non-empty string`);
    const left = finiteNumber(entry.left, `platforms[${index}].left`, 0, width);
    const right = finiteNumber(entry.right, `platforms[${index}].right`, 0, width);
    const top = finiteNumber(entry.top, `platforms[${index}].top`, 0, height);
    if (right <= left) throw new Error(`platforms[${index}] must have right > left`);
    return { id: entry.id, left, right, top };
  });

  const minX = finiteNumber(value.spawnZone.minX, "spawnZone.minX", 0, width);
  const maxX = finiteNumber(value.spawnZone.maxX, "spawnZone.maxX", 0, width);
  if (maxX <= minX) throw new Error("spawnZone.maxX must be greater than minX");

  const walls = value.walls === undefined
    ? undefined
    : (() => {
        if (!Array.isArray(value.walls) || value.walls.length > 64)
          throw new Error("layout.walls must contain at most 64 walls");
        return value.walls.map((entry, index) => {
          if (!isRecord(entry)) throw new Error(`walls[${index}] must be an object`);
          const x = finiteNumber(entry.x, `walls[${index}].x`, 0, width);
          const top = finiteNumber(entry.top, `walls[${index}].top`, 0, height);
          const bottom = finiteNumber(entry.bottom, `walls[${index}].bottom`, 0, height);
          if (bottom <= top) throw new Error(`walls[${index}] must have bottom > top`);
          return { x, top, bottom };
        });
      })();

  return {
    placeholder: value.placeholder === true,
    canvas: { width, height },
    platforms,
    walls,
    spawnZone: { minX, maxX },
    edgeMargin: finiteNumber(value.edgeMargin, "edgeMargin", 0, width / 2),
    debug: value.debug === true,
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function finiteNumber(value: unknown, name: string, minimum: number, maximum: number): number {
  if (typeof value !== "number" || !Number.isFinite(value) || value < minimum || value > maximum)
    throw new Error(`${name} must be a finite number between ${minimum} and ${maximum}`);
  return value;
}
