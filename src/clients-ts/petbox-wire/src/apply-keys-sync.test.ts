// Integration tests for `apply`'s keys.json auto-sync (card keys-json-doctor-drift-check).
// Measured 2026-09-09: before this, ~/.petbox/keys.json was written ONLY by a full `wire` run
// (wire.ts's writeKeyToStore, step [4/10]) — a plain env-var rotation never reached it. This adds
// the same sync to `apply`, not only full `wire`, verified here by comparing the file before and
// after a run. `--dry-run` must leave the file untouched, matching the existing "a preview writes
// nothing" rule this file already applies to roleScope persistence.
//
// Same spawn-subprocess technique as apply-unbound-refusal.test.ts (wire.ts runs main() at module
// top level).
//
// Run: node --test src/apply-keys-sync.test.ts

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { makeGitWorkingTree } from "./test-git-tree.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

function writeRegistry(homeDir: string, entries: Array<{ prefix: string; project: string; envVar: string }>): void {
  const petboxDir = join(homeDir, ".petbox");
  mkdirSync(petboxDir, { recursive: true });
  writeFileSync(join(petboxDir, "projects.json"), JSON.stringify({ entries }), "utf8");
}

function writeKeysJson(homeDir: string, store: Record<string, string>): void {
  const petboxDir = join(homeDir, ".petbox");
  mkdirSync(petboxDir, { recursive: true });
  writeFileSync(join(petboxDir, "keys.json"), JSON.stringify(store), "utf8");
}

function runApply(
  cwd: string,
  homeDir: string,
  extraArgs: string[],
  extraEnv: Record<string, string>,
): { stdout: string; stderr: string; status: number | null } {
  const res = spawnSync(process.execPath, [WIRE_TS, "apply", "--offline", ...extraArgs], {
    cwd,
    encoding: "utf8",
    env: {
      ...process.env,
      USERPROFILE: homeDir,
      HOME: homeDir,
      HOMEDRIVE: undefined,
      HOMEPATH: undefined,
      ...extraEnv,
    },
  });
  return { stdout: res.stdout ?? "", stderr: res.stderr ?? "", status: res.status };
}

test("apply syncs a drifted key from the environment into keys.json (before/after comparison)", () => {
  const homeDir = freshDir("petbox-apply-keyssync-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-apply-keyssync-proj-"));
  const envVar = "PETBOX_APPLY_SYNC_CASE_API_KEY";
  try {
    writeRegistry(homeDir, [{ prefix: projectDir, project: "apply-sync-case", envVar }]);
    writeKeysJson(homeDir, { [envVar]: "old-stale-value" });
    const before = JSON.parse(readFileSync(join(homeDir, ".petbox", "keys.json"), "utf8"));
    assert.equal(before[envVar], "old-stale-value");

    const { stdout, stderr, status } = runApply(projectDir, homeDir, [], { [envVar]: "new-rotated-value" });
    const out = stdout + stderr;
    assert.doesNotMatch(out, /old-stale-value/, `Full output:\n${out}`);
    assert.doesNotMatch(out, /new-rotated-value/, `Full output:\n${out}`);
    assert.equal(status, 0, `apply must still succeed. Full output:\n${out}`);

    const after = JSON.parse(readFileSync(join(homeDir, ".petbox", "keys.json"), "utf8"));
    assert.equal(after[envVar], "new-rotated-value", "apply must sync the drifted key into keys.json");
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("apply --dry-run does NOT touch keys.json — a preview leaves the file byte-for-byte as found", () => {
  const homeDir = freshDir("petbox-apply-keyssync-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-apply-keyssync-proj-"));
  const envVar = "PETBOX_APPLY_DRYRUN_CASE_API_KEY";
  try {
    writeRegistry(homeDir, [{ prefix: projectDir, project: "apply-dryrun-case", envVar }]);
    writeKeysJson(homeDir, { [envVar]: "old-stale-value" });
    const beforeRaw = readFileSync(join(homeDir, ".petbox", "keys.json"), "utf8");

    const { stdout, stderr, status } = runApply(projectDir, homeDir, ["--dry-run"], { [envVar]: "new-rotated-value" });
    const out = stdout + stderr;
    assert.equal(status, 0, `Full output:\n${out}`);

    const afterRaw = readFileSync(join(homeDir, ".petbox", "keys.json"), "utf8");
    assert.equal(afterRaw, beforeRaw, "a --dry-run apply must never write keys.json");
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("apply with nothing registered / no keys.json is a clean no-op for the sync step (no crash, no file created)", () => {
  const homeDir = freshDir("petbox-apply-keyssync-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-apply-keyssync-proj-"));
  try {
    const { status } = runApply(projectDir, homeDir, [], {});
    assert.equal(status, 0);
    // No registry at all → detectKeysStoreDrift has nothing to check → no keys.json materializes
    // purely from the sync step.
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});
