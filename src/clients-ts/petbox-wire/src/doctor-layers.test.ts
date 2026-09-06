// Integration test for `doctor` gating the SAME definition apply would compile
// (bug: doctor-gates-wrong-definition), now that "the same definition" means the FILE CASCADE
// base < user < project (card wire-stops-fetching-definition, spec definition-layer-cascade).
//
// This file replaces doctor-definition.test.ts, which spun up fake PetBox servers to prove
// doctor gated the LKG cache rather than the hard-coded baseline, and that its built-in-vs-live
// drift check never called an answered 404/403/503 "unreachable". Every one of those tests
// asserted something about a leg that no longer exists: doctor makes no definition fetch, keeps
// no cache, and has no second document to drift from. Replacing them with a fake server that
// nothing calls would be theatre; what is worth pinning down is what doctor gates NOW.
//
// wire.ts runs main() at import time (see its own file header for why testable logic lives in
// side modules), so the only way to exercise doctor's real argv/behavior end-to-end is to spawn
// it as a subprocess with a redirected HOME.
//
// Run: node --test src/doctor-layers.test.ts

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

function writeLayer(dir: string, manifest: { name: string; mode: string }, files: Record<string, string>): void {
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, "layer.json"), JSON.stringify(manifest), "utf8");
  for (const [name, content] of Object.entries(files)) writeFileSync(join(dir, name), content, "utf8");
}

function runDoctor(
  cwd: string,
  homeDir: string,
  extraArgs: string[] = [],
): { out: string; stderr: string; status: number | null } {
  const res = spawnSync(process.execPath, [WIRE_TS, "doctor", ...extraArgs], {
    cwd,
    encoding: "utf8",
    env: {
      ...process.env,
      // Windows resolves homedir() from USERPROFILE; POSIX from HOME. Set both so the test is
      // portable across the dev machine (win32) and any Linux CI runner.
      USERPROFILE: homeDir,
      HOME: homeDir,
      HOMEDRIVE: undefined,
      HOMEPATH: undefined,
    },
  });
  return { out: (res.stdout ?? "") + (res.stderr ?? ""), stderr: res.stderr ?? "", status: res.status };
}

test("doctor with no layer directories: gates the kit base, names it as the only layer, exits 0", () => {
  const homeDir = freshDir("petbox-doctor-home-");
  const projectDir = freshDir("petbox-doctor-proj-");
  try {
    const { out, status } = runDoctor(projectDir, homeDir, ["--offline"]);
    assert.match(out, /doctor: definition="base"/, `Full output:\n${out}`);
    assert.match(out, /layers=1: base\[kit v/, `Full output:\n${out}`);
    // Absence is no opinion, and must READ as that — never as a degradation or a failure.
    assert.match(out, /absent, no opinion/, `Full output:\n${out}`);
    assert.equal(status, WIRE_EXIT.ok, `Full output:\n${out}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor gates the CASCADE, not the bare baseline: a user layer's override shows in the provenance it prints", () => {
  const homeDir = freshDir("petbox-doctor-home-");
  const projectDir = freshDir("petbox-doctor-proj-");
  try {
    writeLayer(join(homeDir, ".petbox", "agents"), { name: "user", mode: "overlay" }, {
      "petbox-worker.md": "USER-LAYER-WORKER-PROSE-abc123",
    });
    const { out, status } = runDoctor(projectDir, homeDir);
    assert.match(out, /doctor: definition="base < user"/, `Full output:\n${out}`);
    assert.match(out, /layers=2: base\[kit v[^\]]*\] .*  <  user\[overlay\] /, `Full output:\n${out}`);
    assert.match(out, /per-field provenance/, `Full output:\n${out}`);
    assert.match(out, /worker {2}tier=worker {2}provenance: .*notes=user/, `Full output:\n${out}`);
    assert.equal(status, WIRE_EXIT.ok, `Full output:\n${out}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor HARD-FAILS on a present-but-unparseable layer, naming the file by absolute path (spec broken-layer-fails-loudly)", () => {
  const homeDir = freshDir("petbox-doctor-home-");
  const projectDir = freshDir("petbox-doctor-proj-");
  try {
    const layerDir = join(projectDir, ".petbox", "agents");
    writeLayer(layerDir, { name: "project", mode: "overlay" }, { "petbox-worker.json": "{ not json at all" });
    const { out, stderr, status } = runDoctor(projectDir, homeDir);

    assert.equal(
      status,
      WIRE_EXIT.hard,
      `a broken layer must stop the gate, not degrade it quietly. Full output:\n${out}`,
    );
    assert.ok(
      stderr.includes(join(layerDir, "petbox-worker.json")),
      `stderr must name the broken file by ABSOLUTE path. stderr:\n${stderr}`,
    );
    assert.match(stderr, /is not valid JSON/, `stderr must carry the parser's own complaint:\n${stderr}`);
    // The one thing D15 forbids: quietly answering from something that worked earlier.
    assert.doesNotMatch(out, /LKG|last known good|cache/i, `Full output:\n${out}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor HARD-FAILS on a cascade ERROR (a layer that tombstones a role still named as an escalation target)", () => {
  const homeDir = freshDir("petbox-doctor-home-");
  const projectDir = freshDir("petbox-doctor-proj-");
  try {
    writeLayer(join(projectDir, ".petbox", "agents"), { name: "project", mode: "overlay" }, {
      "petbox-reserve.json": JSON.stringify({ slug: "reserve", removed: true, reason: "test" }),
    });
    const { out, stderr, status } = runDoctor(projectDir, homeDir);
    assert.equal(status, WIRE_EXIT.hard, `Full output:\n${out}`);
    assert.match(stderr, /E1 .*orchestrator\.escalation\.targets → "reserve"/, `stderr:\n${stderr}`);
    assert.match(stderr, /Nothing was gated/, `stderr:\n${stderr}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("--offline changes NOTHING about the definition doctor gates — the resolve never had a network leg to skip", () => {
  const homeDir = freshDir("petbox-doctor-home-");
  const projectDir = freshDir("petbox-doctor-proj-");
  try {
    writeLayer(join(homeDir, ".petbox", "agents"), { name: "user", mode: "overlay" }, {
      "petbox-worker.md": "USER-LAYER-WORKER-PROSE-abc123",
    });
    const online = runDoctor(projectDir, homeDir);
    const offline = runDoctor(projectDir, homeDir, ["--offline"]);

    const definitionLine = (out: string) => out.split("\n").filter((l) => l.startsWith("doctor: definition=") || l.startsWith("doctor: layers="));
    assert.deepEqual(definitionLine(online.out), definitionLine(offline.out));
    assert.equal(online.status, WIRE_EXIT.ok, `Full output:\n${online.out}`);
    assert.equal(offline.status, WIRE_EXIT.ok, `Full output:\n${offline.out}`);
    // --offline is a deliberate skip of the network CHECKS doctor still has, and must say so —
    // it must never be reported as, or confused with, an unreachable server
    // (bug doctor-reports-answering-server-unreachable, whose wording rule outlives its subject).
    assert.match(offline.out, /skill check skipped \(--offline\)/, `Full output:\n${offline.out}`);
    assert.doesNotMatch(offline.out, /unreachable/i, `Full output:\n${offline.out}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});
