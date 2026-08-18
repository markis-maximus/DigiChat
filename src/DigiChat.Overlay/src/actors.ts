import Phaser from "phaser";
import type { AssetLibrary, AnimName } from "./assets";
import {
  Stage,
  type ParticipantView,
  type PlatformConfig,
  type TraversalPlan,
} from "./types";

const WALK_SPEED = 55;
export const GRAVITY = 1400;
/** How far above the destination ledge a jump aims to peak. */
const APEX_MARGIN = 70;
/** Added to the apex per retry while a wall is still in the way. */
const APEX_STEP = 70;
const MAX_ARC_ATTEMPTS = 8;
/** Feet must stay this far above a wall's top for the whole crossing. */
const WALL_CLEARANCE = 12;
/** Chance per idle beat of heading for the other platform. */
const TRAVERSAL_CHANCE = 0.4;
/** Push-off speed when a jump clips a wall anyway. */
const WALL_KNOCKBACK = 130;
/** Gap between the top of a Digimon's artwork and its name. */
const LABEL_GAP = 2;
/** How a dead Digimon rests: dark, faded, still legible as itself. */
const DEAD_TINT = 0x3a3f52;
const DEAD_ALPHA = 0.4;
/** Names render above every sprite, whatever order the cast was created in. */
const LABEL_DEPTH = 1;
const DEAD_LABEL_ALPHA = 0.55;

type BehaviorState =
  | "entering" // falling in from above the canvas
  | "landing"
  | "gettingUp"
  | "idle"
  | "walking"
  | "walkingToJump" // heading to a takeoff point for a planned traversal
  | "jumping" // traversing to the other platform
  | "frozen"; // digivolve / death / egg sequences own the sprite

/**
 * One roaming Digimon. Visual form varies wildly (placeholder or real art,
 * any size), but movement rules are identical for every species (spec §25):
 * same walk speed, same jump physics, same traversal logic.
 */
export class DigimonActor {
  readonly sprite: Phaser.Physics.Arcade.Sprite;
  private label: Phaser.GameObjects.Text;
  private state: BehaviorState = "entering";
  private behaviorTimer = 0;
  private direction: 1 | -1 = 1;
  private currentPlatform: PlatformConfig | null = null;
  private jumpTarget: PlatformConfig | null = null;
  private pendingJump: TraversalPlan | null = null;
  private currentAnim: AnimName = "idle";
  private roamTargetX: number | null = null;
  private entranceSettled: (() => void) | null = null;
  private landingTween: Phaser.Tweens.Tween | null = null;
  private eggWobble: Phaser.Tweens.Tween | null = null;
  /**
   * Suppresses new traversals. Set while a death settles everyone onto solid
   * ground; cleared when the generation comes back (undo) or hatches.
   */
  holdStill = false;
  private landingScaleY: number | null = null;
  participant: ParticipantView;
  stage: Stage;

  constructor(
    private scene: Phaser.Scene,
    private assets: AssetLibrary,
    /** Scene-provided route planner (overlap jump or step traversal). */
    private planTraversal: (
      from: PlatformConfig,
      halfWidth: number,
    ) => TraversalPlan | null,
    private edgeMargin: number,
    participant: ParticipantView,
    stage: Stage,
    labelsEnabled: boolean,
  ) {
    this.participant = participant;
    this.stage = stage;

    const texture = this.resolveTexture();
    this.sprite = scene.physics.add.sprite(0, 0, texture);
    this.sprite.setOrigin(0.5, 1); // feet anchor: y is where the feet touch
    this.applyBodySize();

    this.label = scene.add
      .text(0, 0, this.labelText(), {
        fontFamily: "Verdana, sans-serif",
        fontSize: "17px",
        fontStyle: "bold",
        color: "#ffffff",
        stroke: "#000000",
        strokeThickness: 5,
        align: "center",
      })
      // Outline plus a soft drop shadow: the overlay sits over whatever the
      // scene behind it happens to be, so white alone disappears on pale blues.
      .setShadow(0, 3, "#000000", 5, false, true)
      .setOrigin(0.5, 1)
      // Everything defaults to depth 0, so paint order is creation order and a
      // later chatter's sprite covers an earlier chatter's name. On a crowded
      // platform most labels end up behind somebody. Names always on top.
      .setDepth(LABEL_DEPTH)
      .setVisible(labelsEnabled);
  }

