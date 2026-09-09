// Unit tests for model-registration-check.ts — the warn-only gap closer for task
// wire-support-codex-qwen's live-run finding: seedMissingRoleBindings never rewrites an existing
// binding, so a non-active profile can hold a model id the kit stopped registering (roles.ts's
// CODEX_ROLE_MODEL_SEED/QWEN_ROLE_MODEL_SEED "REVISED 2026-09-08" seed change) forever, invisibly.
//
// REVISED (task qwen-model-registration-check-live-source, 09.09.2026): the qwen half now reads
// the LIVE `$QWEN_HOME/settings.json` → `modelProviders` (via qwen-live-providers.ts, the same
// leaf model-validity.ts's gate reads) instead of the kit's hardcoded qwenRegisteredModelIds() —
// see this file's own header for why two sources of truth about the same fact was the defect.
// Every qwen test below injects its own `homeDir` (a fresh temp dir) so this suite is hermetic:
// it must pass identically on a CI box with no `~/.qwen` at all and on the owner's real machine.
//
// Run: node --test src/model-registration-check.test.ts   (Node >= 23.6 native TS type-stripping)

import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { findUnregisteredRoleBindings } from "./model-registration-check.ts";
import { makeRoleBinding, ROLES_FORMAT_VERSION, type RoleBinding, type RolesFile } from "./roles.ts";

function rolesFile(profiles: RolesFile["profiles"], activeProfile = "default"): RolesFile {
  return { formatVersion: ROLES_FORMAT_VERSION, activeProfile, profiles };
}

function tempHome(): string {
  return mkdtempSync(join(tmpdir(), "petbox-model-registration-check-"));
}

/** Write a live `$QWEN_HOME/settings.json` under `homeDir` with the given bare ids split across
 * the kit's two provider keys — enough shape for readLiveQwenProviders to parse. */
function writeQwenSettings(homeDir: string, deepseekIds: readonly string[], opencodeGoIds: readonly string[]): void {
  const dir = join(homeDir, ".qwen");
  mkdirSync(dir, { recursive: true });
  writeFileSync(
    join(dir, "settings.json"),
    JSON.stringify({
      modelProviders: {
        deepseek: deepseekIds.map((id) => ({ id })),
        "opencode-go": opencodeGoIds.map((id) => ({ id })),
      },
    }),
    "utf8",
  );
}

// The kit's own seed ids (roles.ts's QWEN_ROLE_MODEL_SEED / qwen-model-registry.ts) — used below
// as a stand-in "this machine's live registration", not read from either module: the point of
// this suite is that the check no longer cares whether an id is in the kit's hardcoded catalog at
// all, only whether it is in the (injected) live file.
const DEEPSEEK_IDS = ["ds-deepseek-v4-pro", "ds-deepseek-v4-flash"];
const OPENCODE_GO_IDS = ["go-glm-5.3-flash", "go-qwen3.8-max"];

test("qwen: a binding on an id the live settings.json registers (authType:id form) produces no warning", () => {
  const home = tempHome();
  writeQwenSettings(home, DEEPSEEK_IDS, OPENCODE_GO_IDS);
  const data = rolesFile({
    p1: { agents: { qwen: { roles: { orchestrator: makeRoleBinding("qwen", "openai:ds-deepseek-v4-pro") } } } },
  });
  assert.deepEqual(findUnregisteredRoleBindings(data, { homeDir: home }), []);
});

test("qwen: an id the owner registered live but the kit's own hardcoded catalog never knew (the card's exact scenario — ds-deepseek-v4-pro-max / go-glm-5.3-flash-low) produces no warning", () => {
  const home = tempHome();
  writeQwenSettings(
    home,
    [...DEEPSEEK_IDS, "ds-deepseek-v4-pro-max"],
    [...OPENCODE_GO_IDS, "go-glm-5.3-flash-low"],
  );
  const data = rolesFile({
    p1: {
      agents: {
        qwen: {
          roles: {
            orchestrator: makeRoleBinding("qwen", "openai:ds-deepseek-v4-pro-max"),
            worker: makeRoleBinding("qwen", "openai:go-glm-5.3-flash-low"),
          },
        },
      },
    },
  });
  assert.deepEqual(findUnregisteredRoleBindings(data, { homeDir: home }), []);
});

