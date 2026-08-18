import Phaser from "phaser";
import { AssetLibrary } from "./assets";
import { DigimonActor, GRAVITY } from "./actors";
import { Stage } from "./types";
import type {
  DeathView,
  LayoutConfig,
  OverlayStateView,
  ParticipantView,
  PlatformConfig,
  ReincarnationView,
  SpawnEventView,
  StageChangeView,
  TraversalPlan,
} from "./types";

/** Collision thickness of a step-face wall, in canvas pixels. */
const WALL_THICKNESS = 12;
/** Run-up before jumping *up* a step, and run-out after jumping *down* one. */
const RUN_UP = 260;
const RUN_OUT = 240;
/** Gap between the body's edge and the wall face at the near end of a jump. */
const LAND_CLEAR = 60;
const TAKEOFF_CLEAR = 90;

/**
 * The single overlay scene. It never owns authoritative state: it renders
 * whatever the backend last said, and every event sequence (spawn, stage
 * change, reincarnation) runs through a serial promise queue so animations
 * can't interleave (spec §14 renderer side).
 */
export class OverlayScene extends Phaser.Scene {
  readonly assets = new AssetLibrary(this);
  private actors = new Map<string, DigimonActor>();
  private actorColliders = new Map<string, Phaser.Physics.Arcade.Collider>();
  private activeEntrances = new Map<
    string,
    { actor: DigimonActor; platform: PlatformConfig; settled: Promise<void> }
  >();
  private queue: Promise<void> = Promise.resolve();
  private labelsEnabled = true;
  private platformBodies: Phaser.GameObjects.Rectangle[] = [];

  constructor(private layout: LayoutConfig) {
    super("overlay");
  }

  // ---------------------------------------------------------------- lifecycle

  async preloadManifest(): Promise<void> {
    await this.assets.loadManifest();
  }

  preload(): void {
    this.assets.preloadDeclared();
  }

  create(): void {
    this.physics.world.gravity.y = GRAVITY;
    this.assets.registerAnimations();

    // 3x3 white pixel for particle effects.
    const g = this.add.graphics();
    g.fillStyle(0xffffff, 1);
    g.fillRect(0, 0, 3, 3);
    g.generateTexture("particle-px", 3, 3);
    g.destroy();

    // Invisible one-way platform bodies from OBS-space geometry.
    for (const p of this.layout.platforms) {
      const width = p.right - p.left;
      const rect = this.add.rectangle(p.left + width / 2, p.top + 8, width, 16, 0, 0);
      this.physics.add.existing(rect, true);
      const body = rect.body as Phaser.Physics.Arcade.StaticBody;
      body.checkCollision.down = false;
      body.checkCollision.left = false;
      body.checkCollision.right = false;
      rect.setData("platform", p);
      this.platformBodies.push(rect);
    }

    // Solid walls (e.g. the step face between two adjacent platforms).
    // Side collision only: falling past a wall must never snag on its top.
    for (const w of this.layout.walls ?? []) {
      const height = w.bottom - w.top;
      const rect = this.add.rectangle(w.x, w.top + height / 2, WALL_THICKNESS, height, 0, 0);
      this.physics.add.existing(rect, true);
      const body = rect.body as Phaser.Physics.Arcade.StaticBody;
      body.checkCollision.up = false;
      body.checkCollision.down = false;
      this.platformBodies.push(rect); // same collider; no "platform" data → no landing
    }

    if (this.layout.debug) this.drawDebug();
    this.game.events.emit("scene-ready");
  }

  override update(_time: number, delta: number): void {
    for (const actor of this.actors.values()) {
      actor.update(delta);
      // Safety net: fell past the canvas (bad spawn x) → put back on a platform.
      if (actor.sprite.y > this.layout.canvas.height + 120) {
        const p = this.randomPlatform();
        actor.placeStanding(p, Phaser.Math.Between(p.left + 40, p.right - 40));
      }
    }
  }

  // ---------------------------------------------------------------- event API
  // Called from net.ts; each defers into the serial queue.

  applyResync(state: OverlayStateView): Promise<void> {
    return this.enqueueTracked(async () => {
      this.labelsEnabled = state.labelsEnabled;
      this.activeEntrances.clear();
      for (const id of [...this.actors.keys()]) this.destroyActor(id);
      // Clean final state, no entrance replays (spec §19). A death is stored
      // state rather than an animation, so a reload during one has to rebuild
      // the silhouettes exactly; chatters held during it stay off screen.
      for (const p of state.participants) {
        if (p.awaitingLineage || p.heldForReincarnation) continue;
        const platform = this.randomPlatform();
        const actor = this.createActor(p, state.stage);
        actor.placeStanding(
          platform,
          Phaser.Math.Between(platform.left + 40, platform.right - 40),
        );
        if (state.isDead) actor.applyDeadLook();
      }
    });
  }

