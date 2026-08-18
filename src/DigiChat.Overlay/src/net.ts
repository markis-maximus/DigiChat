import * as signalR from "@microsoft/signalr";
import {
  Stage,
  type OverlayStateView,
  type DeathView,
  type ReincarnationView,
  type SpawnEventView,
  type StageChangeView,
} from "./types";

// When served by the backend (/overlay/) same-origin works; during `vite dev`
// the backend is on 5170.
export const API_BASE =
  location.port === "5173" ? "http://localhost:5170" : "";

export interface OverlayEvents {
  /** Resolves once the scene has actually rendered the state, rejects if not. */
  onResync(state: OverlayStateView): Promise<void>;
  /** All four resolve once the scene rendered the event, and reject if not. */
  onSpawn(spawn: SpawnEventView): Promise<void>;
  onStageChanged(change: StageChangeView): Promise<void>;
  onDeath(death: DeathView): Promise<void>;
  onReincarnation(view: ReincarnationView): Promise<void>;
}

/**
 * SignalR connection with automatic reconnect. The backend is authoritative:
 * on every (re)connect we pull the full state and hand it to onResync, so an
 * OBS reload / source hide / crash always reconstructs the clean current state
 * without replaying old animations (spec §19).
 */
export async function connect(events: OverlayEvents): Promise<void> {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(`${API_BASE}/hub`)
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: (ctx) =>
        Math.min(1000 * 2 ** Math.min(ctx.previousRetryCount, 5), 15000),
    })
    .configureLogging(signalR.LogLevel.Information)
    .build();

  let knownState: OverlayStateView | null = null;
  let liveEventVersion = 0;
  let lastLiveEventAt = 0;

  const noteLiveEvent = (): void => {
    liveEventVersion++;
    lastLiveEventAt = Date.now();
  };
  const stageName = (stage: number): string =>
    ["Fresh", "In-Training", "Rookie", "Champion", "Ultimate"][stage] ?? "Unknown";
  const renderedStateSignature = (state: OverlayStateView): string =>
    JSON.stringify({
      stage: state.stage,
      generationNumber: state.generationNumber,
      sessionNumber: state.sessionNumber,
      labelsEnabled: state.labelsEnabled,
      isDead: state.isDead,
      participants: state.participants
        .filter((p) => !p.awaitingLineage && !p.heldForReincarnation)
        .map((p) => ({
          twitchUserId: p.twitchUserId,
          displayName: p.displayName,
          lineageSlug: p.lineageSlug,
          formName: p.formName,
          assetKey: p.assetKey,
        }))
        .sort((a, b) => a.twitchUserId.localeCompare(b.twitchUserId)),
    });
  const acceptAuthoritativeState = (state: OverlayStateView, reason: string): void => {
    const changed = knownState === null
      || renderedStateSignature(knownState) !== renderedStateSignature(state);
    if (!changed) {
      knownState = state;
      return;
    }

    console.info(`[net] applying ${reason} state reconciliation`);
    void events.onResync(state).then(
      () => {
        knownState = state;
      },
      (err: unknown) => {
        // Deliberately leave knownState behind. It is the mirror the periodic
        // reconciliation compares against, so recording a state the scene
        // failed to draw would make every later snapshot look equal and strand
        // a wrong canvas until the next reconnect.
        console.error(`[net] ${reason} reconciliation failed; will retry`, err);
      },
    );
  };

  /**
   * Advance the mirror only once the scene has actually rendered the event.
   *
   * `knownState` is what the periodic reconciliation compares the authoritative
   * snapshot against. Recording an event the scene failed to draw makes every
   * later snapshot look equal, so the reconciler sees nothing to repair and the
   * stream stays visibly wrong until the streamer reloads the browser source.
   * Leaving the mirror behind instead guarantees the next comparison differs.
   */
  const applyLive = (
    advance: (from: OverlayStateView) => OverlayStateView,
    rendered: Promise<void>,
    what: string,
  ): void => {
    void rendered.then(
      () => {
        // Applied to whatever the mirror holds NOW, not to a copy captured when
        // the event arrived. Capturing at arrival means a second event whose
        // render finishes first is silently discarded by the first event's
        // stale snapshot, and the mirror then disagrees with the server —
        // which costs a full resync, i.e. the whole cast visibly teleporting.
        if (knownState) knownState = advance(knownState);
      },
      (err: unknown) => {
        console.error(`[net] ${what} failed to render; mirror left behind so reconciliation retries`, err);
      },
    );
  };

  connection.on("spawn", (s: SpawnEventView) => {
    noteLiveEvent();
    applyLive(
      (from) => ({
        ...from,
        stage: s.stage,
        stageName: stageName(s.stage),
        participants: [
          ...from.participants.filter((p) => p.twitchUserId !== s.participant.twitchUserId),
          s.participant,
        ],
      }),
      events.onSpawn(s),
      "spawn",
    );
  });
  connection.on("stageChanged", (c: StageChangeView) => {
    noteLiveEvent();
    applyLive(
      (from) => ({
        ...from,
        stage: c.toStage,
        stageName: stageName(c.toStage),
        participants: c.participants,
        isDead: false,
      }),
      events.onStageChanged(c),
      "stage change",
    );
  });
  connection.on("died", (d: DeathView) => {
    noteLiveEvent();
    applyLive(
      (from) => ({ ...from, participants: d.participants, isDead: true }),
      events.onDeath(d),
      "death",
    );
  });
  connection.on("reincarnation", (r: ReincarnationView) => {
    noteLiveEvent();
    applyLive(
      (from) => ({
        ...from,
        stage: Stage.Fresh,
        stageName: stageName(Stage.Fresh),
        generationNumber: r.newGenerationNumber,
        participants: r.participants,
        isDead: false,
      }),
      events.onReincarnation(r),
      "reincarnation",
    );
  });
  connection.on("stateResync", (s: OverlayStateView) => {
    noteLiveEvent();
    acceptAuthoritativeState(s, "server-requested");
  });
  connection.on("adminStatus", () => {}); // admin-only event; ignore here

  const wait = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

  const pullStateUntilSuccessful = async (reason: string): Promise<void> => {
    let delay = 500;
    while (connection.state === signalR.HubConnectionState.Connected) {
      try {
        const versionBeforePull = liveEventVersion;
        const state = await connection.invoke<OverlayStateView>("GetOverlayState");
        // If a live event arrived while the snapshot was in flight, that
        // snapshot may predate it. Pull again rather than applying stale state
        // after a newer spawn/transition.
        if (versionBeforePull !== liveEventVersion) continue;
        acceptAuthoritativeState(state, reason);
        return;
      } catch (err) {
        console.warn(`[net] ${reason} state pull failed; retrying`, err);
        await wait(delay);
        delay = Math.min(delay * 2, 15000);
      }
    }
  };

  // Never overlap snapshots. A periodic authoritative pull also repairs any
  // event that was lost after a server-side commit or during a brief reconnect.
  let syncTail: Promise<void> = Promise.resolve();
  const requestSync = (reason: string): Promise<void> => {
    syncTail = syncTail.then(() => pullStateUntilSuccessful(reason));
    return syncTail;
  };

  connection.onreconnected(() => {
    console.info("[net] reconnected — resyncing state");
    void requestSync("reconnect");
  });

  let startTask: Promise<void> | null = null;
  const ensureStarted = (): Promise<void> => {
    if (startTask) return startTask;
    startTask = (async () => {
      let delay = 1000;
      while (connection.state === signalR.HubConnectionState.Disconnected) {
        try {
          await connection.start();
          await requestSync("initial");
          return;
        } catch (err) {
          console.warn(`[net] connect failed, retrying in ${delay}ms`, err);
          await wait(delay);
          delay = Math.min(delay * 2, 15000);
        }
      }
    })().finally(() => {
      startTask = null;
      if (connection.state === signalR.HubConnectionState.Disconnected)
        setTimeout(() => void ensureStarted(), 1000);
    });
    return startTask;
  };

  connection.onclose(() => {
    console.warn("[net] connection closed — restarting");
    void ensureStarted();
  });

  setInterval(() => {
    // Compare a quiet-time snapshot to the locally tracked semantic state.
    // Do not rebuild unchanged actors every 30 seconds: that visibly teleports
    // the entire cast. A mismatch still triggers a full authoritative repair.
    if (connection.state === signalR.HubConnectionState.Connected
        && Date.now() - lastLiveEventAt >= 15000)
      void requestSync("periodic reconciliation");
  }, 30000);

  await ensureStarted();
}