  // ---------------------------------------------------------------- appearance

  private resolveTexture(state: AnimName = this.currentAnim): string {
    const key = this.participant.assetKey;
    return (
      this.assets.stateTexture(key, state) ??
      this.assets.placeholderTexture(key, this.stage)
    );
  }

  /**
   * Point the sprite the way it is travelling. Art is assumed to be drawn
   * facing right; a form whose art faces left declares `facesLeft` and gets
   * mirrored the other way round.
   */
  private face(movingLeft: boolean): void {
    if (this.assets.facingLocked) {
      this.sprite.setFlipX(false);
      return;
    }
    const key = this.participant.assetKey;
    const facesLeft = (key ? this.assets.form(key)?.facesLeft : false) ?? false;
    this.sprite.setFlipX(movingLeft !== facesLeft);
  }

  /** Chatter first: the viewer is what the audience is looking for. */
  private labelText(): string {
    const form = this.participant.formName ?? "???";
    return `${this.participant.displayName}\n${form}`;
  }

  /**
   * Fits the art to its stage, then sizes the collision box. Real art is scaled
   * by a whole factor so the pixel grid survives; placeholders are drawn at
   * their stage size already. Anything in overrides.json is applied last.
   */
  private applyBodySize(): void {
    const body = this.sprite.body as Phaser.Physics.Arcade.Body;
    const key = this.participant.assetKey;
    const def = key ? this.assets.form(key) : undefined;
    const frameW = this.sprite.width;
    const frameH = this.sprite.height;

    // Everything measures against the artwork, not the canvas around it. Two
    // PNGs of the same size routinely hold very differently sized Digimon, and
    // padding below the feet would otherwise leave a form hovering.
    const art = this.assets.visibleBox(key, this.sprite.texture.key) ?? {
      x: 0,
      y: 0,
      width: frameW,
      height: frameH,
    };
    const feetLine = def?.footAnchorY != null ? frameH * def.footAnchorY : art.y + art.height;
    this.sprite.setOrigin((art.x + art.width / 2) / frameW, feetLine / frameH);

    const fitted = this.assets.hasRealArt(key)
      ? this.assets.fit(this.assets.idealScale(this.stage, art.height))
      : 1;
    this.sprite.setScale(fitted * (def?.scaleMultiplier ?? 1));

    const w = art.width * 0.8 * (def?.collisionWidthMultiplier ?? 1);
    const h = art.height * 0.92 * (def?.collisionHeightMultiplier ?? 1);
    body.setSize(w, h);
    body.setOffset(art.x + (art.width - w) / 2, feetLine - h);
  }

  /**
   * Each state is its own image, so "playing" one means swapping the texture —
   * and playing its animation too when that state ships multiple frames. States
   * a form does not supply resolve through the fallback table in AssetLibrary.
   */
  private playAnim(name: AnimName): void {
    this.currentAnim = name;
    const texture = this.resolveTexture(name);
    if (this.sprite.texture.key !== texture) {
      const x = this.sprite.x;
      const feetY = this.sprite.y;
      this.sprite.setTexture(texture);
      this.applyBodySize();
      this.sprite.setPosition(x, feetY); // keep the feet planted across a swap
    }

    const animKey = this.assets.animationFor(texture);
    if (animKey) this.sprite.play(animKey, true);
    else if (this.sprite.anims?.isPlaying) this.sprite.anims.stop();
  }