  onSpawn(spawn: SpawnEventView): Promise<void> {
    return this.enqueueTracked(async () => {
      if (this.actors.has(spawn.participant.twitchUserId)) return; // idempotent
      if (spawn.participant.awaitingLineage) return; // no art until assigned
      const platform = this.randomPlatform();
      const actor = this.createActor(spawn.participant, spawn.stage);
      const settled = actor.enterFalling(this.pickSpawnX(platform));
      const id = spawn.participant.twitchUserId;
      const entry = { actor, platform, settled };
      this.activeEntrances.set(id, entry);
      void settled.finally(() => {
        if (this.activeEntrances.get(id) === entry)
          this.activeEntrances.delete(id);
      });
      // Entrances may overlap. Holding the global renderer queue for 1.2s per
      // chatter let a raid delay a stage/death event by tens of seconds.
    });
  }

  onStageChanged(change: StageChangeView): Promise<void> {
    return this.enqueueTracked(async () => {
      await this.settleActiveEntrances();
      // Synchronized digivolution for everyone (spec §27).
      const jobs: Promise<void>[] = [];
      for (const p of change.participants) {
        if (p.awaitingLineage) continue;
        const actor = this.actors.get(p.twitchUserId);
        if (actor) {
          jobs.push(actor.digivolve(p, change.toStage));
        } else {
          const created = this.createActor(p, change.toStage);
          const platform = this.randomPlatform();
          created.placeStanding(
            platform,
            Phaser.Math.Between(platform.left + 40, platform.right - 40),
          );
        }
      }
      await Promise.all(jobs);
    });
  }

  /** Everyone dies and stays dead — the egg comes later, on its own command. */
  onDeath(view: DeathView): Promise<void> {
    return this.enqueueTracked(async () => {
      await this.settleActiveEntrances();
      const dying = view.participants
        .map((p) => this.actors.get(p.twitchUserId))
        .filter((a): a is DigimonActor => a !== undefined);

      // freeze() disables gravity wherever the sprite is, so a Digimon caught
      // mid-jump would die in open air — and then wobble as an egg and hatch in
      // the sky, since the corpse position carries through reincarnation.
      // Let them finish their arc and land first: the same physics viewers
      // already see when a mid-air Digimon digivolves, rather than a snap.
      for (const a of dying) a.holdStill = true;
      await this.settleAirborne(dying);

      await Promise.all(dying.map((a) => a.dissolve()));
    });
  }

  onReincarnation(view: ReincarnationView): Promise<void> {
    return this.enqueueTracked(async () => {
      await this.settleActiveEntrances();
      // The dying already happened on its own command, so this starts from the
      // corpses: each becomes an egg where it fell.
      for (const a of [...this.actors.values()]) a.showEgg();

      // Chatters who arrived during the death have no sprite yet. Fade an egg
      // in for them too, so the whole screen hatches as one event rather than
      // some hatching while others drop out of the sky.
      const arriving: Promise<void>[] = [];
      for (const p of view.participants) {
        if (p.awaitingLineage || this.actors.has(p.twitchUserId)) continue;
        const platform = this.randomPlatform();
        const actor = this.createActor(p, Stage.Fresh);
        actor.placeStanding(
          platform,
          Phaser.Math.Between(platform.left + 40, platform.right - 40),
        );
        arriving.push(actor.showEggFadingIn());
      }
      await Promise.all(arriving);
      await this.wait(1800);
      // 3. Hatch into the new generation's Fresh forms.
      const byId = new Map(view.participants.map((p) => [p.twitchUserId, p]));
      const hatches: Promise<void>[] = [];
      for (const [id, actor] of this.actors) {
        const next = byId.get(id);
        if (next && !next.awaitingLineage) {
          hatches.push(actor.hatch(next, this.labelsEnabled));
        }
        // Awaiting-lineage viewers keep their egg (admin shows a warning).
      }
      await Promise.all(hatches);
    });
  }

  /** Debug/diagnostics: current number of rendered Digimon. */
  get actorCount(): number {
    return this.actors.size;
  }

  // ---------------------------------------------------------------- internals

  /**
   * Like the plain queue, but the caller learns whether the sequence actually
   * ran. Reconciliation needs this: if a resync throws and the caller is told
   * nothing, it records the state as rendered and every later comparison looks
   * equal, so a wrong canvas is never repaired.
   */
  private enqueueTracked(seq: () => Promise<void>): Promise<void> {
    const run = this.queue.then(seq);
    // The shared tail must stay healthy whatever this sequence does; the
    // caller handles (and logs) the real outcome.
    this.queue = run.catch(() => {});
    return run;
  }

