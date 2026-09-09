// Unit tests for registry.ts's keys.json-vs-environment drift detector (card
// keys-json-doctor-drift-check). Measured 2026-09-09 (work/keys-env-refresh-path's comment):
// ~/.petbox/keys.json is written ONLY by a full `wire` run, so a plain env-var rotation never
// reaches it — the file can go stale indefinitely, and `doctor` never noticed because it resolves
// a key the same env-first way the hooks do (registry.ts:152), which proves nothing about the
// file itself.
//
// Hard rule under test: never print or return key material anywhere — only per-envVar hash
// equality and the envVar's name. These tests assert on shape/names, never on any key value
// appearing in output.
//
// Run: node --test src/keys-drift.test.ts

import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { detectKeysStoreDrift, formatKeyDrift, registryPath } from "./registry.ts";

function freshHome(): string {
  return mkdtempSync(join(tmpdir(), "petbox-wire-keys-drift-"));
}

function writeRegistry(home: string, entries: Array<{ prefix: string; project: string; envVar: string }>): void {
  mkdirSync(join(home, ".petbox"), { recursive: true });
  writeFileSync(registryPath(home), JSON.stringify({ entries }), "utf8");
}

function writeKeysJson(home: string, store: Record<string, string>): void {
  mkdirSync(join(home, ".petbox"), { recursive: true });
  writeFileSync(join(home, ".petbox", "keys.json"), JSON.stringify(store), "utf8");
}

// Runs `fn` with `envVar` set to `value` (or deleted, when value is undefined), always restoring
// the prior value afterwards — this process's own env only, never a real user variable.
function withEnv<T>(envVar: string, value: string | undefined, fn: () => T): T {
  const prior = process.env[envVar];
  if (value === undefined) delete process.env[envVar];
  else process.env[envVar] = value;
  try {
    return fn();
  } finally {
    if (prior === undefined) delete process.env[envVar];
    else process.env[envVar] = prior;
  }
}

test("detectKeysStoreDrift: env unset entirely → no drift (file is the only source, nothing to diff)", () => {
  const home = freshHome();
  const envVar = "PETBOX_KEYSDRIFT_UNSET_TEST_API_KEY";
  try {
    writeRegistry(home, [{ prefix: home, project: "p", envVar }]);
    writeKeysJson(home, { [envVar]: "file-value-does-not-matter" });
    withEnv(envVar, undefined, () => {
      assert.deepEqual(detectKeysStoreDrift(home), []);
    });
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("detectKeysStoreDrift: env set, file matches (same hash) → no drift, silent", () => {
  const home = freshHome();
  const envVar = "PETBOX_KEYSDRIFT_MATCH_TEST_API_KEY";
  try {
    writeRegistry(home, [{ prefix: home, project: "p", envVar }]);
    writeKeysJson(home, { [envVar]: "same-value-123" });
    withEnv(envVar, "same-value-123", () => {
      assert.deepEqual(detectKeysStoreDrift(home), []);
    });
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("detectKeysStoreDrift: env set, file has a DIFFERENT value → one drift entry, kind 'differs', names only the envVar", () => {
  const home = freshHome();
  const envVar = "PETBOX_KEYSDRIFT_DIFFERS_TEST_API_KEY";
  try {
    writeRegistry(home, [{ prefix: home, project: "p", envVar }]);
    writeKeysJson(home, { [envVar]: "stale-file-value" });
    withEnv(envVar, "fresh-env-value", () => {
      const drifts = detectKeysStoreDrift(home);
      assert.deepEqual(drifts, [{ envVar, kind: "differs" }]);
      const msg = formatKeyDrift(drifts[0]!);
      assert.match(msg, new RegExp(envVar));
      assert.doesNotMatch(msg, /stale-file-value/);
      assert.doesNotMatch(msg, /fresh-env-value/);
    });
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("detectKeysStoreDrift: env set, envVar absent from file entirely → one drift entry, kind 'missing-in-file'", () => {
  const home = freshHome();
  const envVar = "PETBOX_KEYSDRIFT_MISSING_TEST_API_KEY";
  try {
    writeRegistry(home, [{ prefix: home, project: "p", envVar }]);
    // keys.json exists but never got this envVar written (never a full `wire` run for it).
    writeKeysJson(home, { PETBOX_OTHER_UNRELATED_API_KEY: "unrelated" });
    withEnv(envVar, "fresh-env-value", () => {
      const drifts = detectKeysStoreDrift(home);
      assert.deepEqual(drifts, [{ envVar, kind: "missing-in-file" }]);
    });
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("detectKeysStoreDrift: no keys.json at all, env set → 'missing-in-file' (never crashes on ENOENT)", () => {
  const home = freshHome();
  const envVar = "PETBOX_KEYSDRIFT_NOFILE_TEST_API_KEY";
  try {
    writeRegistry(home, [{ prefix: home, project: "p", envVar }]);
    withEnv(envVar, "fresh-env-value", () => {
      const drifts = detectKeysStoreDrift(home);
      assert.deepEqual(drifts, [{ envVar, kind: "missing-in-file" }]);
    });
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("detectKeysStoreDrift: multiple registered envVars, only some diverge → only those are reported", () => {
  const home = freshHome();
  const okVar = "PETBOX_KEYSDRIFT_MULTI_OK_API_KEY";
  const badVar = "PETBOX_KEYSDRIFT_MULTI_BAD_API_KEY";
  try {
    writeRegistry(home, [
      { prefix: join(home, "a"), project: "a", envVar: okVar },
      { prefix: join(home, "b"), project: "b", envVar: badVar },
    ]);
    writeKeysJson(home, { [okVar]: "same", [badVar]: "old" });
    const priorOk = process.env[okVar];
    const priorBad = process.env[badVar];
    process.env[okVar] = "same";
    process.env[badVar] = "new";
    try {
      const drifts = detectKeysStoreDrift(home);
      assert.deepEqual(drifts, [{ envVar: badVar, kind: "differs" }]);
    } finally {
      if (priorOk === undefined) delete process.env[okVar];
      else process.env[okVar] = priorOk;
      if (priorBad === undefined) delete process.env[badVar];
      else process.env[badVar] = priorBad;
    }
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});
