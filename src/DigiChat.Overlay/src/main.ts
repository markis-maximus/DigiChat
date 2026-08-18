import Phaser from "phaser";
import { loadLayout } from "./layout";
import { connect } from "./net";
import { OverlayScene } from "./scene";

async function boot(): Promise<void> {
  const layout = await loadLayout();
  const scene = new OverlayScene(layout);
  await scene.preloadManifest();

  const game = new Phaser.Game({
    type: Phaser.AUTO,
    parent: "game",
    width: layout.canvas.width,
    height: layout.canvas.height,
    transparent: true, // OBS Browser Source composites over the game capture
    pixelArt: true, // nearest-neighbour: sprite art must not be smoothed
    roundPixels: true, // and must not land on half-pixels while walking
    fps: { target: 30, forceSetTimeOut: true }, // spec §20: 30 FPS is the target
    physics: { default: "arcade", arcade: { debug: layout.debug } },
    scene,
  });

  // Debug handle for console inspection (not used by application code).
  (window as unknown as Record<string, unknown>).__digichat = { game, scene };

  game.events.once("scene-ready", () => {
    void connect({
      onResync: (s) => scene.applyResync(s),
      onSpawn: (s) => scene.onSpawn(s),
      onStageChanged: (c) => scene.onStageChanged(c),
      onDeath: (d) => scene.onDeath(d),
      onReincarnation: (r) => scene.onReincarnation(r),
    });
  });
}

void boot();
