// Artifact integrity: dangling spawn/escalation targets, and the orphan sweep for a role that
// left the definition (bug: artifact-integrity-dangling-and-orphans, spec
// definition-truthfulness).
//
// Both halves are about what sits on a person's disk after `apply`, and both were previously
// unenforced by anything: targets were rendered without ever being checked against the roster,
// and a removed role's file was deleted by nobody at all.

import { mkdirSync, mkdtempSync, readFileSync, writeFileSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import test from "node:test";
import assert from "node:assert/strict";

import type { AgentDefinition, AgentRole } from "./agent-definition.ts";
import { agentFilesDir, artifactBasename, expectedArtifactBasenames } from "./apply-artifacts.ts";
import { sweepOrphanArtifacts } from "./apply-orphans.ts";
import { findDanglingTargets, formatDanglingTargets } from "./definition-integrity.ts";
import { HARNESS_IDS, type HarnessId } from "./harness-capabilities.ts";
import { PETBOX_MARKER_LINE } from "./origin-marker.ts";

function role(slug: string, extra: Partial<AgentRole> = {}): AgentRole {
  return { slug, tier: "worker", requiredCapabilities: [], ...extra };
}

function def(...roles: AgentRole[]): AgentDefinition {
  return { name: "test", roles };
}

function freshRoot(): string {
  return mkdtempSync(join(tmpdir(), "petbox-orphan-"));
}

/** A file that looks exactly like one apply wrote: frontmatter carrying the origin marker. */
function writeOurs(abs: string, body = "generated"): void {
  mkdirSync(dirname(abs), { recursive: true });
  writeFileSync(abs, `---\nname: x\n${PETBOX_MARKER_LINE}\n---\n\n${body}\n`, "utf8");
}

/** A real user file: frontmatter, but no origin marker anywhere. */
function writeForeign(abs: string, body = "my own agent"): void {
  mkdirSync(dirname(abs), { recursive: true });
  writeFileSync(abs, `---\nname: x\ndescription: mine\n---\n\n${body}\n`, "utf8");
}

// ── E1: dangling targets ────────────────────────────────────────────────────────────────────

test("E1: an escalation target that is not a role of the definition is reported", () => {
  const d = def(
    role("orchestrator", { escalation: { available: true, targets: ["reserve"] } }),
    role("worker"),
  );
  const dangling = findDanglingTargets(d);
  assert.deepEqual(dangling, [
    { role: "orchestrator", field: "escalation.targets", target: "reserve" },
  ]);
  const text = formatDanglingTargets(dangling);
  assert.match(text, /^ {2}E1 orchestrator\.escalation\.targets → "reserve":/);
});

test("E1: a spawn target that is not a role of the definition is reported", () => {
  const d = def(
    role("orchestrator", { spawn: { allowed: true, allowedRoles: ["worker", "ghost"] } }),
    role("worker"),
  );
  assert.deepEqual(findDanglingTargets(d), [
    { role: "orchestrator", field: "spawn.allowedRoles", target: "ghost" },
  ]);
});

test("E1: a referentially closed definition yields nothing", () => {
  const d = def(
    role("orchestrator", {
      spawn: { allowed: true, allowedRoles: ["worker"] },
      escalation: { available: true, targets: ["worker"] },
    }),
    role("worker"),
  );
  assert.deepEqual(findDanglingTargets(d), []);
});

test("E1 is scoped to what is RENDERED: a stale target behind a disabled switch is not a lie", () => {
  // buildRoleBody prints "Not allowed." / "Not available." here and names no target at all, so
  // the artifact prescribes nothing impossible. Deliberate narrowing vs the research prototype,
  // whose toy check is unconditional — documented in definition-integrity.ts's header.
  const d = def(
    role("orchestrator", {
      spawn: { allowed: false, allowedRoles: ["ghost"] },
      escalation: { available: false, targets: ["ghost"] },
    }),
  );
  assert.deepEqual(findDanglingTargets(d), []);
});

test("every dangling reference is reported, not just the first", () => {
  const d = def(
    role("a", {
      spawn: { allowed: true, allowedRoles: ["x", "y"] },
      escalation: { available: true, targets: ["z"] },
    }),
  );
  assert.equal(findDanglingTargets(d).length, 3);
});

// ── Orphan sweep ────────────────────────────────────────────────────────────────────────────

test("orphan sweep removes the artifact of a role that left the definition — on ALL THREE harnesses", () => {
  const root = freshRoot();
  const before = def(role("worker"), role("reserve"));
  const after = def(role("worker"));

  for (const harness of HARNESS_IDS) {
    const dir = join(root, agentFilesDir(harness));
    writeOurs(join(dir, artifactBasename(harness, "worker")));
    writeOurs(join(dir, artifactBasename(harness, "reserve")));
  }

  // Sanity: with the OLD roster nothing is an orphan.
  for (const harness of HARNESS_IDS) {
    assert.deepEqual(sweepOrphanArtifacts(root, harness, before), [], `${harness}: swept too early`);
  }

  for (const harness of HARNESS_IDS) {
    const dir = join(root, agentFilesDir(harness));
    const outcomes = sweepOrphanArtifacts(root, harness, after);
    assert.deepEqual(
      outcomes.map((o) => o.outcome),
      ["removed"],
      `${harness}: expected exactly one removal`,
    );
    assert.equal(
      outcomes[0]?.path,
      join(dir, artifactBasename(harness, "reserve")),
      `${harness}: removed the wrong file`,
    );
    assert.ok(
      !existsSync(join(dir, artifactBasename(harness, "reserve"))),
      `${harness}: orphan still on disk — removing a role is still physically impossible`,
    );
    assert.ok(
      existsSync(join(dir, artifactBasename(harness, "worker"))),
      `${harness}: a live role's artifact was destroyed`,
    );
  }
});

test("droid's orphan is found under its SANITIZED name, not the raw one", () => {
  // .factory/droids files go through sanitizeDroidName; a sweep that assumed `petbox-<slug>.md`
  // for every harness would silently never find a droid orphan.
  const harness: HarnessId = "droid";
  const root = freshRoot();
  const dir = join(root, agentFilesDir(harness));
  const name = artifactBasename(harness, "reserve");
  assert.equal(name, "petbox-reserve.md");
  assert.equal(agentFilesDir(harness), ".factory/droids");
  writeOurs(join(dir, name));
  const outcomes = sweepOrphanArtifacts(root, harness, def(role("worker")));
  assert.deepEqual(outcomes, [{ path: join(dir, name), outcome: "removed" }]);
});

test("a user's own petbox-*.md WITHOUT the origin marker survives the sweep, byte-for-byte", () => {
  const root = freshRoot();
  const harness: HarnessId = "claude-code";
  const dir = join(root, agentFilesDir(harness));
  const abs = join(dir, "petbox-reserve.md");
  writeForeign(abs, "hand-written by the user");
  const beforeBytes = readFileSync(abs);

  const outcomes = sweepOrphanArtifacts(root, harness, def(role("worker")));
  assert.deepEqual(
    outcomes.map((o) => o.outcome),
    ["kept-foreign"],
    "a file we do not own must be reported, not deleted",
  );
  assert.ok(existsSync(abs), "apply deleted a file it did not write");
  assert.deepEqual(readFileSync(abs), beforeBytes, "a foreign file was modified");
});

test("the sweep never touches a bare legacy name — that is the rename path's business", () => {
  // `worker.md` (pre-namespacing) is removed by cleanupLegacyArtifact only AFTER its
  // `petbox-worker.md` replacement lands. Letting the orphan pass delete it would destroy a
  // user's file on any run where the replacement write did not happen.
  const root = freshRoot();
  const harness: HarnessId = "claude-code";
  const dir = join(root, agentFilesDir(harness));
  writeOurs(join(dir, "worker.md"));
  const outcomes = sweepOrphanArtifacts(root, harness, def(role("orchestrator")));
  assert.deepEqual(outcomes, []);
  assert.ok(existsSync(join(dir, "worker.md")));
});

test("a role blocked by the truthfulness gate is NOT an orphan — it is still declared", () => {
  // expectedArtifactBasenames is built from definition.roles, never from an ApplyPlan's files:
  // a gate-skipped role's stale artifact is a gate problem to fix, not a file to delete.
  const harness: HarnessId = "claude-code";
  const d = def(role("worker"), role("reserve", { requiredCapabilities: ["not_a_real_capability"] }));
  assert.deepEqual(
    [...expectedArtifactBasenames(d, harness)].sort(),
    ["petbox-reserve.md", "petbox-worker.md"],
  );
});

test("a missing agent directory sweeps to nothing instead of throwing", () => {
  const root = freshRoot();
  for (const harness of HARNESS_IDS) {
    assert.deepEqual(sweepOrphanArtifacts(root, harness, def(role("worker"))), []);
  }
});