  /**
   * Global transitions must not freeze a just-spawned actor in mid-air. All
   * entrances continue in parallel; wait only for the slowest one, with a
   * hard bound and a grounded fallback for malformed geometry.
   */
  private async settleActiveEntrances(): Promise<void> {
    const entries = [...this.activeEntrances.entries()];
    if (entries.length === 0) return;

    await Promise.race([
      Promise.allSettled(entries.map(([, entry]) => entry.settled)),
      this.wait(1500),
    ]);

    for (const [id, entry] of entries) {
      if (this.activeEntrances.get(id) !== entry) continue;
      const inset = Math.max(this.layout.edgeMargin, 40);
      const x = Phaser.Math.Clamp(
        entry.actor.sprite.x,
        entry.platform.left + inset,
        entry.platform.right - inset,
      );
      entry.actor.placeStanding(entry.platform, x);
      this.activeEntrances.delete(id);
    }
  }

  /**
   * Routes between platforms, derived from OBS geometry (spec §21: the
   * renderer owns trajectory math). Horizontally overlapping platforms jump
   * straight up/down inside the overlap; adjacent "step" platforms walk to
   * the shared boundary and jump across it.
   */
  private planTraversal(
    from: PlatformConfig,
    halfWidth: number,
  ): TraversalPlan | null {
    const others = this.layout.platforms.filter((p) => p !== from);
    if (others.length === 0) return null;
    const to = others[Math.floor(Math.random() * others.length)];
    const m = this.layout.edgeMargin;

    const overlapLeft = Math.max(from.left, to.left) + m;
    const overlapRight = Math.min(from.right, to.right) - m;
    if (overlapRight > overlapLeft) {
      const x = Phaser.Math.Between(overlapLeft, overlapRight);
      return { target: to, takeoffX: x, targetX: x };
    }

    // Adjacent "step" platforms: walk to the boundary and jump across it.
    const towardRight = from.right <= to.left + 40;
    const towardLeft = to.right <= from.left + 40;
    if (!towardRight && !towardLeft) return null;

    const dir = towardRight ? 1 : -1;
    const boundary = towardRight
      ? (from.right + to.left) / 2
      : (to.right + from.left) / 2;
    const wall = this.wallNear(boundary);

    // Both ends are measured from the wall's faces and padded by the body's
    // own half-width, so takeoff and landing sit clear of the wall's x-range.
    // Otherwise the arc gets judged at a moment the actor is standing at ledge
    // height, where no clearance is possible — and a jump *down* that starts
    // far back is already descending when it reaches the step, which clips it.
    const goingUp = to.top < from.top;
    const nearFace = wall ? (dir > 0 ? wall.left : wall.right) : boundary;
    const farFace = wall ? (dir > 0 ? wall.right : wall.left) : boundary;
    // Jitter the long side of the jump so actors do not queue at one exact spot.
    // Only ever longer: more run-up (going up) and more run-out (going down)
    // both put the wall later in the arc, which can only add clearance.
    const spread = Phaser.Math.Between(0, 220);
    const takeoffX = Phaser.Math.Clamp(
      nearFace - dir * (goingUp ? RUN_UP + spread : halfWidth + TAKEOFF_CLEAR),
      from.left + m,
      from.right - m,
    );
    const targetX = Phaser.Math.Clamp(
      farFace + dir * (goingUp ? halfWidth + LAND_CLEAR : RUN_OUT + spread),
      to.left + m,
      to.right - m,
    );
    return { target: to, takeoffX, targetX, clear: wall };
  }

  /** The step-face wall at a platform boundary, if the layout defines one. */
  private wallNear(boundary: number): TraversalPlan["clear"] {
    const wall = (this.layout.walls ?? []).find(
      (w) => Math.abs(w.x - boundary) <= 60,
    );
    return wall
      ? {
          left: wall.x - WALL_THICKNESS / 2,
          right: wall.x + WALL_THICKNESS / 2,
          top: wall.top,
        }
      : undefined;
  }

  private createActor(p: ParticipantView, stage: number): DigimonActor {
    const actor = new DigimonActor(
      this,
      this.assets,
      (from, halfWidth) => this.planTraversal(from, halfWidth),
      this.layout.edgeMargin,
      p,
      stage,
      this.labelsEnabled,
    );
    // Land on platforms only while falling; never collide with other Digimon.
    const collider = this.physics.add.collider(actor.sprite, this.platformBodies, (_s, obj) => {
      const body = actor.sprite.body as Phaser.Physics.Arcade.Body;
      const cfg = (obj as Phaser.GameObjects.Rectangle).getData("platform") as
        | PlatformConfig
        | undefined;
      if (cfg) {
        if (body.touching.down && actor.isAirborne) actor.onLanded(cfg);
        return;
      }
      // A wall. Arcade zeroes only the blocked axis, so a clipped jump would
      // otherwise keep climbing the face; stop the climb and push off.
      if (actor.isAirborne && (body.touching.left || body.touching.right))
        actor.onWallHit(body.touching.left ? 1 : -1);
    });
    this.actorColliders.set(p.twitchUserId, collider);
    this.actors.set(p.twitchUserId, actor);
    return actor;
  }

