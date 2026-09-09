// Integration test over the exact live-incident roles.json shape task wire-support-codex-qwen's
// brief describes (three profiles; two hold pre-revision qwen ids the 2026-09-08 seed change no
// longer registers), run end to end through `apply`.
//
// WHAT THIS FILE ASSERTED BEFORE, AND WHY IT CHANGED (task role-model-bindings-review-refactor,
// stage 1): it asserted that `apply` WARNS about every stale binding. That was the best the kit
// could do while it could not tell its own past seed from the operator's choice — warn, and leave
// the operator ten `model set` commands to run. With the binding origin recorded (roles.ts's
// BindingOrigin) the kit can now attribute those exact values to a seed it shipped itself and
// bring them up to its current defaults, so the same file produces ZERO warnings and needs no
// commands at all. That is this stage's headline acceptance criterion, and this is where it is
// proven end to end.
//
// The warning path is NOT dead — it is what still fires for an id the migration could not
// attribute to any historical seed (the operator's own choice, which the kit must never rewrite);
// the second test below covers that, so a regression that silently reverts to warning-instead-of-
// fixing, OR one that starts rewriting owner bindings, fails here.
//
// Unit coverage of the check function itself lives in model-registration-check.test.ts.
//
// wire.ts runs main() at import time (see its own file header), so the only way to exercise
// `apply`'s real argv/behavior is a subprocess with a redirected HOME — same technique
// apply-unbound-refusal.test.ts already uses.
//
// REVISED (task qwen-model-registration-check-live-source, 09.09.2026): the qwen registration
// check now reads the LIVE `$QWEN_HOME/settings.json` instead of the kit's hardcoded catalog (see
// model-registration-check.ts's header) — so a homeDir with NO settings.json at all no longer
// means "everything I bind resolves", it means "nothing can be verified" (the THIRD outcome).
// Both tests below now write a `.qwen/settings.json` fixture into the temp homeDir before running
// `apply`, registering exactly the ids the scenario needs resolved — the live-file equivalent of
// what the hardcoded catalog used to provide implicitly. Without it every concrete qwen binding
// would report "could not be checked" instead of either outcome this file means to exercise.
//
// Run: node --test src/model-registration-check.wire-integration.test.ts

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, realpathSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { makeGitWorkingTree } from "./test-git-tree.ts";

/** Write a live `$QWEN_HOME/settings.json` (bare ids under the `deepseek` provider key — enough
 * shape for readLiveQwenProviders to parse) into a temp homeDir before running `apply`. */
function writeQwenSettings(homeDir: string, deepseekIds: readonly string[]): void {
  const dir = join(homeDir, ".qwen");
  mkdirSync(dir, { recursive: true });
  writeFileSync(
    join(dir, "settings.json"),
    JSON.stringify({ modelProviders: { deepseek: deepseekIds.map((id) => ({ id })) } }),
    "utf8",
  );
}

const WIRE_TS = join(import.meta.dirname, "wire.ts");

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

function runApply(cwd: string, homeDir: string): { out: string; status: number | null } {
  const res = spawnSync(process.execPath, [WIRE_TS, "apply", "--offline"], {
    cwd,
    encoding: "utf8",
    env: {
      ...process.env,
      USERPROFILE: homeDir,
      HOME: homeDir,
      HOMEDRIVE: undefined,
      HOMEPATH: undefined,
    },
  });
  return { out: (res.stdout ?? "") + (res.stderr ?? ""), status: res.status };
}

