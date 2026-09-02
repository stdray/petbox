// End-to-end tests for `apply --all` / `--dry-run` (task kit-version-lands-everywhere-and-sweeps
// item 2): a single call sweeps EVERY registered project directory instead of only cwd, with a
// per-project outcome line, and a registry entry whose directory no longer exists must be
// reported and skipped WITHOUT aborting the rest of the sweep — this is the card's explicit
// trap ("шесть чужих проектов — не полигон"; a mass apply must have a preview mode before it
// writes into other people's working directories).
//
// --offline throughout: it makes the whole run network-free (built-in default definition, no
// workspace probe, skills step intentionally skipped), so this exercises the actual write/no-op
// decision plumbing (writeArtifact's dryRun option, threaded through performApply) without
// needing a fake HTTP server.
//
// Seam: same throwaway-HOME spawn pattern every other CLI e2e test in this package uses.
//
// Run: node --test src/apply-all-registry.test.ts

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

function writeRegistry(homeDir: string, entries: Array<{ prefix: string; project: string; envVar: string }>): void {
  mkdirSync(join(homeDir, ".petbox"), { recursive: true });
  writeFileSync(join(homeDir, ".petbox", "projects.json"), JSON.stringify({ entries }, null, 2), "utf8");
}

function runWire(args: string[], homeDir: string, cwd: string): { out: string; status: number | null } {
  const res = spawnSync(process.execPath, [WIRE_TS, ...args], {
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

function agentFileFor(projectDir: string): string {
  return join(projectDir, ".claude", "agents", "petbox-worker.md");
}

test("apply --all --offline --dry-run: writes NOTHING, reports every project including a stale (missing) registry entry, never aborts", () => {
  const homeDir = freshDir("petbox-apply-all-home-");
  const projA = freshDir("petbox-apply-all-projA-");
  const missingDir = join(freshDir("petbox-apply-all-parent-"), "gone");
  try {
    writeRegistry(homeDir, [
      { prefix: projA, project: "proj-a", envVar: "PETBOX_PROJ_A_API_KEY" },
      { prefix: missingDir, project: "proj-missing", envVar: "PETBOX_PROJ_MISSING_API_KEY" },
    ]);

    const { out, status } = runWire(["apply", "--all", "--offline", "--dry-run"], homeDir, projA);

    assert.equal(status, WIRE_EXIT.ok, `expected exit 0; output:\n${out}`);
    // Nothing was actually written — the whole point of --dry-run.
    assert.equal(existsSync(agentFileFor(projA)), false, "dry-run must not create any artifact file");
    // Both projects show up in the per-project table, in order; the missing one never aborts it.
    assert.match(out, /proj-a/);
    assert.match(out, /proj-missing/);
    assert.match(out, /missing/i);
    assert.match(out, /would write/i);
    // Summary line accounts for both rows.
    // This `written=1` is the PER-PROJECT count in the `--all` tail ("2 project(s): written=1"),
    // not the per-file counter — that one was renamed to `writes=` (card:
    // normalize-all-environments-to-default item 4) and is asserted separately below.
    assert.match(out, /written=1/);
    // The preview's own file counts, from the same ledger the "would write" lines came from.
    assert.match(out, /writes=15 \(roles=15 skills=0\)/);
    assert.match(out, /missing=1/);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projA, { recursive: true, force: true });
  }
});

test("apply --all --offline: actually writes into the registered project directory; a second dry-run then reports 'unchanged'", () => {
  const homeDir = freshDir("petbox-apply-all-home2-");
  const projA = freshDir("petbox-apply-all-projA2-");
  try {
    writeRegistry(homeDir, [{ prefix: projA, project: "proj-a2", envVar: "PETBOX_PROJ_A2_API_KEY" }]);

    const first = runWire(["apply", "--all", "--offline"], homeDir, projA);
    assert.equal(first.status, WIRE_EXIT.ok, `expected exit 0; output:\n${first.out}`);
    assert.equal(existsSync(agentFileFor(projA)), true, "a real (non-dry-run) apply --all must write the artifact");
    assert.match(first.out, /wrote/i);
    assert.match(first.out, /written=1/);

    // Re-run in --dry-run: everything already matches, so nothing is (or would be) written again.
    const second = runWire(["apply", "--all", "--offline", "--dry-run"], homeDir, projA);
    assert.equal(second.status, WIRE_EXIT.ok, `expected exit 0; output:\n${second.out}`);
    assert.match(second.out, /unchanged=1/);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projA, { recursive: true, force: true });
  }
});

test("apply --all --offline: a foreign (non-PetBox) file at an artifact path is refused for THAT project only, without crashing the sweep", () => {
  const homeDir = freshDir("petbox-apply-all-home3-");
  const projA = freshDir("petbox-apply-all-projA3-");
  const projB = freshDir("petbox-apply-all-projB3-");
  try {
    writeRegistry(homeDir, [
      { prefix: projA, project: "proj-a3", envVar: "PETBOX_PROJ_A3_API_KEY" },
      { prefix: projB, project: "proj-b3", envVar: "PETBOX_PROJ_B3_API_KEY" },
    ]);
    // Plant a real, non-PetBox file at proj A's would-be artifact path.
    mkdirSync(join(projA, ".claude", "agents"), { recursive: true });
    writeFileSync(agentFileFor(projA), "# not ours, no origin marker\n", "utf8");

    const { out, status } = runWire(["apply", "--all", "--offline"], homeDir, projA);

    // The refusal for proj-a3 is the strongest outcome across the sweep -> exit 1 (hard).
    assert.equal(status, WIRE_EXIT.hard, `expected exit 1; output:\n${out}`);
    assert.match(out, /proj-a3/);
    assert.match(out, /refused/i);
    // proj-b3 still got its own real, successful outcome — the refusal did not take the sweep down.
    assert.match(out, /proj-b3/);
    assert.equal(existsSync(agentFileFor(projB)), true, "proj-b3 must still be written despite proj-a3's refusal");
    // The foreign file itself must be untouched, byte for byte.
    assert.equal(readFileSync(agentFileFor(projA), "utf8"), "# not ours, no origin marker\n");
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projA, { recursive: true, force: true });
    rmSync(projB, { recursive: true, force: true });
  }
});
