interface DisableableCommandControl {
  disabled: boolean;
}

interface CommandControlCollection {
  forEach(callback: (control: DisableableCommandControl) => void): void;
}

/**
 * Browser commands need both a server projection and an idle local latch.
 * Native API callers intentionally remain independent of this UI-only guard.
 */
export function canStartBrowserCommand<T>(
  status: T | null,
  commandInFlight: boolean,
): status is T {
  return status !== null && !commandInFlight;
}

/**
 * True when an incoming snapshot may replace what is on screen.
 *
 * Pushed broadcasts and explicit pulls race, so a snapshot projected *before*
 * the pull we just awaited can still arrive *after* it. Rendering it would
 * silently revert the panel to older state — wrong participant count, stale
 * stage, buttons enabled that the backend will refuse. Revisions come from a
 * monotonic server-side counter stamped at projection time, and projections
 * read current state, so a higher revision can never describe older data.
 *
 * A snapshot with no revision (an older backend) is always accepted, and an
 * equal revision is accepted so a re-render of the same state is harmless.
 */
export function isCurrentSnapshot(
  rendered: { revision: number } | null,
  incoming: { revision?: number },
): boolean {
  if (rendered === null || incoming.revision === undefined) return true;
  return incoming.revision >= rendered.revision;
}

/** Lock controls during the gap between wiring the page and its first status. */
export function disableCommandsUntilStatus(controls: CommandControlCollection): void {
  controls.forEach((control) => {
    control.disabled = true;
  });
}
