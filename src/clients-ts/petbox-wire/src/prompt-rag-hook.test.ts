// Unit tests for the GLOBAL prompt-RAG hook's install-time policy (prompt-rag-hook.ts).
//
// The bug this pins: the toggle is a sticky TRI-STATE, but the installer only saw a boolean
// (`args.promptRag === true`), so "neither flag passed" collapsed into "off" and EVERY plain
// re-wire pruned the UserPromptSubmit hook while the project's registry flag stayed `true`.
//
// The logic lives in its own module precisely so it can be tested: wire.ts runs main() at import
// time and can never be imported by a test (same reason wire-exit.ts / posix-env.ts exist).
//
// Run: node --test src/prompt-rag-hook.test.ts   (Node >= 23.6 native TS type-stripping)

import assert from "node:assert/strict";
import { test } from "node:test";
import {
  anyProjectPromptRagEnabled,
  applyPromptRagHook,
  checkPromptRagWiring,
  decidePromptRagHook,
  hasHookTargeting,
  PROMPT_RAG_FILE,
  pruneHooksTargeting,
} from "./prompt-rag-hook.ts";
import type { RegistryEntry } from "./registry.ts";

const CC_CMD = 'node "C:\\Users\\x\\.petbox\\wire\\prompt-rag.ts"';
const DROID_CMD = 'node "C:\\Users\\x\\.petbox\\wire\\prompt-rag.ts" --agent droid';
const PUSH_CMD = 'node "C:\\Users\\x\\.petbox\\wire\\push-session.ts"';

// A settings.hooks object with the kit's ordinary hooks + (optionally) the prompt-rag hook.
function hooksWith(promptRag: boolean): any {
  const h: any = { Stop: [{ hooks: [{ type: "command", command: PUSH_CMD }] }] };
  if (promptRag) h.UserPromptSubmit = [{ hooks: [{ type: "command", command: CC_CMD }] }];
  return h;
}

const entry = (project: string, enabled?: boolean): RegistryEntry => ({
  prefix: `D:/prj/${project}`,
  project,
  envVar: `PETBOX_${project.toUpperCase()}_API_KEY`,
  ...(enabled === undefined ? {} : { promptRag: { enabled } }),
});

// ---- the decision (the actual bug) ----------------------------------------------------------

test("neither flag → KEEP: the hook is left exactly as found (the sticky bug)", () => {
  // This is the regression: a routine re-wire (`npx petbox-wire <dir> <project>` to refresh an MCP
  // config) used to PRUNE the global hook while the registry flag stayed true.
  const d = decidePromptRagHook({ requested: undefined, kitShipsHook: true, anyProjectEnabled: true });
  assert.equal(d.action, "keep");
  // …and equally when nothing is enabled anywhere: sticky means sticky in BOTH directions.
  const d2 = decidePromptRagHook({ requested: undefined, kitShipsHook: true, anyProjectEnabled: false });
  assert.equal(d2.action, "keep");
});

test("--prompt-rag → INSTALL", () => {
  const d = decidePromptRagHook({ requested: true, kitShipsHook: true, anyProjectEnabled: true });
  assert.equal(d.action, "install");
});

test("--no-prompt-rag → PRUNE only when no project is left enabled (the hook is global)", () => {
  // Last one out turns off the light.
  assert.equal(
    decidePromptRagHook({ requested: false, kitShipsHook: true, anyProjectEnabled: false }).action,
    "prune",
  );
  // But another project still opted in → the global hook stays; it self-gates per project, so
  // ripping it out here would silently disable prompt-RAG for THEM.
  const d = decidePromptRagHook({ requested: false, kitShipsHook: true, anyProjectEnabled: true });
  assert.equal(d.action, "keep");
  assert.match(d.reason, /another registered project/);
});

test("version-skew guard outranks everything: a kit without prompt-rag.ts always prunes", () => {
  for (const requested of [true, false, undefined]) {
    const d = decidePromptRagHook({ requested, kitShipsHook: false, anyProjectEnabled: true });
    assert.equal(d.action, "prune", `requested=${requested}`);
    assert.match(d.reason, /version-skew/);
  }
});

test("anyProjectPromptRagEnabled: true iff some entry has promptRag.enabled === true", () => {
  assert.equal(anyProjectPromptRagEnabled([]), false);
  assert.equal(anyProjectPromptRagEnabled([entry("a"), entry("b", false)]), false);
  assert.equal(anyProjectPromptRagEnabled([entry("a", false), entry("b", true)]), true);
  // Malformed config degrades to "not enabled" (same polarity as registry.resolveProject).
  assert.equal(anyProjectPromptRagEnabled([{ ...entry("a"), promptRag: "yes" } as any]), false);
});

// ---- applying the decision to a settings.hooks object ----------------------------------------

test("apply keep: an INSTALLED hook survives, an ABSENT hook stays absent — byte-identical either way", () => {
  const installed = hooksWith(true);
  const before = JSON.stringify(installed);
  const r1 = applyPromptRagHook(installed, "keep", CC_CMD);
  assert.equal(r1.changed, false);
  assert.equal(JSON.stringify(installed), before);
  assert.ok(hasHookTargeting(installed, PROMPT_RAG_FILE));

  const absent = hooksWith(false);
  const beforeAbsent = JSON.stringify(absent);
  const r2 = applyPromptRagHook(absent, "keep", CC_CMD);
  assert.equal(r2.changed, false);
  assert.equal(JSON.stringify(absent), beforeAbsent);
  assert.equal(hasHookTargeting(absent, PROMPT_RAG_FILE), false);
});

