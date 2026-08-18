import * as signalR from "@microsoft/signalr";
import { canStartBrowserCommand, disableCommandsUntilStatus, isCurrentSnapshot } from "./command-guard";

const API = location.port === "5174" ? "http://localhost:5170" : "";
const COMMAND_TIMEOUT_MS = 15_000;
const STATUS_TIMEOUT_MS = 5_000;

interface ParticipantView {
  twitchUserId: string;
  displayName: string;
  awaitingLineage: boolean;
  lineageName: string | null;
  formName: string | null;
  joinedUtc: string;
}

interface AdminStatusView {
  generationNumber: number;
  sessionNumber: number | null;
  stage: number;
  stageName: string;
  twitchStatus: string;
  transitionActive: boolean;
  participantCount: number;
  assignedLineages: number;
  totalLineages: number;
  awaitingLineageCount: number;
  lastUndoableAction: string | null;
  warnings: string[];
  participants: ParticipantView[];
  isDead: boolean;
  heldForReincarnationCount: number;
  lastUndoableTransitionId: number | null;
  /** Monotonic projection stamp; see render() for why it is needed. */
  revision: number;
}

const STAGES = [
  { name: "Fresh", path: "fresh" },
  { name: "In-Training", path: "in-training" },
  { name: "Rookie", path: "rookie" },
  { name: "Champion", path: "champion" },
  { name: "Ultimate", path: "ultimate" },
];

const $ = <T extends HTMLElement>(id: string): T =>
  document.getElementById(id) as T;

let current: AdminStatusView | null = null;
let commandInFlight = false;

// ---------------------------------------------------------------- rendering

function render(status: AdminStatusView): void {
  if (!isCurrentSnapshot(current, status)) return;
  current = status;
  $("s-session").textContent =
    status.sessionNumber != null ? `#${status.sessionNumber}` : "none — start one";
  $("s-generation").textContent = `#${status.generationNumber}`;
  $("s-stage").textContent = status.stageName;
  $("s-twitch").textContent = status.twitchStatus;
  $("s-participants").textContent = String(status.participantCount);
  $("s-lineages").textContent =
    `${status.assignedLineages} / ${status.totalLineages}` +
    (status.awaitingLineageCount > 0 ? ` (+${status.awaitingLineageCount} waiting)` : "");

  const warningsEl = $("warnings");
  if (status.warnings.length) {
    warningsEl.textContent = status.warnings.join("\n");
    warningsEl.className = "warnings";
  } else {
    warningsEl.textContent = "No warnings.";
    warningsEl.className = "ok";
  }

  const buttons = $("stage-buttons").querySelectorAll("button");
  buttons.forEach((btn, i) => {
    btn.classList.toggle("active", i === status.stage);
    // The dead do not digivolve — the backend refuses it too.
    btn.disabled = commandInFlight || status.transitionActive || status.isDead;
    (btn as HTMLButtonElement).title = status.isDead
      ? "They're dead — undo the death, or reincarnate"
      : "";
  });
  // Kill and Reincarnate are mutually exclusive: one is always the only legal move.
  const killBtn = $("btn-kill") as HTMLButtonElement;
  killBtn.disabled = commandInFlight || status.transitionActive || status.isDead;
  killBtn.title = status.isDead ? "Already dead — reincarnate, or undo the death" : "Kill every Digimon on screen";

  const reBtn = $("btn-reincarnate") as HTMLButtonElement;
  reBtn.disabled = commandInFlight || status.transitionActive || !status.isDead;
  reBtn.title = status.isDead
    ? status.heldForReincarnationCount > 0
      ? `Hatch a new generation — ${status.heldForReincarnationCount} chatter(s) waiting will join`
      : "Hatch a new generation"
    : "Kill them first — reincarnation only follows a death";

  ($("btn-session") as HTMLButtonElement).disabled = commandInFlight || status.transitionActive;
  const undoBtn = $("btn-undo") as HTMLButtonElement;
  undoBtn.disabled = commandInFlight || status.transitionActive || !status.lastUndoableAction;
  undoBtn.title = status.lastUndoableAction
    ? `Will undo: ${status.lastUndoableAction}`
    : "Nothing to undo — reincarnation is final, so it seals everything before it";
  $("transition-note").classList.toggle("show", status.transitionActive);
  // The Dev/Mock buttons aren't part of the status projection, so nothing above
  // re-enables them. Without this pass the blanket disable below strands them
  // off after the first command and mock testing dies until a page reload.
  for (const id of ["dev-send", "dev-bulk5", "dev-bulk31", "dev-dup"]) {
    const devButton = document.getElementById(id) as HTMLButtonElement | null;
    if (devButton) devButton.disabled = commandInFlight;
  }
  if (commandInFlight)
    document.querySelectorAll<HTMLButtonElement>("button").forEach((button) => (button.disabled = true));

  const tbody = $("participants");
  tbody.innerHTML = "";
  for (const p of status.participants) {
    const tr = document.createElement("tr");
    const joined = new Date(p.joinedUtc).toLocaleTimeString();
    const lineage = p.awaitingLineage ? "⚠ awaiting lineage" : (p.lineageName ?? "—");
    const form = p.formName ?? "—";
    for (const text of [p.displayName, lineage, form, joined]) {
      const td = document.createElement("td");
      td.textContent = text;
      tr.appendChild(td);
    }
    tbody.appendChild(tr);
  }
}