test("apply on the live-incident roles.json shape: the stale kit-seeded qwen bindings are migrated to the kit's current defaults, so ZERO model-registration warnings survive and no `model set` is needed", () => {
  const homeDir = freshDir("petbox-model-reg-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-model-reg-proj-"));

  const petboxDir = join(homeDir, ".petbox");
  mkdirSync(petboxDir, { recursive: true });
  // Exactly the brief's observed shape, in the v1 format (no formatVersion, no origin/provider):
  // the active profile already carries the CURRENT seed, the other two still carry the
  // pre-2026-09-08 bare qwen ids that only a past version of this kit ever wrote.
  const rolesJson = {
    activeProfile: "default",
    profiles: {
      default: {
        agents: {
          qwen: {
            roles: {
              orchestrator: { model: "openai:ds-deepseek-v4-pro" },
              worker: { model: "openai:ds-deepseek-v4-flash" },
              "worker-highstakes": { model: "openai:ds-deepseek-v4-pro" },
              explore: { model: "openai:ds-deepseek-v4-flash" },
              reserve: { model: "openai:ds-deepseek-v4-pro" },
            },
          },
        },
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
  };
  writeFileSync(join(petboxDir, "roles.json"), JSON.stringify(rolesJson, null, 2), "utf8");
  // Live registration for exactly the ids the migration maps every stale binding onto.
  writeQwenSettings(homeDir, ["ds-deepseek-v4-pro", "ds-deepseek-v4-flash"]);

  const { out, status } = runApply(projectDir, homeDir);

  assert.equal(status, 0, `apply must exit 0. Output:\n${out}`);
  assert.doesNotMatch(
    out,
    /model registration:/,
    "every stale binding here was this kit's own past seed, so the migration must have fixed them " +
      `and left nothing to warn about. Output:\n${out}`,
  );
  // The rewrite is LOUD: an operator must be able to see which cell moved and to what.
  assert.match(out, /migrated .*roles\.json to format v2/);
  assert.match(out, /openai:deepseek-v4-pro -> openai:ds-deepseek-v4-pro/);
  assert.match(out, /openai:glm-5\.3-flash -> openai:ds-deepseek-v4-flash/);
  assert.match(out, /openai:qwen3\.8-max -> openai:ds-deepseek-v4-pro/);

  // On disk: every qwen binding now names a registered id, is labelled as this kit's, and carries
  // the provider that actually serves it.
  const after = JSON.parse(readFileSync(join(petboxDir, "roles.json"), "utf8"));
  assert.equal(after.formatVersion, 2);
  for (const profile of ["opencode-go-max", "opencode-direct"]) {
    const roles = after.profiles[profile].agents.qwen.roles;
    assert.equal(roles.orchestrator.model, "openai:ds-deepseek-v4-pro");
    assert.equal(roles.orchestrator.origin, "kit");
    assert.equal(roles.orchestrator.provider, "deepseek");
    assert.equal(roles.worker.model, "openai:ds-deepseek-v4-flash");
    assert.equal(roles.reserve.model, "openai:ds-deepseek-v4-pro");
  }

  // Idempotent: a second run has nothing left to migrate, changes no byte, and still says nothing
  // is wrong.
  const beforeSecond = readFileSync(join(petboxDir, "roles.json"), "utf8");
  const second = runApply(projectDir, homeDir);
  assert.equal(second.status, 0, `second apply must exit 0. Output:\n${second.out}`);
  assert.doesNotMatch(second.out, /model registration:/);
  assert.doesNotMatch(second.out, /migrated .*roles\.json to format v2/);
  assert.equal(readFileSync(join(petboxDir, "roles.json"), "utf8"), beforeSecond);
});

test("apply still warns — and rewrites nothing — for an unregistered qwen id the OWNER chose: the migration only ever touches values this kit itself shipped", () => {
  const homeDir = freshDir("petbox-model-reg-owner-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-model-reg-owner-proj-"));

  const petboxDir = join(homeDir, ".petbox");
  mkdirSync(petboxDir, { recursive: true });
  // `openai:some-private-model` has never been a seed of this kit for any role, so the migration
  // must attribute it to the owner, leave it byte-for-byte, and let the registration check say
  // (correctly) that the kit does not register it.
  const rolesJson = {
    activeProfile: "default",
    profiles: {
      default: {
        agents: {
          qwen: {
            roles: {
              orchestrator: { model: "openai:some-private-model" },
              worker: { model: "openai:glm-5.3-flash" },
            },
          },
        },
      },
    },
  };
  writeFileSync(join(petboxDir, "roles.json"), JSON.stringify(rolesJson, null, 2), "utf8");
  // Live registration for the kit-migrated id only — 'some-private-model' is deliberately absent,
  // so it is a genuine "not registered" (not a "could not be checked") for the assertion below.
  writeQwenSettings(homeDir, ["ds-deepseek-v4-flash"]);

  const { out, status } = runApply(projectDir, homeDir);

  assert.equal(status, 0, `warn-only, never blocks. Output:\n${out}`);
  assert.match(out, /model registration:/);
  assert.match(out, /profile 'default' harness 'qwen' role 'orchestrator'/);
  assert.match(out, /silently falls back to the FIRST/);
  assert.match(out, /petbox-wire model set orchestrator <id> --agent qwen --profile default/);

  const after = JSON.parse(readFileSync(join(petboxDir, "roles.json"), "utf8"));
  const roles = after.profiles.default.agents.qwen.roles;
  assert.equal(roles.orchestrator.model, "openai:some-private-model");
  assert.equal(roles.orchestrator.origin, "owner");
  // ...while the kit-seeded sibling right next to it WAS updated — the two cases live side by side
  // in one agent block, which is exactly the discrimination this stage adds.
  assert.equal(roles.worker.model, "openai:ds-deepseek-v4-flash");
  assert.equal(roles.worker.origin, "kit");
});
