// Unit tests for the retired-prompt-RAG hook MIGRATION (hook-prune.ts).
//
// What this pins: prompt-RAG was removed from the kit, but a machine that ever ran
// `petbox-wire --prompt-rag` still has the hook command written into ~/.claude/settings.json and
// ~/.factory/settings.json, pointing at a prompt-rag.ts the kit no longer ships. If wire only
// deleted the kit files, that hook would fail on EVERY prompt. So wire prunes it unconditionally,
// idempotently, and without disturbing any other hook — exactly the four properties below.
//
// The logic lives in its own module precisely so it can be tested: wire.ts runs main() at import
// time and can never be imported by a test (same reason wire-exit.ts / posix-env.ts exist).
//
// Run: node --test src/hook-prune.test.ts   (Node >= 23.6 native TS type-stripping)

import assert from "node:assert/strict";
import { test } from "node:test";
import {
  hasDeadPromptRagHook,
  hasHookTargeting,
  LEGACY_PROMPT_RAG_FILE,
  pruneDeadPromptRagHooks,
  pruneHooksTargeting,
} from "./hook-prune.ts";

// The two commands older kits actually wrote (CC plain; Droid with the --agent droid suffix).
const CC_RAG_CMD = 'node "C:\\Users\\x\\.petbox\\wire\\prompt-rag.ts"';
const DROID_RAG_CMD = 'node "C:\\Users\\x\\.petbox\\wire\\prompt-rag.ts" --agent droid';
const PUSH_CMD = 'node "C:\\Users\\x\\.petbox\\wire\\push-session.ts"';
const PULL_CMD = 'node "C:\\Users\\x\\.petbox\\wire\\pull-memory.ts"';
const FOREIGN_CMD = 'node "C:\\Users\\x\\my-own-hooks\\lint.ts"';

const cmd = (command: string) => ({ type: "command", command });

// A realistic ~/.claude/settings.json `hooks` object from a machine that opted into prompt-RAG.
function claudeHooksWithRag(): any {
  return {
    Stop: [{ hooks: [cmd(PUSH_CMD)] }],
    SessionStart: [{ hooks: [cmd(PULL_CMD)] }],
    UserPromptSubmit: [{ hooks: [cmd(CC_RAG_CMD)] }],
  };
}

// ---- prune when present ----------------------------------------------------------------------

test("prune removes the dead prompt-rag hook from a Claude settings hooks object", () => {
  const hooks = claudeHooksWithRag();
  assert.equal(hasDeadPromptRagHook(hooks), true);

  const pruned = pruneDeadPromptRagHooks(hooks);

  assert.equal(pruned, 1);
  assert.equal(hasDeadPromptRagHook(hooks), false);
  // The now-empty event key is gone entirely (no `"UserPromptSubmit": []` litter).
  assert.equal("UserPromptSubmit" in hooks, false);
});

test("prune catches the droid variant (`--agent droid` suffix) — matching is on the quoted basename", () => {
  const hooks = { UserPromptSubmit: [{ hooks: [cmd(DROID_RAG_CMD)] }] };
  assert.equal(hasDeadPromptRagHook(hooks), true);
  assert.equal(pruneDeadPromptRagHooks(hooks), 1);
  assert.equal(hasDeadPromptRagHook(hooks), false);
});

test("prune removes BOTH variants and every duplicate, wherever they sit", () => {
  const hooks = {
    UserPromptSubmit: [
      { hooks: [cmd(CC_RAG_CMD), cmd(FOREIGN_CMD)] },
      { hooks: [cmd(DROID_RAG_CMD)] },
      { hooks: [cmd(CC_RAG_CMD)] }, // a duplicate group from a re-wire on an older kit
    ],
  };
  assert.equal(pruneDeadPromptRagHooks(hooks), 3);
  // The user's own hook in the shared group survives; the groups that held ONLY rag hooks are gone.
  assert.deepEqual(hooks.UserPromptSubmit, [{ hooks: [cmd(FOREIGN_CMD)] }]);
});

// ---- no-op when absent -----------------------------------------------------------------------