  /** Swap to the current participant/stage form, keeping feet planted. */
  applyForm(participant: ParticipantView, stage: Stage): void {
    this.participant = participant;
    this.stage = stage;
    const feetY = this.sprite.y;
    const x = this.sprite.x;
    this.sprite.setTexture(this.resolveTexture());
    this.applyBodySize();
    this.sprite.setPosition(x, feetY);
    this.label.setText(this.labelText());
  }

  setLabelsVisible(visible: boolean): void {
    this.label.setVisible(visible);
  }

  // ---------------------------------------------------------------- lifecycle

  /** Entrance (spec §23): spawn above the canvas, fall, land, get up, roam. */
  enterFalling(x: number): Promise<void> {
    // Resolve any previous waiter defensively before starting a new entrance.
    this.cancelLandingTween();
    this.completeEntrance();
    this.state = "entering";
    this.sprite.setPosition(x, -60);
    this.sprite.setVelocity(0, 50);
    this.playAnim("fall");
    return new Promise<void>((resolve) => {
      this.entranceSettled = resolve;
    });
  }

  /**
   * Whether a platform is currently under this actor. Broader than
   * {@link isAirborne}, which asks about the entering/jumping *states* for
   * landing detection: this stays false through any state where the actor has
   * left its platform and not yet acquired another.
   */
  get hasGround(): boolean {
    return this.currentPlatform !== null;
  }

  /** Restore without entrance animation (resync after reload, spec §19). */
  placeStanding(platform: PlatformConfig, x: number): void {
    this.cancelLandingTween();
    this.currentPlatform = platform;
    this.sprite.setPosition(x, platform.top);
    this.sprite.setVelocity(0, 0);
    this.toIdle();
    this.completeEntrance();
  }

  destroy(): void {
    this.cancelLandingTween();
    this.completeEntrance();
    this.sprite.destroy();
    this.label.destroy();
  }

  // ---------------------------------------------------------------- behavior

  /** Called from the scene's collider when feet touch a platform surface. */
  onLanded(platform: PlatformConfig): void {
    if (this.state === "entering" || this.state === "jumping") {
      const completedEntrance = this.state === "entering";
      this.currentPlatform = platform;
      this.sprite.setVelocityX(0);
      this.state = "landing";
      this.playAnim("land");
      // Procedural squash doubles as the missing-Land fallback (spec §30).
      this.cancelLandingTween();
      this.landingScaleY = this.sprite.scaleY;
      const tween = this.scene.tweens.add({
        targets: this.sprite,
        scaleY: this.sprite.scaleY * 0.82,
        duration: 90,
        yoyo: true,
        onComplete: () => {
          // A global transition or forced-ground fallback may have taken
          // ownership while this tween was running. Never let that stale
          // callback unfreeze the actor or replace its transition animation.
          if (this.landingTween !== tween || this.state !== "landing") return;
          this.landingTween = null;
          this.landingScaleY = null;
          this.state = "gettingUp";
          this.playAnim("getup");
          this.behaviorTimer = 350; // Get Up → Idle after a beat
          if (completedEntrance) this.completeEntrance();
        },
      });
      this.landingTween = tween;
    }
  }

  private cancelLandingTween(): void {
    this.landingTween?.stop();
    this.landingTween?.remove();
    this.landingTween = null;
    if (this.landingScaleY !== null && this.sprite.active)
      this.sprite.setScale(this.sprite.scaleX, this.landingScaleY);
    this.landingScaleY = null;
  }

  private completeEntrance(): void {
    const resolve = this.entranceSettled;
    this.entranceSettled = null;
    resolve?.();
  }