  private destroyActor(twitchUserId: string): void {
    this.activeEntrances.delete(twitchUserId);
    this.actorColliders.get(twitchUserId)?.destroy();
    this.actorColliders.delete(twitchUserId);
    this.actors.get(twitchUserId)?.destroy();
    this.actors.delete(twitchUserId);
  }

  private pickSpawnX(platform: PlatformConfig): number {
    const min = Math.max(platform.left + 40, this.layout.spawnZone.minX);
    const max = Math.min(platform.right - 40, this.layout.spawnZone.maxX);
    return max > min
      ? Phaser.Math.Between(min, max)
      : Phaser.Math.Between(platform.left + 40, platform.right - 40);
  }

  private randomPlatform(): PlatformConfig {
    return this.layout.platforms[
      Math.floor(Math.random() * this.layout.platforms.length)
    ];
  }

  /**
   * Wait for airborne actors to land under their own gravity, then hard-place
   * any stragglers. Bounded, because a death must not stall on one actor whose
   * arc cannot complete — a jump interrupted by a resize, or physics paused by
   * a hidden tab.
   */
  private async settleAirborne(actors: DigimonActor[], timeoutMs = 1500): Promise<void> {
    const airborne = actors.filter((a) => !a.hasGround);
    if (airborne.length === 0) return;

    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline && airborne.some((a) => !a.hasGround)) {
      await new Promise<void>((resolve) => this.time.delayedCall(50, resolve));
    }

    for (const actor of airborne) {
      if (actor.hasGround) continue;
      // Prefer where the actor was actually going. Deriving a platform from
      // (x, y) instead plants an upward jumper back on the platform it was
      // leaving — for most of an ascending arc it is still horizontally over
      // the origin — so it would drop ~300px onto the wrong ledge.
      const platform =
        actor.targetPlatform ?? this.landingPlatformFor(actor.sprite.x, actor.sprite.y);
      const min = platform.left + 40;
      const max = platform.right - 40;
      // Keep the actor's own x when usable. A bare clamp put every
      // out-of-bounds actor on the identical pixel column, stacking them.
      const x =
        actor.sprite.x >= min && actor.sprite.x <= max
          ? actor.sprite.x
          : Phaser.Math.Between(min, max);
      actor.placeStanding(platform, x);
    }
  }

  /**
   * Fallback for an airborne actor with no recorded jump target. Assumes the
   * platforms tile the canvas horizontally, as the shipped layout does; with a
   * real gap between platforms an actor over the gap falls through to
   * `randomPlatform()`, which would move it across the canvas.
   */
  private landingPlatformFor(x: number, y: number): PlatformConfig {
    const spanning = this.layout.platforms.filter((p) => x >= p.left && x <= p.right);
    const below = spanning
      .filter((p) => p.top >= y)
      .sort((a, b) => a.top - b.top);
    return below[0] ?? spanning[0] ?? this.randomPlatform();
  }

  private wait(ms: number): Promise<void> {
    return new Promise((r) => this.time.delayedCall(ms, r));
  }

  private drawDebug(): void {
    const g = this.add.graphics();
    g.lineStyle(2, 0x00ff88, 0.9);
    for (const p of this.layout.platforms) {
      g.lineBetween(p.left, p.top, p.right, p.top);
      g.lineStyle(1, 0x00ff88, 0.4);
      g.lineBetween(p.left + this.layout.edgeMargin, p.top - 10, p.left + this.layout.edgeMargin, p.top + 10);
      g.lineBetween(p.right - this.layout.edgeMargin, p.top - 10, p.right - this.layout.edgeMargin, p.top + 10);
      g.lineStyle(2, 0x00ff88, 0.9);
    }
    g.lineStyle(3, 0xff5577, 0.9);
    for (const w of this.layout.walls ?? []) {
      g.lineBetween(w.x, w.top, w.x, w.bottom);
    }
    g.lineStyle(2, 0xffaa00, 0.7);
    g.lineBetween(this.layout.spawnZone.minX, 20, this.layout.spawnZone.maxX, 20);
    if (this.layout.placeholder) {
      this.add
        .text(12, 12, "PLACEHOLDER LAYOUT — edit data/layout.json", {
          fontSize: "16px",
          color: "#ffaa00",
          stroke: "#000",
          strokeThickness: 3,
        })
        .setDepth(1000);
    }
  }
}