// ---------------------------------------------------------------- commands

function toast(message: string): void {
  const el = $("toast");
  el.textContent = message;
  el.classList.add("show");
  setTimeout(() => el.classList.remove("show"), 3500);
}

async function fetchJsonWithTimeout<T>(
  input: RequestInfo | URL,
  init: RequestInit = {},
  timeoutMs = COMMAND_TIMEOUT_MS,
): Promise<{ response: Response; data: T | null }> {
  const controller = new AbortController();
  const timer = window.setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetch(input, { ...init, signal: controller.signal });
    let data: T | null = null;
    try {
      data = (await response.json()) as T;
    } catch (err) {
      // Invalid/empty JSON retains the previous best-effort fallback. An
      // aborted body read must escape so the command latch is released.
      if (controller.signal.aborted) throw err;
    }
    return { response, data };
  } finally {
    window.clearTimeout(timer);
  }
}

async function post(path: string, body?: unknown): Promise<void> {
  if (!canStartBrowserCommand(current, commandInFlight)) {
    if (current === null && !commandInFlight)
      toast("Waiting for authoritative status; command not sent.");
    return;
  }
  const expectedState = current;
  commandInFlight = true;
  if (current) render(current);
  try {
    const headers: Record<string, string> = { "X-DigiChat-Command": "1" };
    if (body) headers["Content-Type"] = "application/json";
    if (path === "/api/admin/session/start")
      headers["X-DigiChat-Expected-Session"] = String(expectedState?.sessionNumber ?? 0);
    if (path === "/api/admin/undo" && expectedState?.lastUndoableTransitionId != null)
      headers["X-DigiChat-Expected-Undo"] = String(expectedState.lastUndoableTransitionId);
    const { response: res, data: parsed } = await fetchJsonWithTimeout<{
      error?: string;
      message?: string;
      outcomeName?: string;
    }>(
      `${API}${path}`,
      {
        method: "POST",
        headers,
        body: body ? JSON.stringify(body) : undefined,
      },
    );
    const data = parsed ?? {};
    if (!res.ok) {
      toast(`Error: ${data.error ?? res.statusText}`);
      return;
    }
    if (typeof data.message === "string") toast(data.message);
    else if (typeof data.outcomeName === "string") toast(`Mock chat: ${data.outcomeName}`);
  } catch (err) {
    if (err instanceof DOMException && err.name === "AbortError")
      toast("Command timed out; refreshing authoritative status.");
    else toast(`Request failed: ${err}`);
  } finally {
    // Pull once after every command. This closes the small gap before a
    // SignalR status push arrives and keeps the buttons latched on double-click.
    try {
      const { response: status, data } = await fetchJsonWithTimeout<AdminStatusView>(
        `${API}/api/admin/status`,
        {},
        STATUS_TIMEOUT_MS,
      );
      if (status.ok && data) current = data;
    } catch {
      // The normal reconnect/status paths will recover when the backend returns.
    }
    commandInFlight = false;
    if (current) render(current);
  }
}

