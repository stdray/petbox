// Integration test for the root cause of the "worker rides on Opus" incident (2026-07-12) — bug
// wire-silent-failures-invisible, card item 1: a corrupt ~/.petbox/roles.json used to silently
// read as "no bindings", so `apply` rendered every role with no `model:` frontmatter line and
// every subagent inherited the session's model. `apply` must now hard-fail instead (WIRE_EXIT.hard),
// with a message naming the file and the incident shape — never silently compiling a falsely-empty
// roster. `doctor` (a read-only diagnostic, not a compiler) keeps the old non-strict behavior:
// it must stay offline-safe and never crash on this, only leave a wire.log trace.
//
// wire.ts runs main() at import time, so `apply`/`doctor` are exercised as real subprocesses —
// same technique as doctor-definition.test.ts / apply-unbound-refusal.test.ts.
//
// Run: node --test src/apply-roles-corrupt.test.ts

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { WIRE_EXIT } from "./wire-exit.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

function runCli(
  subcommand: "apply" | "doctor",
  cwd: string,
  homeDir: string,
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
    },
  });
  return { stdout: res.stdout ?? "", stderr: res.stderr ?? "", status: res.status };
}

function writeCorruptRoles(homeDir: string): string {
  const petboxDir = join(homeDir, ".petbox");
  mkdirSync(petboxDir, { recursive: true });
  const path = join(petboxDir, "roles.json");
  writeFileSync(path, "not-json{{{", "utf8");
  return path;
}

test("apply HARD-FAILS on a corrupt roles.json — exit WIRE_EXIT.hard, message names the file and the incident shape", () => {
  const homeDir = freshDir("petbox-apply-corrupt-home-");
  const projectDir = freshDir("petbox-apply-corrupt-proj-");
  try {
    const rolesFilePath = writeCorruptRoles(homeDir);

    const { stdout, stderr, status } = runCli("apply", projectDir, homeDir);
    const out = stdout + stderr;

    assert.equal(
      status,
      WIRE_EXIT.hard,
      `a corrupt roles.json must hard-fail apply (never silently compile as unbound). Output:\n${out}`,
    );
    assert.match(out, /hard failure/i);
    assert.match(out, /corrupt roles\.json/i);
    assert.match(out, /2026-07-12/, "the message should name the incident shape it is preventing a repeat of");

    // Never compiled anything against the falsely-empty roster — no role files written at all.
    assert.equal(
      existsSync(join(projectDir, ".claude", "agents", "petbox-worker.md")),
      false,
      "apply must write NOTHING when it hard-fails, not a partial roster with missing model: lines",
    );

    // The file itself is untouched — apply must not "fix" it silently either.
    assert.equal(readFileSync(rolesFilePath, "utf8"), "not-json{{{");
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor stays offline-safe against the SAME corrupt roles.json — never crashes, reports it via wire.log instead", () => {
  const homeDir = freshDir("petbox-doctor-corrupt-home-");
  const projectDir = freshDir("petbox-doctor-corrupt-proj-");
  try {
    writeCorruptRoles(homeDir);

    const { stdout, stderr, status } = runCli("doctor", projectDir, homeDir);
    const out = stdout + stderr;

    // doctor is a read-only diagnostic — it must not adopt apply's hard-fail polarity. A corrupt
    // roles.json there degrades to "no bindings" (exactly the pre-existing behavior), same as
    // before this card; the DEFAULT definition has no requiredCapabilities so this still exits 0.
    assert.equal(status, WIRE_EXIT.ok, `doctor must stay offline-safe, not crash. Output:\n${out}`);
    assert.match(out, /capability gate only/i);

    // But the event is no longer invisible: doctor's own wire.log tail print surfaces it.
    assert.match(
      out,
      /wire\.log.*trace line/i,
      `doctor must surface the Class-Б trace from the corrupt-roles.json read. Output:\n${out}`,
    );
    assert.match(out, /roles\.json.*failed to parse/i);

    const logPath = join(homeDir, ".petbox", "wire.log");
    const logContent = readFileSync(logPath, "utf8");
    assert.match(logContent, /roles\.json.*failed to parse/i);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});
