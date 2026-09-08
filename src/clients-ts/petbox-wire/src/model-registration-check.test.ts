// Unit tests for model-registration-check.ts — the warn-only gap closer for task
// wire-support-codex-qwen's live-run finding: seedMissingRoleBindings never rewrites an existing
// binding, so a non-active profile can hold a model id the kit stopped registering (roles.ts's
// CODEX_ROLE_MODEL_SEED/QWEN_ROLE_MODEL_SEED "REVISED 2026-09-08" seed change) forever, invisibly.
//
// Run: node --test src/model-registration-check.test.ts   (Node >= 23.6 native TS type-stripping)

import assert from "node:assert/strict";
import { test } from "node:test";
import { findUnregisteredRoleBindings } from "./model-registration-check.ts";
import { qwenRegisteredModelIds } from "./qwen-model-catalog.ts";
import type { RolesFile } from "./roles.ts";

function rolesFile(profiles: RolesFile["profiles"], activeProfile = "default"): RolesFile {
  return { activeProfile, profiles };
}

test("qwen: a binding on a currently-registered id (authType:id form) produces no warning", () => {
  const data = rolesFile({
    p1: { agents: { qwen: { roles: { orchestrator: { model: "openai:ds-deepseek-v4-pro" } } } } },
  });
  assert.deepEqual(findUnregisteredRoleBindings(data), []);
});

test("qwen: a binding on an id the kit no longer registers (live-incident shape — bare pre-revision id) produces exactly one warning naming profile/harness/role/id", () => {
  const data = rolesFile({
    "opencode-go-max": {
      agents: {
        qwen: {
          roles: {
            orchestrator: { model: "openai:deepseek-v4-pro" }, // pre-revision bare id, not ds-*
          },
        },
      },
    },
  });
  const warnings = findUnregisteredRoleBindings(data);
  assert.equal(warnings.length, 1);
  const w = warnings[0]!;
  assert.match(w, /profile 'opencode-go-max'/);
  assert.match(w, /harness 'qwen'/);
  assert.match(w, /role 'orchestrator'/);
  assert.match(w, /'openai:deepseek-v4-pro'/);
  // Names the exact remedy command, agent-flagged and profile-flagged.
  assert.match(
    w,
    /petbox-wire model set orchestrator <id> --agent qwen --profile opencode-go-max/,
  );
  // States the qwen-specific silent-fallback consequence — the fact that makes this urgent.
  assert.match(w, /silently falls back to the FIRST/);
});

test("qwen: the live-incident scenario — three stale bindings across two non-active profiles — produces exactly three warnings, one per role", () => {
  const data = rolesFile(
    {
      default: {
        agents: { qwen: { roles: { orchestrator: { model: "openai:ds-deepseek-v4-pro" } } } },
      },
      "opencode-go-max": {
        agents: {
          qwen: {
            roles: {
              orchestrator: { model: "openai:deepseek-v4-pro" },
              worker: { model: "openai:glm-5.3-flash" },
              reserve: { model: "openai:qwen3.8-max" },
            },
          },
        },
      },
      "opencode-direct": {
        agents: {
          qwen: {
            roles: {
              orchestrator: { model: "openai:deepseek-v4-pro" },
              worker: { model: "openai:glm-5.3-flash" },
              reserve: { model: "openai:qwen3.8-max" },
            },
          },
        },
      },
    },
    "default",
  );
  const warnings = findUnregisteredRoleBindings(data);
  assert.equal(warnings.length, 6); // 3 roles x 2 stale profiles; the active "default" profile is clean
  for (const w of warnings) assert.match(w, /silently falls back to the FIRST/);
});

test("qwen: registered ids come from qwenRegisteredModelIds() — a binding on every id that function returns never warns", () => {
  const ids = qwenRegisteredModelIds();
  assert.ok(ids.length > 0);
  const roles: Record<string, { model: string }> = {};
  ids.forEach((id, i) => {
    roles[`role-${i}`] = { model: `openai:${id}` };
  });
  const data = rolesFile({ p: { agents: { qwen: { roles } } } });
  assert.deepEqual(findUnregisteredRoleBindings(data), []);
});

test("qwen: blank and 'inherit' bindings are not concrete ids — never checked, never warned", () => {
  const data = rolesFile({
    p: {
      agents: {
        qwen: {
          roles: {
            orchestrator: { model: "inherit" },
            worker: { model: "   " },
          },
        },
      },
    },
  });
  assert.deepEqual(findUnregisteredRoleBindings(data), []);
});

test("codex: a binding on a currently-registered slug produces no warning", () => {
  const data = rolesFile({
    p1: { agents: { codex: { roles: { orchestrator: { model: "deepseek-v4-pro" } } } } },
  });
  assert.deepEqual(findUnregisteredRoleBindings(data), []);
});

test("codex: the catalog is the union of EVERY profile's bindings, so a slug that looks stale (grok-4.6, the live-incident id) still resolves and never warns — this is the documented 'milder' case, not a bug in this check", () => {
  const data = rolesFile({
    default: {
      agents: { codex: { roles: { orchestrator: { model: "deepseek-v4-pro" } } } },
    },
    "opencode-go-max": {
      agents: { codex: { roles: { reserve: { model: "grok-4.6" } } } },
    },
  });
  // Sanity: grok-4.6 really is absent from the *current* seed (roles.ts) — this is the exact
  // live-incident shape, not a contrived id.
  assert.deepEqual(findUnregisteredRoleBindings(data), []);
});

test("harnesses outside the checked set (claude-code, droid, opencode) never produce a warning, however unrecognizable the id", () => {
  const data = rolesFile({
    p: {
      agents: {
        "claude-code": { roles: { orchestrator: { model: "totally-not-a-real-model" } } },
        droid: { roles: { orchestrator: { model: "custom:DeepSeek-V4-Pro-0" } } },
        opencode: { roles: { orchestrator: { model: "anthropic/claude-opus-4-8" } } },
      },
    },
  });
  assert.deepEqual(findUnregisteredRoleBindings(data), []);
});

test("mixed profile: only the harness/role actually mismatched is warned about, siblings are silent", () => {
  const data = rolesFile({
    p: {
      agents: {
        qwen: {
          roles: {
            orchestrator: { model: "openai:ds-deepseek-v4-pro" }, // valid
            reserve: { model: "openai:qwen3.9-max" }, // typo'd / stale, invalid
          },
        },
      },
    },
  });
  const warnings = findUnregisteredRoleBindings(data);
  assert.equal(warnings.length, 1);
  assert.match(warnings[0]!, /role 'reserve'/);
  assert.doesNotMatch(warnings[0]!, /role 'orchestrator'/);
});