test("no-op when no prompt-rag hook is present: nothing removed, object untouched", () => {
  const hooks = {
    Stop: [{ hooks: [cmd(PUSH_CMD)] }],
    SessionStart: [{ hooks: [cmd(PULL_CMD)] }],
  };
  const before = JSON.stringify(hooks);

  assert.equal(hasDeadPromptRagHook(hooks), false);
  assert.equal(pruneDeadPromptRagHooks(hooks), 0);
  assert.equal(JSON.stringify(hooks), before);
});

test("no-op on a settings file with no hooks at all (missing / junk hooks value)", () => {
  assert.equal(pruneDeadPromptRagHooks(undefined), 0);
  assert.equal(pruneDeadPromptRagHooks(null), 0);
  assert.equal(pruneDeadPromptRagHooks("nonsense"), 0);
  assert.equal(pruneDeadPromptRagHooks({}), 0);
  assert.equal(hasDeadPromptRagHook(undefined), false);
  assert.equal(hasDeadPromptRagHook({}), false);
});

// ---- idempotence -----------------------------------------------------------------------------

test("double run is a byte-identical no-op (idempotent: the second wire changes nothing)", () => {
  const hooks = claudeHooksWithRag();

  assert.equal(pruneDeadPromptRagHooks(hooks), 1);
  const afterFirst = JSON.stringify(hooks);

  // Second `wire` run on the same machine: nothing left to prune → nothing rewritten.
  assert.equal(pruneDeadPromptRagHooks(hooks), 0);
  assert.equal(JSON.stringify(hooks), afterFirst);

  // …and a third, for good measure.
  assert.equal(pruneDeadPromptRagHooks(hooks), 0);
  assert.equal(JSON.stringify(hooks), afterFirst);
});

// ---- leaves everything else alone ------------------------------------------------------------

test("leaves other hooks alone — kit hooks, foreign hooks, and unrelated events all survive", () => {
  const hooks: any = {
    Stop: [{ hooks: [cmd(PUSH_CMD)] }],
    SessionStart: [{ hooks: [cmd(PULL_CMD)] }],
    UserPromptSubmit: [
      { hooks: [cmd(FOREIGN_CMD)] }, // someone else's UserPromptSubmit hook
      { hooks: [cmd(CC_RAG_CMD)] },
    ],
    PreToolUse: [{ matcher: "Bash", hooks: [cmd(FOREIGN_CMD)] }],
  };

  assert.equal(pruneDeadPromptRagHooks(hooks), 1);

  assert.deepEqual(hooks, {
    Stop: [{ hooks: [cmd(PUSH_CMD)] }],
    SessionStart: [{ hooks: [cmd(PULL_CMD)] }],
    UserPromptSubmit: [{ hooks: [cmd(FOREIGN_CMD)] }], // event kept — it still has a real hook
    PreToolUse: [{ matcher: "Bash", hooks: [cmd(FOREIGN_CMD)] }], // matcher preserved
  });
});

test("leaves shapes it does not own exactly as found (non-array event, pre-existing empty group)", () => {
  const hooks: any = {
    Weird: "not-an-array",
    SessionStart: [{ hooks: [] }, { hooks: [cmd(PULL_CMD)] }], // an empty group we did NOT create
    UserPromptSubmit: [{ hooks: [cmd(CC_RAG_CMD)] }],
  };

  assert.equal(pruneDeadPromptRagHooks(hooks), 1);

  assert.deepEqual(hooks, {
    Weird: "not-an-array",
    SessionStart: [{ hooks: [] }, { hooks: [cmd(PULL_CMD)] }],
  });
});

// ---- the generic primitive (kept from the deleted prompt-rag-hook.ts) -------------------------

test("pruneHooksTargeting / hasHookTargeting match on the QUOTED basename of any kit file", () => {
  assert.equal(LEGACY_PROMPT_RAG_FILE, "prompt-rag.ts");

  const hooks = { Stop: [{ hooks: [cmd(PUSH_CMD)] }] };
  assert.equal(hasHookTargeting(hooks, "push-session.ts"), true);
  assert.equal(hasHookTargeting(hooks, "pull-memory.ts"), false); // a different kit file
  assert.equal(hasHookTargeting(hooks, "push-session"), false); // the CLOSING QUOTE is part of the needle
  assert.equal(pruneHooksTargeting(hooks, "push-session.ts"), 1);
  assert.deepEqual(hooks, {});
});
