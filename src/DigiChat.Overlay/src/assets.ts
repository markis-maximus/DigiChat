import Phaser from "phaser";
import {
  Stage,
  type AssetManifest,
  type SpriteFormDef,
  type SpriteState,
} from "./types";

export const ANIM_NAMES = ["idle", "walk", "jump", "fall", "land", "getup"] as const;
export type AnimName = (typeof ANIM_NAMES)[number];

/** Stage-based placeholder sizes (px), also the target heights for real art. */
const STAGE_SIZES: Record<Stage, { w: number; h: number }> = {
  [Stage.Fresh]: { w: 34, h: 30 },
  [Stage.InTraining]: { w: 44, h: 38 },
  [Stage.Rookie]: { w: 56, h: 60 },
  [Stage.Champion]: { w: 78, h: 82 },
  [Stage.Ultimate]: { w: 96, h: 104 },
};

/**
 * Which artwork stands in for a state when a form does not ship one. `airborne`
 * is the shorthand a single rising/falling frame usually covers.
 */
const STATE_FALLBACKS: Record<AnimName, SpriteState[]> = {
  idle: ["idle"],
  walk: ["walk", "idle"],
  jump: ["jump", "airborne", "idle"],
  fall: ["fall", "airborne", "jump", "idle"],
  land: ["land", "idle"],
  getup: ["getup", "idle"],
};

/**
 * Data-driven asset resolution (spec §29–§31). Real art is described by
 * public/assets/manifest.json, which tools/import-assets.mjs generates from
 * assets/sprites/<assetKey>/<state>.png. Anything absent falls back to a
 * generated placeholder, so a missing asset can never break a lineage.
 */
/** Stage order as written in overrides.json's stageTargetHeights. */
const STAGE_KEYS = ["fresh", "inTraining", "rookie", "champion", "ultimate"] as const;

export class AssetLibrary {
  private manifest: AssetManifest = { digimon: {} };
  private placeholderKeys = new Set<string>();

  constructor(private scene: Phaser.Scene) {}

  async loadManifest(): Promise<void> {
    try {
      // Revalidate every load: the manifest has no content hash, and OBS caches
      // hard enough that a re-import would otherwise show none of the new art.
      const res = await fetch("assets/manifest.json", { cache: "no-cache" });
      if (res.ok) this.manifest = (await res.json()) as AssetManifest;
    } catch {
      console.info("[assets] no manifest found; placeholder art only");
    }
  }

  /** Art URL, stamped so a replaced PNG never comes back from a stale cache. */
  private url(file: string, version?: number): string {
    return `assets/${file}${version ? `?v=${version}` : ""}`;
  }

  /** Queues every declared state's file into the loader. Call during preload. */
  preloadDeclared(): void {
    const queue = (key: string, def: SpriteFormDef) => {
      for (const [state, art] of Object.entries(def.states)) {
        if (!art) continue;
        const tex = this.textureKey(key, state as SpriteState);
        const url = this.url(art.file, art.v);
        if (art.frameWidth && art.frameHeight) {
          this.scene.load.spritesheet(tex, url, {
            frameWidth: art.frameWidth,
            frameHeight: art.frameHeight,
          });
        } else {
          this.scene.load.image(tex, url);
        }
      }
    };
    for (const [key, def] of Object.entries(this.manifest.digimon)) queue(key, def);
    if (this.manifest.egg) queue("_egg", this.manifest.egg);
  }

  /** Registers a looping animation for any state that ships multiple frames. */
  registerAnimations(): void {
    const register = (key: string, def: SpriteFormDef) => {
      for (const [state, art] of Object.entries(def.states)) {
        if (!art?.frames || art.frames.length < 2) continue;
        const tex = this.textureKey(key, state as SpriteState);
        if (!this.scene.textures.exists(tex)) continue;
        const animKey = `${tex}:play`;
        if (this.scene.anims.exists(animKey)) continue;
        this.scene.anims.create({
          key: animKey,
          frames: this.scene.anims.generateFrameNumbers(tex, { frames: art.frames }),
          frameRate: art.frameRate ?? 8,
          repeat: art.repeat ?? (state === "idle" || state === "walk" ? -1 : 0),
        });
      }
    };
    for (const [key, def] of Object.entries(this.manifest.digimon)) register(key, def);
    if (this.manifest.egg) register("_egg", this.manifest.egg);
  }

  hasRealArt(assetKey: string | null): boolean {
    return !!assetKey && !!this.stateTexture(assetKey, "idle");
  }

  form(assetKey: string): SpriteFormDef | undefined {
    return this.manifest.digimon[assetKey];
  }

  textureKey(assetKey: string, state: SpriteState): string {
    return `digi-${assetKey}-${state}`;
  }

  /** The loaded texture standing in for `state`, following the fallback table. */
  stateTexture(assetKey: string | null, state: AnimName): string | null {
    if (!assetKey) return null;
    const def = this.manifest.digimon[assetKey];
    if (!def) return null;
    for (const candidate of STATE_FALLBACKS[state]) {
      if (!def.states[candidate]) continue;
      const tex = this.textureKey(assetKey, candidate);
      if (this.scene.textures.exists(tex)) return tex;
    }
    return null;
  }

  /** True when that texture has a registered multi-frame animation. */
  animationFor(textureKey: string): string | null {
    const animKey = `${textureKey}:play`;
    return this.scene.anims.exists(animKey) ? animKey : null;
  }

  /** Target height of the *visible* artwork, tunable in overrides.json. */
  targetHeight(stage: Stage): number {
    const configured = this.manifest.settings?.stageTargetHeights?.[STAGE_KEYS[stage]];
    return typeof configured === "number" && configured > 0 ? configured : STAGE_SIZES[stage].h;
  }

