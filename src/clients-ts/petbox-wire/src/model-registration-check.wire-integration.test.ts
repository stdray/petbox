// Integration test: `apply` actually prints the model-registration-check.ts warnings on a real
// run, for the exact live-incident roles.json shape task wire-support-codex-qwen's brief
// describes (three profiles; two hold pre-revision qwen ids the 2026-09-08 seed change no longer
// registers). Unit coverage of the check function itself lives in
// model-registration-check.test.ts — this file only proves the wiring: `apply` (which
// seedDefaultRoleBindingsIfMissing runs on, same as full `wire`'s step 11 — see that function's
// call sites in wire.ts) surfaces the warnings on stderr, non-blocking (exit stays 0).
//
// wire.ts runs main() at import time (see its own file header), so the only way to exercise
// `apply`'s real argv/behavior is a subprocess with a redirected HOME — same technique
// apply-unbound-refusal.test.ts already uses.
//
// Run: node --test src/model-registration-check.wire-integration.test.ts

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, realpathSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { makeGitWorkingTree } from "./test-git-tree.ts";

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

test("apply on the live-incident roles.json shape: warns loudly for every stale qwen binding, names the remedy, and still exits 0 (warn-only, never blocks)", () => {
  const homeDir = freshDir("petbox-model-reg-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-model-reg-proj-"));

  const petboxDir = join(homeDir, ".petbox");
  mkdirSync(petboxDir, { recursive: true });
  // Exactly the brief's observed shape: the active profile already carries the CURRENT seed
  // (rebound by hand), the other two still carry the pre-2026-09-08 bare qwen ids.
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

  const { out, status } = runApply(projectDir, homeDir);

  assert.equal(status, 0, `apply must still exit 0 — warn-only, never blocks. Output:\n${out}`);
  assert.match(out, /model registration:/, `expected a model-registration warning. Output:\n${out}`);
  assert.match(out, /profile 'opencode-go-max'/);
  assert.match(out, /profile 'opencode-direct'/);
  assert.match(out, /harness 'qwen'/);
  assert.match(out, /silently falls back to the FIRST/);
  assert.match(
    out,
    /petbox-wire model set orchestrator <id> --agent qwen --profile opencode-go-max/,
  );
  // The active profile's own (correctly-rebound) qwen bindings must NOT be flagged.
  assert.doesNotMatch(out, /profile 'default' harness 'qwen'/);
});
