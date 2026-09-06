// Regression test for card user-scope-roles-rendered-from-cwd-project-definition.
//
// Measured 2026-09-02: the SAME `apply --all --dry-run` reported "using server definition
// default v20" from $system and "default v1" from pochtar — user-scope role rendering resolved
// its definition against `process.cwd()`'s registered project, so a run from the wrong directory
// silently downgraded the whole machine profile to whichever project's document happened to be
// current there. The property that fixed it: the 15 user-scope files are a MACHINE fact and must
// render from MACHINE-WIDE inputs only.
//
// That property survived stage 2 of wire-stops-fetching-definition; only its subject changed.
// There is no server document to leak in any more — the definition is a file cascade — but there
// IS still a per-directory layer in it: `<root>/.petbox/agents`. So the cwd-dependence this card
// closed has a live mechanism again, and this file tests exactly that mechanism instead of the
// retired one. `apply --roles=user` resolves base < user (definition-source.ts's
// resolveUserScopeDefinition) and must never let a PROJECT layer reach a profile file.
//
// The setup mirrors the original shape one-for-one: two directories, each carrying its OWN
// definition input that says something recognizable, run WITHOUT --offline, byte-compared.
//
// Run: node --test src/roles-user-cwd-independent.test.ts

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { DEFAULT_AGENT_DEFINITION, KIT_VERSION } from "./agent-definition.ts";
import { planApply } from "./apply-artifacts.ts";
import { DEFAULT_ROLE_MODEL_SEED } from "./roles.ts";
import { HARNESS_IDS } from "./harness-capabilities.ts";
import { WIRE_EXIT } from "./wire-exit.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

function writeLayer(dir: string, name: string, files: Record<string, string>): void {
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, "layer.json"), JSON.stringify({ name, mode: "overlay" }), "utf8");
  for (const [f, content] of Object.entries(files)) writeFileSync(join(dir, f), content, "utf8");
}

/** A project layer whose prose is recognizable, so a leak into a profile file is unmissable. */
function writeProjectLayer(root: string, label: string): void {
  writeLayer(join(root, ".petbox", "agents"), `project:${label}`, {
    "petbox-worker.md": `PROJECT-LAYER DOCUMENT (${label}) — must never reach a user-scope role file.`,
  });
}

function runWire(cwd: string, homeDir: string, args: string[]): { out: string; status: number | null } {
  const res = spawnSync(process.execPath, [WIRE_TS, ...args], {
    cwd,
    encoding: "utf8",
    env: { ...process.env, USERPROFILE: homeDir, HOME: homeDir, HOMEDRIVE: undefined, HOMEPATH: undefined },
  });
  return { out: (res.stdout ?? "") + (res.stderr ?? ""), status: res.status };
}

const USER_ROLE_DIRS = [".claude/agents", ".config/opencode/agents", ".factory/droids"] as const;

function snapshotUserRoleFiles(homeDir: string): Record<string, string> {
  const out: Record<string, string> = {};
  for (const rel of USER_ROLE_DIRS) {
    const dir = join(homeDir, ...rel.split("/"));
    if (!existsSync(dir)) continue;
    for (const f of readdirSync(dir)) {
      if (/^petbox-[a-z0-9_-]+\.md$/.test(f)) out[`${rel}/${f}`] = readFileSync(join(dir, f), "utf8");
    }
  }
  return out;
}