  update(delta: number): void {
    const body = this.sprite.body as Phaser.Physics.Arcade.Body;

    // Self-heal: grounded states always own a platform, so losing one means a
    // landing was missed somewhere. Fall again and let the collider re-land us
    // rather than idling in place forever.
    if (!this.currentPlatform && (this.state === "idle" || this.state === "walking"))
      this.resumeFalling();

    // Label follows the sprite (spec §26). Measured from the top of the
    // *artwork*, not the frame: displayHeight includes transparent padding, so
    // a form padded above its head pushed its name absurdly high. Clamped so a
    // tall Digimon on the upper platform cannot shove its name off the canvas.
    const art = this.assets.visibleBox(this.participant.assetKey, this.sprite.texture.key);
    const artTop = this.sprite.y - (art ? art.height : this.sprite.height) * this.sprite.scaleY;
    this.label.setPosition(
      this.sprite.x,
      Math.max(this.label.height + 2, artTop - LABEL_GAP),
    );

    switch (this.state) {
      case "frozen":
      case "entering":
      case "landing":
        return;

      case "gettingUp":
        this.behaviorTimer -= delta;
        if (this.behaviorTimer <= 0) this.toIdle();
        return;

      case "jumping":
        if (body.velocity.y > 0) this.playAnim("fall");
        return;

      case "idle":
        this.behaviorTimer -= delta;
        if (this.behaviorTimer <= 0) this.decideNextAction();
        return;

      case "walking": {
        this.behaviorTimer -= delta;
        const p = this.currentPlatform;
        if (!p) return this.toIdle();
        // Walk to a chosen spot rather than for a random duration: a timed walk
        // covers at most ~190px, so everyone stayed bunched near wherever they
        // last landed instead of using the whole platform.
        const target = Phaser.Math.Clamp(
          this.roamTargetX ?? this.sprite.x,
          p.left + this.walkInset(body),
          p.right - this.walkInset(body),
        );
        const dx = target - this.sprite.x;
        if (Math.abs(dx) <= 4 || this.behaviorTimer <= 0) return this.toIdle();

        this.direction = dx > 0 ? 1 : -1;
        body.setVelocityX(WALK_SPEED * this.direction);
        this.face(this.direction < 0);
        return;
      }

      case "walkingToJump": {
        this.behaviorTimer -= delta;
        const plan = this.pendingJump;
        // A jump already committed still has to be abandoned once a death is
        // settling: suppressing only new decisions leaves an actor walking to
        // its takeoff point, leaping, and freezing in open air regardless.
        if (this.holdStill) {
          this.pendingJump = null;
          return this.toIdle();
        }
        if (!plan || this.behaviorTimer <= 0) {
          this.pendingJump = null;
          return this.toIdle(); // couldn't reach takeoff in time; give up calmly
        }
        const dx = plan.takeoffX - this.sprite.x;
        if (Math.abs(dx) <= 10) {
          this.pendingJump = null;
          // No arc clears the wall from here → stay put rather than clip it.
          if (!this.startJump(plan)) return this.toIdle();
          return;
        }
        this.direction = dx > 0 ? 1 : -1;
        body.setVelocityX(WALK_SPEED * this.direction);
        this.face(this.direction < 0);
        return;
      }
    }
  }

  private toIdle(): void {
    this.state = "idle";
    (this.sprite.body as Phaser.Physics.Arcade.Body).setVelocityX(0);
    this.behaviorTimer = Phaser.Math.Between(1200, 4200);
    this.playAnim("idle");
  }

  private decideNextAction(): void {
    // Settling for a death: no new traversals, or an actor that was safely on
    // the ground when the kill arrived can leap during the settle wait and be
    // frozen in open air anyway.
    if (this.holdStill) return this.toIdle();
    if (this.currentPlatform && Math.random() < TRAVERSAL_CHANCE) {
      const body = this.sprite.body as Phaser.Physics.Arcade.Body;
      const plan = this.planTraversal(this.currentPlatform, body.halfWidth);
      if (plan) {
        this.pendingJump = plan;
        this.state = "walkingToJump";
        this.behaviorTimer = 20000; // generous cap: longest walk across a platform
        this.playAnim("walk");
        return;
      }
    }
    const p = this.currentPlatform;
    if (!p) return this.toIdle();
    // Anywhere on this platform, drawn independently per actor, so the group
    // spreads out over time instead of orbiting the step they last used.
    const body = this.sprite.body as Phaser.Physics.Arcade.Body;
    const inset = this.walkInset(body);
    this.roamTargetX = Phaser.Math.Between(p.left + inset, p.right - inset);

    this.state = "walking";
    this.direction = this.roamTargetX > this.sprite.x ? 1 : -1;
    // Enough time to actually arrive, with slack; toIdle happens on arrival.
    const distance = Math.abs(this.roamTargetX - this.sprite.x);
    this.behaviorTimer = (distance / WALK_SPEED) * 1000 * 1.7 + 900;
    this.playAnim("walk");
  }