test("qwen: a binding on an id NOT in the live settings.json produces exactly one warning naming profile/harness/role/id, phrased as 'not registered' (not 'could not be checked')", () => {
  const home = tempHome();
  writeQwenSettings(home, DEEPSEEK_IDS, OPENCODE_GO_IDS);
  const data = rolesFile({
    "opencode-go-max": {
      agents: {
        qwen: {
          roles: {
            orchestrator: makeRoleBinding("qwen", "openai:deepseek-v4-pro"), // bare id, not ds-* — not live-registered
          },
        },
      },
    },
  });
  const warnings = findUnregisteredRoleBindings(data, { homeDir: home });
  assert.equal(warnings.length, 1);
  const w = warnings[0]!;
  assert.match(w, /profile 'opencode-go-max'/);
  assert.match(w, /harness 'qwen'/);
  assert.match(w, /role 'orchestrator'/);
  assert.match(w, /'openai:deepseek-v4-pro'/);
  // Names the exact remedy command, agent-flagged and profile-flagged.
  assert.match(w, /petbox-wire model set orchestrator <id> --agent qwen --profile opencode-go-max/);
  // States the qwen-specific silent-fallback consequence — the fact that makes this urgent.
  assert.match(w, /silently falls back to the FIRST/);
  assert.doesNotMatch(w, /could not be checked/);
});

test("qwen: the live-incident scenario — three stale bindings across two non-active profiles — produces exactly three warnings, one per role", () => {
  const home = tempHome();
  writeQwenSettings(home, DEEPSEEK_IDS, OPENCODE_GO_IDS);
  const data = rolesFile(
    {
      default: {
        agents: { qwen: { roles: { orchestrator: makeRoleBinding("qwen", "openai:ds-deepseek-v4-pro") } } },
      },
      "opencode-go-max": {
        agents: {
          qwen: {
            roles: {
              orchestrator: makeRoleBinding("qwen", "openai:deepseek-v4-pro"),
              worker: makeRoleBinding("qwen", "openai:glm-5.3-flash"),
              reserve: makeRoleBinding("qwen", "openai:qwen3.8-max"),
            },
          },
        },
      },
      "opencode-direct": {
        agents: {
          qwen: {
            roles: {
              orchestrator: makeRoleBinding("qwen", "openai:deepseek-v4-pro"),
              worker: makeRoleBinding("qwen", "openai:glm-5.3-flash"),
              reserve: makeRoleBinding("qwen", "openai:qwen3.8-max"),
            },
          },
        },
      },
    },
    "default",
  );
  const warnings = findUnregisteredRoleBindings(data, { homeDir: home });
  assert.equal(warnings.length, 6); // 3 roles x 2 stale profiles; the active "default" profile is clean
  for (const w of warnings) assert.match(w, /silently falls back to the FIRST/);
});

test("qwen: registered ids come from the live settings.json — a binding on every id it registers never warns", () => {
  const home = tempHome();
  writeQwenSettings(home, DEEPSEEK_IDS, OPENCODE_GO_IDS);
  const ids = [...DEEPSEEK_IDS, ...OPENCODE_GO_IDS];
  const roles: Record<string, RoleBinding> = {};
  ids.forEach((id, i) => {
    roles[`role-${i}`] = makeRoleBinding("qwen", `openai:${id}`);
  });
  const data = rolesFile({ p: { agents: { qwen: { roles } } } });
  assert.deepEqual(findUnregisteredRoleBindings(data, { homeDir: home }), []);
});

test("qwen: blank and 'inherit' bindings are not concrete ids — never checked, never warned, even with no live settings.json at all", () => {
  const data = rolesFile({
    p: {
      agents: {
        qwen: {
          roles: {
            orchestrator: makeRoleBinding("qwen", "inherit"),
            worker: makeRoleBinding("qwen", "   "),
          },
        },
      },
    },
  });
  assert.deepEqual(findUnregisteredRoleBindings(data, { homeDir: tempHome() }), []);
});

// ---- qwen: the THIRD outcome — the live file could not be consulted at all ---------------------
// Mirrors model-validity.ts's own "THREE OUTCOMES, NEVER TWO" rule: an unreadable/missing/empty
// settings.json must never be reported the same way as "consulted, and this id is absent".

