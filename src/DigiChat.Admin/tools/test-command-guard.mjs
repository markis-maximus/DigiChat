import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import ts from "typescript";

const here = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(here, "..", "src", "command-guard.ts"), "utf8");
const compiled = ts.transpileModule(source, {
  compilerOptions: {
    module: ts.ModuleKind.ESNext,
    target: ts.ScriptTarget.ES2022,
  },
  reportDiagnostics: true,
});

const errors = (compiled.diagnostics ?? []).filter(
  (diagnostic) => diagnostic.category === ts.DiagnosticCategory.Error,
);
assert.equal(errors.length, 0, "command guard must transpile without diagnostics");

const encoded = Buffer.from(compiled.outputText).toString("base64");
const guard = await import(`data:text/javascript;base64,${encoded}`);

assert.equal(guard.canStartBrowserCommand(null, false), false, "pre-status command");
assert.equal(guard.canStartBrowserCommand({ sessionNumber: 1 }, true), false, "latched command");
assert.equal(guard.canStartBrowserCommand({ sessionNumber: 1 }, false), true, "ready command");

const controls = [{ disabled: false }, { disabled: false }, { disabled: true }];
guard.disableCommandsUntilStatus(controls);
assert.deepEqual(
  controls.map((control) => control.disabled),
  [true, true, true],
  "all command controls must start disabled",
);

// Snapshot ordering: a delayed older push must never overwrite a newer pull.
assert.equal(guard.isCurrentSnapshot(null, { revision: 5 }), true, "first snapshot always renders");
assert.equal(guard.isCurrentSnapshot({ revision: 5 }, { revision: 6 }), true, "newer push renders");
assert.equal(guard.isCurrentSnapshot({ revision: 5 }, { revision: 5 }), true, "same revision re-renders");
assert.equal(guard.isCurrentSnapshot({ revision: 6 }, { revision: 5 }), false, "stale push is dropped");
assert.equal(guard.isCurrentSnapshot({ revision: 6 }, {}), true, "unversioned backend still renders");

console.log("Admin pre-status command guard passed.");
console.log("Admin snapshot ordering guard passed.");