  /**
   * Keeps the whole body on the platform. Applied to the sprite's centre, the
   * plain edge margin would let a big form stand half over the drop.
   */
  private walkInset(body: Phaser.Physics.Arcade.Body): number {
    return Math.max(this.edgeMargin, body.halfWidth);
  }

  /**
   * Identical jump physics for every species (spec §25): velocity derived from
   * the height difference, never from the sprite. Returns false when no safe
   * arc exists, so the caller can stay put instead of jumping into a wall.
   */
  private startJump(plan: TraversalPlan): boolean {
    const arc = this.solveArc(plan);
    if (!arc) return false;

    const body = this.sprite.body as Phaser.Physics.Arcade.Body;
    this.state = "jumping";
    this.jumpTarget = plan.target;
    this.currentPlatform = null;

    body.setVelocity(arc.vx, arc.vy);
    this.face(arc.vx < 0);
    this.playAnim("jump");
    return true;
  }

  /**
   * Aiming the apex just above the destination ledge is not enough when a wall
   * stands in between: contact starts half a body-width before the wall face,
   * and at that point in the arc the feet are still well below the ledge — the
   * old arc clipped the step on every upward traversal. Raise the apex until
   * the feet clear the wall at first contact.
   */
  private solveArc(plan: TraversalPlan): { vx: number; vy: number } | null {
    const dy = this.sprite.y - plan.target.top; // positive when jumping up
    const dx = plan.targetX - this.sprite.x;
    let apexAbove = Math.max(dy, 0) + APEX_MARGIN;

    for (let attempt = 0; attempt <= MAX_ARC_ATTEMPTS; attempt++) {
      const vy = -Math.sqrt(2 * GRAVITY * apexAbove);
      const timeUp = -vy / GRAVITY;
      const timeDown = Math.sqrt((2 * Math.max(apexAbove - dy, 10)) / GRAVITY);
      const vx = dx / (timeUp + timeDown);

      if (!plan.clear || this.clearsWall(plan.clear, vx, vy)) return { vx, vy };
      apexAbove += APEX_STEP;
    }
    return null;
  }

  /**
   * Do the feet stay above `wall.top` for the whole crossing? Checking only
   * where the body arrives is not enough: a jump *down* a step is still
   * descending on the way out and would clip the far edge of the face. Feet
   * height is convex in time, so the worst moment is one of the two ends.
   */
  private clearsWall(
    wall: NonNullable<TraversalPlan["clear"]>,
    vx: number,
    vy: number,
  ): boolean {
    if (vx === 0) return false;
    const { halfWidth } = this.sprite.body as Phaser.Physics.Arcade.Body;
    const entryX = vx > 0 ? wall.left - halfWidth : wall.right + halfWidth;
    const exitX = vx > 0 ? wall.right + halfWidth : wall.left - halfWidth;
    const tIn = (entryX - this.sprite.x) / vx;
    const tOut = (exitX - this.sprite.x) / vx;
    if (tOut <= 0) return true; // the wall is behind us

    const feetAt = (t: number) =>
      this.sprite.y + vy * t + 0.5 * GRAVITY * t * t;
    const worst = Math.max(tIn <= 0 ? -Infinity : feetAt(tIn), feetAt(tOut));
    return worst <= wall.top - WALL_CLEARANCE;
  }