test("qwen: no settings.json at all produces a 'could not be checked' warning, never 'not registered'", () => {
  const home = tempHome(); // no .qwen dir written
  const data = rolesFile({
    p: { agents: { qwen: { roles: { orchestrator: makeRoleBinding("qwen", "openai:ds-deepseek-v4-pro") } } } },
  });
  const warnings = findUnregisteredRoleBindings(data, { homeDir: home });
  assert.equal(warnings.length, 1);
  const w = warnings[0]!;
  assert.match(w, /profile 'p'/);
  assert.match(w, /role 'orchestrator'/);
  assert.match(w, /could not be checked/);
  assert.match(w, /is NOT a claim that .* is unregistered/);
  assert.doesNotMatch(w, /does not currently register/);
  assert.doesNotMatch(w, /silently falls back to the FIRST/); // that's the "invalid" wording, not this one
});

test("qwen: an unparseable settings.json also produces 'could not be checked', not 'not registered'", () => {
  const home = tempHome();
  mkdirSync(join(home, ".qwen"), { recursive: true });
  writeFileSync(join(home, ".qwen", "settings.json"), "{ not json", "utf8");
  const data = rolesFile({
    p: { agents: { qwen: { roles: { orchestrator: makeRoleBinding("qwen", "openai:ds-deepseek-v4-pro") } } } },
  });
  const warnings = findUnregisteredRoleBindings(data, { homeDir: home });
  assert.equal(warnings.length, 1);
  assert.match(warnings[0]!, /could not be checked/);
});

test("qwen: an empty modelProviders is also the 'could not be checked' outcome, not 'not registered'", () => {
  const home = tempHome();
  writeQwenSettings(home, [], []);
  const data = rolesFile({
    p: { agents: { qwen: { roles: { orchestrator: makeRoleBinding("qwen", "openai:ds-deepseek-v4-pro") } } } },
  });
  const warnings = findUnregisteredRoleBindings(data, { homeDir: home });
  assert.equal(warnings.length, 1);
  assert.match(warnings[0]!, /could not be checked/);
});

// ---- codex: unchanged, structurally inert (this task's brief explicitly leaves it dead) --------

test("codex: a binding on a currently-registered slug produces no warning", () => {
  const data = rolesFile({
    p1: { agents: { codex: { roles: { orchestrator: makeRoleBinding("codex", "deepseek-v4-pro") } } } },
  });
  assert.deepEqual(findUnregisteredRoleBindings(data), []);
});

test("codex: the catalog is the union of EVERY profile's bindings, so a slug that looks stale (grok-4.6, the live-incident id) still resolves and never warns — this is the documented 'milder' case, not a bug in this check", () => {
  const data = rolesFile({
    default: {
      agents: { codex: { roles: { orchestrator: makeRoleBinding("codex", "deepseek-v4-pro") } } },
    },
    "opencode-go-max": {
      agents: { codex: { roles: { reserve: makeRoleBinding("codex", "grok-4.6") } } },
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
        "claude-code": { roles: { orchestrator: makeRoleBinding("claude-code", "totally-not-a-real-model") } },
        droid: { roles: { orchestrator: makeRoleBinding("droid", "custom:DeepSeek-V4-Pro-0") } },
        opencode: { roles: { orchestrator: makeRoleBinding("opencode", "anthropic/claude-opus-4-8") } },
      },
    },
  });
  assert.deepEqual(findUnregisteredRoleBindings(data, { homeDir: tempHome() }), []);
});

test("mixed profile: only the harness/role actually mismatched is warned about, siblings are silent", () => {
  const home = tempHome();
  writeQwenSettings(home, DEEPSEEK_IDS, OPENCODE_GO_IDS);
  const data = rolesFile({
    p: {
      agents: {
        qwen: {
          roles: {
            orchestrator: makeRoleBinding("qwen", "openai:ds-deepseek-v4-pro"), // valid
            reserve: makeRoleBinding("qwen", "openai:qwen3.9-max"), // typo'd / stale, invalid
          },
        },
      },
    },
  });
  const warnings = findUnregisteredRoleBindings(data, { homeDir: home });
  assert.equal(warnings.length, 1);
  assert.match(warnings[0]!, /role 'reserve'/);
  assert.doesNotMatch(warnings[0]!, /role 'orchestrator'/);
});