  /**
   * The scale a form wants before snapping. At variance 1 this is simply
   * "make it the stage's target height", which is right where a stage's species
   * really are all the same size. Below 1 the source art's own proportions
   * survive: a Digimon drawn small stays smaller than one drawn large, instead
   * of being stretched up to match — which is also what stops tiny sources
   * needing the huge upscales that make them look blocky.
   */
  idealScale(stage: Stage, visibleHeight: number): number {
    const target = this.targetHeight(stage);
    const key = STAGE_KEYS[stage];
    const variance = this.manifest.settings?.stageSizeVariance?.[key] ?? 1;
    const reference = this.manifest.settings?.stageReferenceHeights?.[key];
    if (variance >= 1 || !reference || visibleHeight <= 0) return target / visibleHeight;
    return target / (visibleHeight ** variance * reference ** (1 - variance));
  }

  /** Diagnostic mode: leave every sprite in its artwork's own orientation. */
  get facingLocked(): boolean {
    return this.manifest.settings?.lockFacing === true;
  }

  /** fitScale using the project's configured tolerance. */
  fit(desired: number): number {
    return AssetLibrary.fitScale(desired, this.manifest.settings?.snapTolerance ?? 0.15);
  }

  /**
   * Fits art to its stage while keeping pixels square. Fractional factors like
   * 2.37x smear a pixel grid, so snap to a whole multiple (2x, 3x) when scaling
   * up and a whole divisor (1/2, 1/3) when scaling down.
   */
  static snapScale(desired: number): number {
    if (!isFinite(desired) || desired <= 0) return 1;
    if (desired >= 1) return Math.max(1, Math.round(desired));
    return 1 / Math.max(1, Math.round(1 / desired));
  }

  /**
   * Snapping alone can land a form far from its stage's size — an ideal 0.92
   * snaps to 1.0 while its neighbour's 0.6 snaps to 0.5, and the two no longer
   * look like peers. Take the snapped factor only when it is close; past that,
   * matching the other Digimon of the stage matters more than pixel purity.
   */
  static fitScale(desired: number, tolerance = 0.15): number {
    if (!isFinite(desired) || desired <= 0) return 1;
    const snapped = AssetLibrary.snapScale(desired);
    return Math.abs(snapped - desired) / desired <= tolerance ? snapped : desired;
  }

  /** Visible-pixel box of whichever state owns `textureKey`, if it was measured. */
  visibleBox(
    assetKey: string | null,
    textureKey: string,
  ): { x: number; y: number; width: number; height: number } | null {
    if (!assetKey) return null;
    const def = assetKey === "_egg" ? this.manifest.egg : this.manifest.digimon[assetKey];
    for (const [state, art] of Object.entries(def?.states ?? {})) {
      if (art && this.textureKey(assetKey, state as SpriteState) === textureKey)
        return art.visible ?? null;
    }
    return null;
  }

  /**
   * Placeholder texture: a rounded capsule in a hue derived from the asset key,
   * sized by stage, with simple eyes. Generated once per (key, stage).
   */
  placeholderTexture(assetKey: string | null, stage: Stage): string {
    const key = `ph-${assetKey ?? "unknown"}-${stage}`;
    if (this.placeholderKeys.has(key)) return key;

    const { w, h } = STAGE_SIZES[stage];
    const hue = this.hashHue(assetKey ?? "unknown");
    const color = Phaser.Display.Color.HSLToColor(hue, 0.55, 0.55).color;
    const dark = Phaser.Display.Color.HSLToColor(hue, 0.55, 0.35).color;

    const g = this.scene.add.graphics();
    g.fillStyle(color, 1);
    g.fillRoundedRect(0, 0, w, h, Math.min(w, h) * 0.35);
    g.lineStyle(2, dark, 1);
    g.strokeRoundedRect(1, 1, w - 2, h - 2, Math.min(w, h) * 0.35);
    // eyes
    g.fillStyle(0x111111, 1);
    const eyeY = h * 0.32;
    g.fillCircle(w * 0.32, eyeY, Math.max(2, w * 0.06));
    g.fillCircle(w * 0.68, eyeY, Math.max(2, w * 0.06));
    g.generateTexture(key, w, h);
    g.destroy();

    this.placeholderKeys.add(key);
    return key;
  }

  /** Generic egg (one shared design, spec §32): real art if supplied, else drawn. */
  eggTexture(): string {
    const real = this.stateTexture("_egg", "idle");
    if (real) return real;

    const key = "ph-egg";
    if (this.placeholderKeys.has(key)) return key;

    const w = 44;
    const h = 54;
    const g = this.scene.add.graphics();
    g.fillStyle(0xf2ede4, 1);
    g.fillEllipse(w / 2, h / 2 + 3, w - 6, h - 8);
    g.lineStyle(2, 0xb9b2a6, 1);
    g.strokeEllipse(w / 2, h / 2 + 3, w - 6, h - 8);
    g.fillStyle(0xd9506a, 1); // simple diamond pattern band
    for (let i = 0; i < 3; i++) {
      const cx = w * (0.28 + 0.22 * i);
      g.fillTriangle(cx, h * 0.42, cx - 5, h * 0.52, cx + 5, h * 0.52);
      g.fillTriangle(cx, h * 0.62, cx - 5, h * 0.52, cx + 5, h * 0.52);
    }
    g.generateTexture(key, w, h);
    g.destroy();
    this.placeholderKeys.add(key);
    return key;
  }

  private hashHue(s: string): number {
    let hash = 0;
    for (let i = 0; i < s.length; i++) hash = (hash * 31 + s.charCodeAt(i)) | 0;
    return (Math.abs(hash) % 360) / 360;
  }
}