test("apply install: adds the hook, and a second run is a no-op (idempotent)", () => {
  const hooks = hooksWith(false);
  const first = applyPromptRagHook(hooks, "install", CC_CMD);
  assert.equal(first.installed, true);
  assert.ok(hasHookTargeting(hooks, PROMPT_RAG_FILE));
  const afterFirst = JSON.stringify(hooks);

  const second = applyPromptRagHook(hooks, "install", CC_CMD);
  assert.equal(second.installed, false);
  assert.equal(second.changed, false);
  assert.equal(JSON.stringify(hooks), afterFirst, "re-running the same command must not churn settings");
  // Exactly one hook entry, not two.
  assert.equal(hooks.UserPromptSubmit.flatMap((g: any) => g.hooks).length, 1);
});

test("apply prune: removes the hook and empty groups, and a second run is a no-op (idempotent)", () => {
  const hooks = hooksWith(true);
  const first = applyPromptRagHook(hooks, "prune", CC_CMD);
  assert.equal(first.pruned, 1);
  assert.equal(hasHookTargeting(hooks, PROMPT_RAG_FILE), false);
  assert.deepEqual(hooks.UserPromptSubmit, [], "the now-empty group is dropped");
  const afterFirst = JSON.stringify(hooks);

  const second = applyPromptRagHook(hooks, "prune", CC_CMD);
  assert.equal(second.pruned, 0);
  assert.equal(second.changed, false);
  assert.equal(JSON.stringify(hooks), afterFirst);
  // Non-prompt-rag kit hooks are never touched.
  assert.equal(hooks.Stop[0].hooks[0].command, PUSH_CMD);
});

test("prune matches BOTH agent variants (the droid command carries a --agent suffix)", () => {
  const hooks: any = {
    UserPromptSubmit: [
      { hooks: [{ type: "command", command: CC_CMD }] },
      { hooks: [{ type: "command", command: DROID_CMD }] },
      { hooks: [{ type: "command", command: 'node "other-tool.ts"' }] },
    ],
  };
  assert.equal(pruneHooksTargeting(hooks, PROMPT_RAG_FILE), 2);
  assert.equal(hooks.UserPromptSubmit.length, 1);
  assert.equal(hooks.UserPromptSubmit[0].hooks[0].command, 'node "other-tool.ts"');
});

// ---- end-to-end of the decision + apply (what a re-wire actually does) ------------------------

test("double-run idempotence: a plain re-wire after --prompt-rag leaves settings byte-identical", () => {
  const hooks = hooksWith(false);
  const registry = [entry("petbox", true)];

  // Run 1: `wire … --prompt-rag`
  const a1 = decidePromptRagHook({
    requested: true,
    kitShipsHook: true,
    anyProjectEnabled: anyProjectPromptRagEnabled(registry),
  });
  applyPromptRagHook(hooks, a1.action, CC_CMD);
  const snapshot = JSON.stringify(hooks);

  // Run 2 + 3: plain re-wires (no flag) — the hook must SURVIVE, unchanged.
  for (const _ of [1, 2]) {
    const a = decidePromptRagHook({
      requested: undefined,
      kitShipsHook: true,
      anyProjectEnabled: anyProjectPromptRagEnabled(registry),
    });
    const r = applyPromptRagHook(hooks, a.action, CC_CMD);
    assert.equal(r.changed, false);
  }
  assert.equal(JSON.stringify(hooks), snapshot);
  assert.ok(hasHookTargeting(hooks, PROMPT_RAG_FILE), "a re-wire must not silently remove prompt-RAG");

  // Run 4: `--no-prompt-rag` on the only enabled project (registry now off) → gone, and staying gone.
  const off = [entry("petbox", false)];
  const a4 = decidePromptRagHook({
    requested: false,
    kitShipsHook: true,
    anyProjectEnabled: anyProjectPromptRagEnabled(off),
  });
  assert.equal(a4.action, "prune");
  applyPromptRagHook(hooks, a4.action, CC_CMD);
  assert.equal(hasHookTargeting(hooks, PROMPT_RAG_FILE), false);
  const gone = JSON.stringify(hooks);
  applyPromptRagHook(hooks, a4.action, CC_CMD);
  assert.equal(JSON.stringify(hooks), gone);
});

// ---- doctor's flag/hook mismatch --------------------------------------------------------------

test("checkPromptRagWiring: flag on + no hook anywhere → the mismatch the bug produced", () => {
  const m = checkPromptRagWiring({
    entries: [entry("petbox", true), entry("kpvotes", false)],
    claudeHooks: hooksWith(false),
    droidHooks: {},
  });
  assert.equal(m.length, 1);
  assert.equal(m[0].kind, "flag-on-hook-missing");
  assert.match(m[0].message, /petbox/);
});

test("checkPromptRagWiring: consistent states report nothing", () => {
  // flag on + hook present
  assert.deepEqual(
    checkPromptRagWiring({ entries: [entry("petbox", true)], claudeHooks: hooksWith(true), droidHooks: {} }),
    [],
  );
  // nothing enabled + no hook
  assert.deepEqual(
    checkPromptRagWiring({ entries: [entry("petbox", false)], claudeHooks: hooksWith(false), droidHooks: {} }),
    [],
  );
});

test("checkPromptRagWiring: hook installed with no enabled project is only advisory (harmless)", () => {
  const m = checkPromptRagWiring({
    entries: [entry("petbox", false)],
    claudeHooks: hooksWith(true),
    droidHooks: {},
  });
  assert.equal(m.length, 1);
  assert.equal(m[0].kind, "hook-installed-no-project");
  assert.match(m[0].message, /no-op/);
});