  /**
   * Clipped a wall mid-jump. Arcade has already cancelled the horizontal
   * velocity; cancel the climb too and push off, so it reads as a bonk and a
   * drop rather than sliding up the face.
   */
  onWallHit(pushDirection: 1 | -1): void {
    if (!this.isAirborne) return;
    const body = this.sprite.body as Phaser.Physics.Arcade.Body;
    if (body.velocity.y < 0) body.setVelocityY(0);
    body.setVelocityX(WALL_KNOCKBACK * pushDirection);
    this.jumpTarget = null; // traversal failed; land wherever it falls
    this.playAnim("fall");
  }

  get isAirborne(): boolean {
    return this.state === "entering" || this.state === "jumping";
  }

  get targetPlatform(): PlatformConfig | null {
    return this.jumpTarget;
  }

  // ---------------------------------------------------------------- effects

  freeze(): void {
    this.cancelLandingTween();
    // cancelLandingTween stops the tween without firing onComplete, so the
    // entrance promise would never settle. Every caller currently sits behind
    // settleActiveEntrances' 1500ms fallback, but that is incidental cover;
    // resolving here makes it structural.
    this.completeEntrance();
    this.state = "frozen";
    const body = this.sprite.body as Phaser.Physics.Arcade.Body;
    body.setVelocity(0, 0);
    body.setAllowGravity(false);
    this.sprite.anims?.stop();
  }

  unfreeze(): void {
    this.holdStill = false;
    (this.sprite.body as Phaser.Physics.Arcade.Body).setAllowGravity(true);
    // A transition can interrupt a jump or an entrance, leaving the actor in
    // mid-air with no platform. Going straight to idle strands it for good:
    // landings are only detected while airborne, so it would never reacquire a
    // platform and every later walk or jump would bail out.
    if (!this.currentPlatform) return this.resumeFalling();
    this.toIdle();
  }

  /** Fall until the collider puts us back on a platform. */
  private resumeFalling(): void {
    this.state = "jumping";
    this.jumpTarget = null;
    this.playAnim("fall");
  }

  /** Reusable digivolution effect (spec §27): glow → swap form → settle. */
  async digivolve(participant: ParticipantView, stage: Stage): Promise<void> {
    this.freeze();
    await this.tween({ alpha: 0.85, duration: 120, yoyo: true, repeat: 2 });
    this.sprite.setTint(0xffffff).setTintMode(Phaser.TintModes.FILL);
    await this.tween({ scaleX: this.sprite.scaleX * 1.15, scaleY: this.sprite.scaleY * 1.15, duration: 350, yoyo: true });
    this.applyForm(participant, stage);
    this.sprite.setTint(0xffffff).setTintMode(Phaser.TintModes.FILL);
    this.burstParticles(0x9ad9ff);
    await this.delay(220);
    this.sprite.clearTint();
    this.sprite.setTintMode(Phaser.TintModes.MULTIPLY);
    this.unfreeze();
  }

  /** Death dissolve (spec §28): freeze → darken → pixel-dissolve → gone. */
  /**
   * Death (spec §28). Unlike the old one-shot sequence this does not end in
   * nothing: the Digimon settles into a dark silhouette and stays there, for as
   * long as the generation is dead, so the scene still has something in it.
   */
  async dissolve(): Promise<void> {
    this.freeze();
    this.sprite.setTint(DEAD_TINT).setTintMode(Phaser.TintModes.MULTIPLY);
    await this.delay(300);
    this.burstParticles(0x8888aa);
    await this.tween({ alpha: DEAD_ALPHA, duration: 900 });
    this.applyDeadLook();
  }

