// TypeScript mirrors of the backend view records (DigiChat.Domain/Views.cs).
// SignalR/minimal-API serialization camelCases the C# property names.

export enum Stage {
  Fresh = 0,
  InTraining = 1,
  Rookie = 2,
  Champion = 3,
  Ultimate = 4,
}

export interface ParticipantView {
  twitchUserId: string;
  displayName: string;
  awaitingLineage: boolean;
  lineageSlug: string | null;
  lineageName: string | null;
  formName: string | null;
  assetKey: string | null;
  joinedUtc: string;
  /** Chatted while everyone was dead: recorded, but not on screen yet. */
  heldForReincarnation: boolean;
}

export interface OverlayStateView {
  stage: Stage;
  stageName: string;
  generationNumber: number;
  sessionNumber: number | null;
  labelsEnabled: boolean;
  participants: ParticipantView[];
  /** Everyone is dead and waiting to be reincarnated. */
  isDead: boolean;
}

export interface SpawnEventView {
  participant: ParticipantView;
  stage: Stage;
}

export interface StageChangeView {
  fromStage: Stage;
  toStage: Stage;
  participants: ParticipantView[];
}

export interface DeathView {
  participants: ParticipantView[];
}

export interface ReincarnationView {
  newGenerationNumber: number;
  participants: ParticipantView[];
}

// ---------------------------------------------------------------- layout config

export interface PlatformConfig {
  id: string;
  left: number;
  right: number;
  top: number;
}

export interface WallConfig {
  x: number;
  top: number;
  bottom: number;
}

export interface LayoutConfig {
  placeholder: boolean;
  canvas: { width: number; height: number };
  platforms: PlatformConfig[];
  walls?: WallConfig[];
  spawnZone: { minX: number; maxX: number };
  edgeMargin: number;
  debug: boolean;
}

/** A planned route to the other platform: walk to takeoffX, jump to targetX. */
export interface TraversalPlan {
  target: PlatformConfig;
  takeoffX: number;
  targetX: number;
  /**
   * A wall standing between takeoff and landing. The arc has to put the feet
   * above `top` before the body's leading edge reaches `left`/`right`.
   */
  clear?: { left: number; right: number; top: number };
}

// ---------------------------------------------------------------- asset manifest

export interface AnimationDef {
  frames: number[];
  frameRate: number;
  repeat?: number;
}

/** One state's artwork, as discovered by tools/import-assets.mjs. */
export interface SpriteStateDef {
  file: string;
  width: number;
  height: number;
  /**
   * Bounding box of the non-transparent pixels, in canvas coordinates. Sizing
   * and the foot anchor come from this, never from the canvas: padding varies
   * wildly between sources and would otherwise shrink a form and float it.
   */
  visible?: { x: number; y: number; width: number; height: number };
  /** File mtime, appended to the URL so replaced art defeats the cache. */
  v?: number;
  /** Present only for multi-frame sheets; absent means the file is one frame. */
  frameWidth?: number;
  frameHeight?: number;
  frames?: number[];
  frameRate?: number;
  repeat?: number;
}

/** A form's art: one entry per state, plus any human overrides merged in. */
export interface SpriteFormDef {
  states: Partial<Record<SpriteState, SpriteStateDef>>;
  /**
   * Set when this form's artwork is drawn facing left. The overlay mirrors a
   * sprite to face its direction of travel, which assumes the art faces right;
   * without this, art drawn the other way moonwalks.
   */
  facesLeft?: boolean;
  scaleMultiplier?: number;
  footAnchorY?: number;
  collisionWidthMultiplier?: number;
  collisionHeightMultiplier?: number;
}

export type SpriteState =
  | "idle"
  | "walk"
  | "airborne"
  | "jump"
  | "fall"
  | "land"
  | "getup";


export interface AssetManifest {
  digimon: Record<string, SpriteFormDef>;
  egg?: SpriteFormDef;
  /** Copied from overrides.json `_settings` by the importer. */
  settings?: {
    snapTolerance?: number;
    stageTargetHeights?: Partial<Record<string, number>>;
    /**
     * How strongly a stage flattens its forms to one height. 1 = every form
     * rendered at the stage's target height, which suits stages whose species
     * really are the same size. Lower values let the source art's own size
     * differences through, down to 0 = one scale for the whole stage.
     */
    stageSizeVariance?: Partial<Record<string, number>>;
    /** Median native visible height per stage; measured by the importer. */
    stageReferenceHeights?: Partial<Record<string, number>>;
    /**
     * Diagnostic: never mirror any sprite, so every form shows the direction
     * its artwork is actually drawn in. Use it to find which forms need
     * `facesLeft`, then turn it back off.
     */
    lockFacing?: boolean;
  };
}
