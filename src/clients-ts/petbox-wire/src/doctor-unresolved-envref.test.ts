// Regression test for observation doctor-crashes-on-unresolved-envref-before-any-key-report
// (card keys-json-supports-env-var-references, acceptance item 5's fourth state).
//
// keys.json (card keys-json-supports-env-var-references) lets a stored value be a $VAR/${VAR}
// reference instead of a literal. registry.ts's resolveProject/readKeyStore are documented as
// "never throw, ONE deliberate exception: UnresolvedEnvRefError" — for the reference's target
// var not being set right now. That throw is the CORRECT, wanted behavior for `wire`/hooks (a
// loud refusal before any request goes out, owner's decision, must not change).
//
// `doctor` and `status` are a different animal: both exist to REPORT a broken state, and
// registry.ts's own comment over inspectKeyStoreEntry says a diagnosis-only reader "must always
// finish and report rather than crash". Before this fix, doctor's skill-check section called
// resolveProject() unguarded (wire.ts, in runDoctor) and status's whole run called it unguarded
// too (status.ts's runStatus) — both let UnresolvedEnvRefError escape as an uncaught exception:
// a raw Node stack trace, exit 1, and NEITHER doctor's per-key/wire.log-tail sections NOR any of
// status's pillars ever printed. This file locks both fixes down.
//
// Same spawn-subprocess technique as doctor-keys-drift.test.ts (wire.ts runs main() at module
// top level).
//
// Run: node --test src/doctor-unresolved-envref.test.ts

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { WIRE_EXIT } from "./wire-exit.ts";

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

// The outer envVar itself must NOT be set (else resolveProject's env-first fallback never
// reaches keys.json at all) — only the FILE holds the (unresolved) reference. Overriding process
// env with `undefined` deletes the var for the child even if this test runner's own ambient
// environment happens to carry it.
function runCli(
  subcommand: string,
  cwd: string,
  homeDir: string,
  outerVar: string,
  innerVar: string,
): { stdout: string; stderr: string; status: number | null } {
  const res = spawnSync(process.execPath, [WIRE_TS, subcommand, "--offline"], {
    cwd,
    encoding: "utf8",
    env: {
      ...process.env,
      USERPROFILE: homeDir,
      HOME: homeDir,
      HOMEDRIVE: undefined,
      HOMEPATH: undefined,
      [outerVar]: undefined,
      [innerVar]: undefined,
    },
  });
  return { stdout: res.stdout ?? "", stderr: res.stderr ?? "", status: res.status };
}

test("doctor --offline on an UNRESOLVED reference: does not crash, names outer var / inner ref var / project, still prints the wire.log tail and per-harness sections, exits INCOMPLETE (4)", () => {
  const homeDir = freshDir("petbox-doctor-envref-home-");
  const projectDir = freshDir("petbox-doctor-envref-proj-");
  const outerVar = "PETBOX_DOCTOR_ENVREF_OUTER_KEY";
  const innerVar = "PETBOX_DOCTOR_ENVREF_INNER_TARGET";
  const project = "envref-unresolved-project";
  try {
    writeRegistry(homeDir, [{ prefix: projectDir, project, envVar: outerVar }]);
    writeKeysJson(homeDir, { [outerVar]: "${" + innerVar + "}" });

    const { stdout, stderr, status } = runCli("doctor", projectDir, homeDir, outerVar, innerVar);
    const out = stdout + stderr;

    // The crash this regresses: an uncaught UnresolvedEnvRefError bubbling out of
    // runDoctor's unguarded resolveProject() call prints a raw Node stack trace and exits 1,
    // BEFORE any of the assertions below have a chance to be true.
    assert.doesNotMatch(out, /UnresolvedEnvRefError\s*$/m, `Expected no raw stack trace. Full output:\n${out}`);
    assert.doesNotMatch(out, /at resolveProject/, `Expected no raw stack trace naming resolveProject. Full output:\n${out}`);

    // Third state named explicitly: outer var, project, and the reference target.
    assert.match(out, new RegExp(outerVar), `Expected the outer envVar named. Full output:\n${out}`);
    assert.match(out, new RegExp(innerVar), `Expected the reference target named. Full output:\n${out}`);
    assert.match(out, new RegExp(project), `Expected the project named. Full output:\n${out}`);

    // Doctor must still reach and print its other sections — the whole point of the fix.
    assert.match(out, /doctor: wire\.log —/, `Expected the wire.log tail section to print. Full output:\n${out}`);
    assert.match(out, /doctor: claude-code — OK/, `Expected the per-harness truthfulness section to print. Full output:\n${out}`);
    assert.match(out, /doctor: INCOMPLETE/, `Expected the INCOMPLETE verdict line. Full output:\n${out}`);

    // Exit code: WIRE_EXIT.incomplete (4) — a requested step (skill/banner-budget check) did not
    // run for a reason outside anything --offline or "unregistered project" already covers.
    assert.equal(status, WIRE_EXIT.incomplete, `Full output:\n${out}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("status --offline on an UNRESOLVED reference: does not crash, names outer var / inner ref var, still exits 0 (status's own contract: never a verdict)", () => {
  const homeDir = freshDir("petbox-status-envref-home-");
  const projectDir = freshDir("petbox-status-envref-proj-");
  const outerVar = "PETBOX_STATUS_ENVREF_OUTER_KEY";
  const innerVar = "PETBOX_STATUS_ENVREF_INNER_TARGET";
  const project = "envref-unresolved-status-project";
  try {
    writeRegistry(homeDir, [{ prefix: projectDir, project, envVar: outerVar }]);
    writeKeysJson(homeDir, { [outerVar]: "${" + innerVar + "}" });

    const { stdout, stderr, status } = runCli("status", projectDir, homeDir, outerVar, innerVar);
    const out = stdout + stderr;

    assert.doesNotMatch(out, /UnresolvedEnvRefError\s*$/m, `Expected no raw stack trace. Full output:\n${out}`);
    assert.doesNotMatch(out, /at resolveProject/, `Expected no raw stack trace naming resolveProject. Full output:\n${out}`);

    assert.match(out, new RegExp(outerVar), `Expected the outer envVar named. Full output:\n${out}`);
    assert.match(out, new RegExp(innerVar), `Expected the reference target named. Full output:\n${out}`);

    // status still prints its pillars past the diagnostic (never a registered-project message —
    // this project IS registered, only its key is an unresolved reference).
    assert.match(out, /pillar 1\/4/, `Expected pillar 1 to print. Full output:\n${out}`);
    assert.doesNotMatch(
      out,
      /is not a registered project/,
      `An unresolved reference is not the same fact as "not registered". Full output:\n${out}`,
    );

    // status's documented contract (status.ts's header): "always exit 0 unless it itself
    // throws (an actual bug)" — this WAS that bug; fixed, it must exit 0 like every other run.
    assert.equal(status, WIRE_EXIT.ok, `Full output:\n${out}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});