  /**
   * The resting appearance of a dead Digimon. Applied at the end of the death
   * animation and again when the overlay rebuilds itself — an OBS reload during
   * the dead phase has to look identical, since the death is stored state.
   */
  applyDeadLook(): void {
    this.freeze();
    this.sprite.setVisible(true);
    this.sprite.setAlpha(DEAD_ALPHA);
    this.sprite.setTint(DEAD_TINT).setTintMode(Phaser.TintModes.MULTIPLY);
    this.label.setAlpha(DEAD_LABEL_ALPHA);
  }

  /** Egg for someone held during the death: fades in instead of popping. */
  async showEggFadingIn(): Promise<void> {
    this.showEgg();
    this.sprite.setAlpha(0);
    await this.tween({ alpha: 1, duration: 500 });
  }

  /** Generic shared egg appears where the Digimon stood (spec §32). */
  showEgg(): void {
    // An egg never walks. Corpses are already frozen by the death, but a
    // participant held during the death gets a brand-new actor at reincarnation
    // that is born live — without this it falls, walks, and swaps back to its
    // Digimon texture before hatching.
    this.freeze();
    const feetY = this.sprite.y;
    const x = this.sprite.x;
    this.sprite.setTexture(this.assets.eggTexture());
    this.sprite.setScale(1);
    this.sprite.clearTint();
    this.sprite.setAlpha(1);
    this.sprite.setVisible(true);
    this.applyBodySizeForEgg();
    this.sprite.setPosition(x, feetY);
    // Keep a handle and kill any previous one. An actor that never hatches —
    // one whose new-generation participant is awaiting a lineage, or is absent
    // from the reincarnation view — keeps its egg, and the next reincarnation
    // would stack a second infinite tween on the same angle, then a third.
    this.eggWobble?.remove();
    this.eggWobble = this.scene.tweens.add({
      targets: this.sprite,
      angle: { from: -4, to: 4 },
      duration: 700,
      yoyo: true,
      repeat: -1,
    });
  }

  private applyBodySizeForEgg(): void {
    const body = this.sprite.body as Phaser.Physics.Arcade.Body;
    body.setSize(this.sprite.width * 0.8, this.sprite.height * 0.9);
    body.setOffset(this.sprite.width * 0.1, this.sprite.height * 0.1);
  }

  /** Shared hatch: crack shake → flash → reveal the new Fresh form. */
  async hatch(participant: ParticipantView, labelsEnabled: boolean): Promise<void> {
    this.holdStill = false; // a new generation roams again
    this.scene.tweens.killTweensOf(this.sprite);
    this.sprite.setAngle(0);
    await this.tween({ angle: 8, duration: 70, yoyo: true, repeat: 5 });
    this.burstParticles(0xfff2b0);
    this.applyForm(participant, Stage.Fresh);
    this.sprite.setAlpha(0);
    await this.tween({ alpha: 1, duration: 250 });
    this.label.setAlpha(1); // undo the dimming the death applied
    this.label.setVisible(labelsEnabled);
    this.unfreeze();
  }

  // ---------------------------------------------------------------- helpers

  private tween(cfg: Record<string, unknown>): Promise<void> {
    return new Promise((resolve) => {
      this.scene.tweens.add({
        targets: this.sprite,
        ...cfg,
        onComplete: () => resolve(),
      });
    });
  }

  private delay(ms: number): Promise<void> {
    return new Promise((r) => this.scene.time.delayedCall(ms, r));
  }

  private burstParticles(tint: number): void {
    const emitter = this.scene.add.particles(this.sprite.x, this.sprite.y - this.sprite.displayHeight / 2, "particle-px", {
      speed: { min: 40, max: 140 },
      lifespan: 500,
      quantity: 18,
      scale: { start: 1.4, end: 0 },
      tint,
      emitting: false,
    });
    // Above the labels. Before labels had an explicit depth everything sat at
    // 0 and paint order was creation order, so bursts — created last — drew on
    // top; keeping that reads better for a momentary digivolve/hatch effect.
    emitter.setDepth(LABEL_DEPTH + 1);
    emitter.explode(18);
    this.scene.time.delayedCall(700, () => emitter.destroy());
  }
}
