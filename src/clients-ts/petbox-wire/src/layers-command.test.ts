// Integration test for the `layers` CLI subcommand (card role-definition-cascade-revisit,
// requirement 1 — "a command showing every layer on this machine and how they diverge", the one
// bullet the accepted idea's spec_plan never covered).
//
// wire.ts runs main() at import time (see doctor-definition.test.ts's header for why), so the
// only way to exercise `layers`'s actual argv/behavior end-to-end is to spawn it as a real
// subprocess (`node src/wire.ts layers ...`), same technique the rest of this package's CLI
// tests use.
//
// The trap this command exists to close (observation doctor-drift-check-silent-skip-unregistered-
// dir): "no divergence" and "could not check" must never look the same, in prose OR in exit code.
// Every case below asserts BOTH: the exit code AND that the stdout/stderr text names the right
// state explicitly. Before this command existed, none of this had a machine-checkable answer at
// all — that absence is exactly the regression this test pins down.
//
// Run: node --test src/layers-command.test.ts

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

const WIRE_TS = join(import.meta.dirname, "wire.ts");

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

function writeLayer(
  dir: string,
  manifest: { name: string; mode: string },
  files: Record<string, string> = {},
): void {
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, "layer.json"), JSON.stringify(manifest), "utf8");
  for (const [name, content] of Object.entries(files)) {
    writeFileSync(join(dir, name), content, "utf8");
  }
}

function runLayers(
  args: string[],
  opts: { cwd?: string; homeDir?: string } = {},
): { stdout: string; stderr: string; status: number | null } {
  const homeDir = opts.homeDir ?? freshDir("petbox-layers-home-");
  const res = spawnSync(process.execPath, [WIRE_TS, "layers", ...args], {
    cwd: opts.cwd ?? homeDir,
    encoding: "utf8",
    env: {
      ...process.env,
      // Windows resolves homedir() from USERPROFILE; POSIX from HOME (same portability note as
      // doctor-definition.test.ts) — isolates the default ~/.petbox/agents candidate from
      // whatever the real developer machine happens to have.
      USERPROFILE: homeDir,
      HOME: homeDir,
      HOMEDRIVE: undefined,
      HOMEPATH: undefined,
    },
  });
  return { stdout: res.stdout ?? "", stderr: res.stderr ?? "", status: res.status };
}

test("layers: two clean overlay layers — exit 0, and the override is NAMED by field, not just 'files differ'", () => {
  const root = freshDir("petbox-layers-clean-");
  try {
    const user = join(root, "user");
    const project = join(root, "project");
    writeLayer(
      user,
      { name: "user", mode: "overlay" },
      { "petbox-worker.json": JSON.stringify({ slug: "worker", tier: "worker", requiredCapabilities: [] }) },
    );
    writeLayer(project, { name: "project", mode: "overlay" }, {
      "petbox-worker.json": JSON.stringify({ slug: "worker", tier: "worker-highstakes" }),
    });
    const res = runLayers([user, project]);
    assert.equal(res.status, 0, res.stderr + res.stdout);
    // Field-level divergence must be nameable, not just "files differ".
    assert.match(res.stdout, /tier=worker-highstakes/);
    assert.match(res.stdout, /provenance: tier=project/);
    assert.match(res.stdout, /project: ~ worker → tier/);
    assert.match(res.stdout, /clean — 2 layer\(s\) compared, zero cascade errors/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("layers: a real cascade ERROR (dangling escalation target) is reported and exits 1, distinct from clean", () => {
  const root = freshDir("petbox-layers-error-");
  try {
    const base = join(root, "base");
    writeLayer(base, { name: "base", mode: "overlay" }, {
      "petbox-orchestrator.json": JSON.stringify({
        slug: "orchestrator",
        tier: "orchestrator",
        requiredCapabilities: [],
        escalation: { available: true, targets: ["reserve"] },
      }),
      "petbox-reserve.json": JSON.stringify({ slug: "reserve", tier: "reserve", requiredCapabilities: [] }),
    });
    const drop = join(root, "drop");
    writeLayer(drop, { name: "drop", mode: "overlay" }, {
      "petbox-reserve.json": JSON.stringify({ slug: "reserve", removed: true, reason: "test" }),
    });
    const res = runLayers([base, drop]);
    assert.equal(res.status, 1, res.stderr + res.stdout);
    assert.match(res.stderr, /DIVERGED — 1 cascade ERROR/);
    assert.match(res.stdout, /E1 .*orchestrator\.escalation.*reserve/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("layers: zero layer directories present — CANNOT CHECK, exit 3, never confused with clean (0)", () => {
  const root = freshDir("petbox-layers-none-");
  try {
    const res = runLayers([join(root, "nope-a"), join(root, "nope-b")]);
    assert.equal(res.status, 3, res.stderr + res.stdout);
    assert.match(res.stderr, /CANNOT CHECK/);
    assert.match(res.stderr, /NOT "no divergence"/);
    assert.doesNotMatch(res.stdout, /clean —/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("layers: exactly one layer present — nothing to diverge from, CANNOT CHECK (exit 3), not 'clean'", () => {
  const root = freshDir("petbox-layers-one-");
  try {
    const only = join(root, "only");
    writeLayer(only, { name: "only", mode: "overlay" });
    const res = runLayers([only, join(root, "absent")]);
    assert.equal(res.status, 3, res.stderr + res.stdout);
    assert.match(res.stderr, /only one layer is present/);
    assert.doesNotMatch(res.stdout, /clean —/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("layers: a present-but-broken layer source fails LOUD (exit 3) — never silently treated as clean", () => {
  const root = freshDir("petbox-layers-broken-");
  try {
    const good = join(root, "good");
    writeLayer(good, { name: "good", mode: "overlay" });
    // A directory with no layer.json is not a layer at all (readDefinitionLayer's own contract).
    const brokenDir = join(root, "broken");
    mkdirSync(brokenDir, { recursive: true });
    writeFileSync(join(brokenDir, "petbox-worker.json"), JSON.stringify({ slug: "worker" }), "utf8");
    const res = runLayers([good, brokenDir]);
    assert.equal(res.status, 3, res.stderr + res.stdout);
    assert.match(res.stderr, /CANNOT CHECK — a present layer's source is broken/);
    assert.match(res.stderr, /has no layer\.json/);
    assert.doesNotMatch(res.stdout, /clean —/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("layers: no arguments falls back to this command's own conventional defaults, and names the base-layer gap out loud", () => {
  const homeDir = freshDir("petbox-layers-defhome-");
  const cwd = freshDir("petbox-layers-defcwd-");
  try {
    // Neither ~/.petbox/agents (user) nor <cwd>/.petbox/agents (project) exists — a fresh
    // machine, honestly reported as "nothing to check", never as "no divergence".
    const res = runLayers([], { cwd, homeDir });
    assert.equal(res.status, 3, res.stderr + res.stdout);
    assert.match(res.stdout, /base .*NOT yet a layer directory/);
    assert.match(res.stdout, /user .*absent/);
    assert.match(res.stdout, /project .*absent/);
    assert.match(res.stderr, /CANNOT CHECK/);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(cwd, { recursive: true, force: true });
  }
});

test("layers --help exits 0 and prints usage without touching any layer directory", () => {
  const res = runLayers(["--help"]);
  assert.equal(res.status, 0, res.stderr + res.stdout);
  assert.match(res.stdout, /petbox-wire layers/);
});

test("layers: an unrecognized flag is a usage error (exit 2), not a silent no-op", () => {
  const res = runLayers(["--bogus"]);
  assert.equal(res.status, 2, res.stderr + res.stdout);
});
