// Integration tests for doctor's keys.json-vs-environment drift check (card
// keys-json-doctor-drift-check). Before this, `doctor` resolved a key the same env-first way the
// hooks do (registry.ts:152, resolveProject), so a healthy `doctor` proved nothing about whether
// ~/.petbox/keys.json — what a process with NO env var set actually falls back to — was in sync.
// Acceptance (from the card): doctor WARNS by naming the envVar on a machine with drift, and stays
// SILENT about keys.json on a machine without it; values are never printed anywhere; `doctor`
// additionally auto-syncs whatever it found stale, verified here by comparing the file before and
// after.
//
// This check is local-only (registry + keys.json, no network) and runs unconditionally, so these
// tests use `doctor --offline` — same spawn-subprocess technique as doctor-banner-budget.test.ts
// (wire.ts runs main() at module top level).
//
// Run: node --test src/doctor-keys-drift.test.ts

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

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

function runDoctorOffline(
  cwd: string,
  homeDir: string,
  extraEnv: Record<string, string> = {},
): { stdout: string; stderr: string; status: number | null } {
  const res = spawnSync(process.execPath, [WIRE_TS, "doctor", "--offline"], {
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

test("doctor --offline on a machine WITH drift: warns naming the envVar, never prints either value, exit code unaffected, and syncs the file", () => {
  const homeDir = freshDir("petbox-doctor-keysdrift-home-");
  const projectDir = freshDir("petbox-doctor-keysdrift-proj-");
  const envVar = "PETBOX_DOCTOR_DRIFT_CASE_API_KEY";
  try {
    writeRegistry(homeDir, [{ prefix: projectDir, project: "drift-case", envVar }]);
    writeKeysJson(homeDir, { [envVar]: "stale-file-value-zzz" });

    const { stdout, stderr, status } = runDoctorOffline(projectDir, homeDir, { [envVar]: "fresh-env-value-yyy" });
    const out = stdout + stderr;

    assert.match(out, /keys\.json.*out of sync/i, `Full output:\n${out}`);
    assert.match(out, new RegExp(envVar), `Expected the envVar name in the warning. Full output:\n${out}`);
    assert.doesNotMatch(out, /stale-file-value-zzz/, `Full output:\n${out}`);
    assert.doesNotMatch(out, /fresh-env-value-yyy/, `Full output:\n${out}`);
    assert.equal(status, 0, "keys.json drift is informational — must never change doctor's exit code");

    // Auto-sync: the file must now carry the env value (before/after comparison).
    const after = JSON.parse(readFileSync(join(homeDir, ".petbox", "keys.json"), "utf8"));
    assert.equal(after[envVar], "fresh-env-value-yyy", "doctor must auto-sync the drifted key into the file");
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor --offline on a machine WITHOUT drift: stays silent about keys.json, exit 0", () => {
  const homeDir = freshDir("petbox-doctor-keysdrift-home-");
  const projectDir = freshDir("petbox-doctor-keysdrift-proj-");
  const envVar = "PETBOX_DOCTOR_NODRIFT_CASE_API_KEY";
  try {
    writeRegistry(homeDir, [{ prefix: projectDir, project: "nodrift-case", envVar }]);
    writeKeysJson(homeDir, { [envVar]: "same-value-in-both" });

    const before = readFileSync(join(homeDir, ".petbox", "keys.json"), "utf8");
    const { stdout, stderr, status } = runDoctorOffline(projectDir, homeDir, { [envVar]: "same-value-in-both" });
    const out = stdout + stderr;

    assert.doesNotMatch(out, /keys\.json.*out of sync/i, `Expected silence on a synced machine. Full output:\n${out}`);
    assert.doesNotMatch(out, new RegExp(envVar), `Expected no mention of ${envVar} when nothing drifted. Full output:\n${out}`);
    assert.equal(status, 0);

    const after = readFileSync(join(homeDir, ".petbox", "keys.json"), "utf8");
    assert.equal(after, before, "a synced file must be left byte-for-byte untouched");
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("acceptance #5: doctor --offline does NOT lie on a REFERENCE entry — no false drift even though the literal reference text differs from the env value, and the file is left untouched (no bogus auto-sync)", () => {
  const homeDir = freshDir("petbox-doctor-keysdrift-home-");
  const projectDir = freshDir("petbox-doctor-keysdrift-proj-");
  const envVar = "PETBOX_DOCTOR_REFCASE_API_KEY";
  try {
    writeRegistry(homeDir, [{ prefix: projectDir, project: "ref-case", envVar }]);
    // The pre-fix bug: hashing this literal reference TEXT against the live env value would
    // always mismatch, manufacturing a permanent false "differs" on every reference-based entry.
    writeKeysJson(homeDir, { [envVar]: "${" + envVar + "}" });

    const before = readFileSync(join(homeDir, ".petbox", "keys.json"), "utf8");
    const { stdout, stderr, status } = runDoctorOffline(projectDir, homeDir, { [envVar]: "fresh-env-value-for-ref-case" });
    const out = stdout + stderr;

    assert.doesNotMatch(out, /keys\.json.*out of sync/i, `A reference entry must never be reported as drifted. Full output:\n${out}`);
    assert.doesNotMatch(out, new RegExp(envVar), `Full output:\n${out}`);
    assert.equal(status, 0);

    const after = readFileSync(join(homeDir, ".petbox", "keys.json"), "utf8");
    assert.equal(after, before, "a reference entry must never be auto-synced/rewritten by doctor — there is nothing to sync");
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor --offline: envVar set in env but NEVER written to keys.json at all still warns by name", () => {
  const homeDir = freshDir("petbox-doctor-keysdrift-home-");
  const projectDir = freshDir("petbox-doctor-keysdrift-proj-");
  const envVar = "PETBOX_DOCTOR_NEVERWIRED_CASE_API_KEY";
  try {
    writeRegistry(homeDir, [{ prefix: projectDir, project: "neverwired-case", envVar }]);
    writeKeysJson(homeDir, { PETBOX_UNRELATED_API_KEY: "unrelated" });

    const { stdout, stderr, status } = runDoctorOffline(projectDir, homeDir, { [envVar]: "only-in-env" });
    const out = stdout + stderr;

    assert.match(out, new RegExp(envVar), `Full output:\n${out}`);
    assert.doesNotMatch(out, /only-in-env/, `Full output:\n${out}`);
    assert.equal(status, 0);

    const after = JSON.parse(readFileSync(join(homeDir, ".petbox", "keys.json"), "utf8"));
    assert.equal(after[envVar], "only-in-env");
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});