function wireControls(): void {
  const stageContainer = $("stage-buttons");
  for (const s of STAGES) {
    const btn = document.createElement("button");
    btn.textContent = s.name;
    // Normal stage selection is deliberately one-click (spec §37).
    btn.onclick = () => void post(`/api/admin/stage/${s.path}`);
    stageContainer.appendChild(btn);
  }

  $("btn-kill").onclick = () => {
    if (
      confirm(
        "Kill every Digimon?\n\nThey stay dead — through restarts — until you reincarnate " +
          "them, so take as long as you like. Chatters who arrive meanwhile are held " +
          "back for the next egg.\n\nUndo brings them back with the lineages they have now.",
      )
    )
      void post("/api/admin/kill");
  };

  $("btn-reincarnate").onclick = () => {
    const held = current?.heldForReincarnationCount ?? 0;
    if (
      confirm(
        "Reincarnate?\n\nThe generation ends and everyone hatches into a new Fresh lineage" +
          (held > 0 ? `, including ${held} chatter(s) held during the death` : "") +
          ".\n\nThis CANNOT be undone.",
      )
    )
      void post("/api/admin/reincarnate");
  };

  $("btn-undo").onclick = () => {
    const action = current?.lastUndoableAction;
    if (!action) return;
    if (confirm(`Undo last transition?\n\nThis will undo: ${action}`))
      void post("/api/admin/undo");
  };

  $("btn-session").onclick = () => {
    if (
      confirm(
        "Start a new overlay session?\n\nThe overlay will clear. Everyone currently visible " +
          "disappears until they chat again. Lineage assignments are NOT affected.\n\n" +
          "This only affects the overlay on this PC. Nothing is sent to Twitch and " +
          "your viewers see nothing.",
      )
    )
      void post("/api/admin/session/start");
  };

  $("dev-send").onclick = () => {
    const name = ($("dev-name") as HTMLInputElement).value.trim() || "TestViewer";
    void post("/api/dev/chat", { login: name.toLowerCase(), displayName: name });
  };
  $("dev-bulk5").onclick = () => void post("/api/dev/chat/bulk?count=5");
  $("dev-bulk31").onclick = () => void post("/api/dev/chat/bulk?count=31");
  $("dev-dup").onclick = () => {
    // Same EventSub message ID twice — the second must be a no-op (spec §15).
    const id = `dup-test-${Date.now()}`;
    const name = `DupTest${Math.floor(Math.random() * 1000)}`;
    const body = { login: name.toLowerCase(), displayName: name, messageId: id };
    void post("/api/dev/chat", body).then(() => post("/api/dev/chat", body));
  };

  // The HTML is interactive before SignalR can deliver its first projection.
  // Keep every command visibly locked as well as guarded in post(); render()
  // applies the state-specific availability once authoritative status arrives.
  disableCommandsUntilStatus(document.querySelectorAll<HTMLButtonElement>("button"));
}

// ---------------------------------------------------------------- live updates

/// The Dev/Mock chat endpoints only exist in mock mode; in live mode the panel
/// would just 404 on every button, so hide it. An older backend without this
/// endpoint leaves the panel exactly as it was.
async function hideDevPanelInLiveMode(): Promise<void> {
  try {
    const res = await fetch(`${API}/api/config/features`);
    if (!res.ok) return;
    const { devChat } = (await res.json()) as { devChat: boolean };
    if (!devChat) $("dev-panel").style.display = "none";
  } catch {
    // Backend not reachable yet; the status poll reports connection problems.
  }
}

async function start(): Promise<void> {
  wireControls();
  void hideDevPanelInLiveMode();

  const connection = new signalR.HubConnectionBuilder()
    .withUrl(`${API}/hub`)
    // SignalR's default policy gives up permanently after ~42 seconds, which
    // silently freezes the panel on stale state — mid-stream, with no warning.
    // Retry forever, backing off to 15s, exactly as the overlay does.
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: (ctx) =>
        Math.min(1000 * 2 ** Math.min(ctx.previousRetryCount, 5), 15000),
    })
    .build();

  connection.on("adminStatus", (s: AdminStatusView) => render(s));
  // Events that change what the table shows but carry overlay-shaped payloads:
  const refresh = async () => render(await connection.invoke<AdminStatusView>("GetAdminStatus"));
  connection.on("stateResync", () => void refresh());
  connection.onreconnected(() => void refresh());

  try {
    await connection.start();
    await refresh();
  } catch (err) {
    toast(`Backend unreachable: ${err}`);
    setTimeout(() => location.reload(), 4000);
  }

  // Transition lock is time-based; poll briefly while active so buttons unlock.
  setInterval(() => {
    if (current?.transitionActive) void refresh();
  }, 1500);
}

void start();