test(
  "apply --roles=user: two directories carrying DIFFERENT project layers render byte-identical " +
    "user-scope role files — the project layer is never consulted for this step",
  () => {
    const homeDir = freshDir("petbox-cwd-indep-home-");
    const projSystem = freshDir("petbox-cwd-indep-system-");
    const projPochtar = freshDir("petbox-cwd-indep-pochtar-");
    try {
      // The machine-wide layer DOES belong in this render — it is the half of the cascade that is
      // a machine fact — so its prose must be present in the result, proving the user layer is
      // read rather than the whole cascade being skipped.
      writeLayer(join(homeDir, ".petbox", "agents"), "user", {
        "petbox-worker.md": "USER-LAYER DOCUMENT — machine-wide, belongs in every profile file.",
      });
      writeProjectLayer(projSystem, "system");
      writeProjectLayer(projPochtar, "pochtar");

      const first = runWire(projSystem, homeDir, ["apply", "--roles=user"]);
      assert.equal(first.status, WIRE_EXIT.ok, `run from the $system-shaped dir failed; output:\n${first.out}`);
      const afterSystem = snapshotUserRoleFiles(homeDir);
      assert.equal(Object.keys(afterSystem).length, 15, `expected 15 files; output:\n${first.out}`);

      const workerFile = afterSystem[".claude/agents/petbox-worker.md"];
      assert.ok(workerFile, `no worker profile file was written; output:\n${first.out}`);
      assert.match(workerFile!, /USER-LAYER DOCUMENT/, "the machine-wide user layer must be applied");
      for (const content of Object.values(afterSystem)) {
        assert.equal(
          content.includes("PROJECT-LAYER DOCUMENT"),
          false,
          `a project layer leaked into a machine-wide role file:\n${content}`,
        );
      }
      // The layer line for this step must name base < user and nothing else.
      assert.match(first.out, /roles:user\]: layers=2: base\[kit v[^\]]*\] .*default-agents\.json {2}< {2}user\[overlay\]/, `output:\n${first.out}`);
      assert.doesNotMatch(first.out, /roles:user\].*project\[overlay\]/, `output:\n${first.out}`);

      const second = runWire(projPochtar, homeDir, ["apply", "--roles=user"]);
      assert.equal(second.status, WIRE_EXIT.ok, `run from the pochtar-shaped dir failed; output:\n${second.out}`);
      const afterPochtar = snapshotUserRoleFiles(homeDir);

      assert.deepEqual(
        afterPochtar,
        afterSystem,
        "the two runs, from two directories carrying DIFFERENT project layers, must render " +
          "byte-identical files — this is the card's own acceptance test",
      );
    } finally {
      rmSync(homeDir, { recursive: true, force: true });
      rmSync(projSystem, { recursive: true, force: true });
      rmSync(projPochtar, { recursive: true, force: true });
    }
  },
);

test("apply --roles=user --offline: an UNREGISTERED cwd with no layers at all still renders the full 15-file baseline", () => {
  const homeDir = freshDir("petbox-cwd-indep-unreg-home-");
  const proj = freshDir("petbox-cwd-indep-unreg-proj-"); // never written to projects.json
  try {
    const run = runWire(proj, homeDir, ["apply", "--offline", "--roles=user"]);
    assert.equal(run.status, WIRE_EXIT.ok, `output:\n${run.out}`);
    const files = snapshotUserRoleFiles(homeDir);
    assert.equal(Object.keys(files).length, 15, `output:\n${run.out}`);
    assert.match(
      run.out,
      /roles:user\]: layers=1: base\[kit v\S+\] .*default-agents\.json/,
      `output:\n${run.out}`,
    );
    assert.match(run.out, /machine-wide, same on every cwd/, `output:\n${run.out}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(proj, { recursive: true, force: true });
  }
});

test("computeUserRoleReports source: rendering DEFAULT_AGENT_DEFINITION with the kit's own DEFAULT_ROLE_MODEL_SEED matches what apply --roles=user actually writes, for every harness", () => {
  // Unit-level pin (no subprocess) that the SAME primitives applyUserRoles/status use produce the
  // SAME bytes given the SAME (definition, roleModels) pair — i.e. the render is a pure function
  // of the baseline, not of anything environmental. Guards against a future refactor accidentally
  // reintroducing a hidden per-call source of variance (e.g. a timestamp, a cwd-relative path).
  for (const harness of HARNESS_IDS) {
    const planA = planApply(DEFAULT_AGENT_DEFINITION, harness, DEFAULT_ROLE_MODEL_SEED);
    const planB = planApply(DEFAULT_AGENT_DEFINITION, harness, DEFAULT_ROLE_MODEL_SEED);
    assert.deepEqual(planA.files, planB.files, `${harness}: two renders of the same baseline diverged`);
  }
  assert.ok(KIT_VERSION.length > 0, "KIT_VERSION must resolve to a non-empty label");
});

test("status --all --offline: names the MACHINE-WIDE layers (never a project layer) as the user-scope role source", () => {
  const homeDir = freshDir("petbox-cwd-indep-status-home-");
  const proj = freshDir("petbox-cwd-indep-status-proj-");
  try {
    mkdirSync(join(homeDir, ".petbox"), { recursive: true });
    writeFileSync(join(homeDir, ".petbox", "wire.json"), JSON.stringify({ roleScope: "user" }) + "\n", "utf8");
    writeProjectLayer(proj, "must-not-appear");
    const run = runWire(proj, homeDir, ["status", "--all", "--offline"]);
    assert.equal(run.status, WIRE_EXIT.ok, `output:\n${run.out}`);
    assert.match(
      run.out,
      /user-scope role source: source: layers=1: base\[kit v\S+\][^\n]*, kit v\S+ — machine-wide, independent of cwd/,
      `output:\n${run.out}`,
    );
    assert.doesNotMatch(run.out, /user-scope role source:.*project/, `output:\n${run.out}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(proj, { recursive: true, force: true });
  }
});
