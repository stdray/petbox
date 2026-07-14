// Unit tests for the apply write guard (bug: apply-clobbers-user-agent-files) and the
// namespacing-rename legacy cleanup it enables (chore: petbox-namespaced-agent-names).
//
// Run: node --test src/apply-write.test.ts

import assert from "node:assert/strict";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { renderAgentMarkdown } from "./apply-artifacts.ts";
import { cleanupLegacyArtifact, writeArtifact } from "./apply-write.ts";
import { DEFAULT_AGENT_DEFINITION } from "./agent-definition.ts";
import { hasPetboxMarker, PETBOX_MARKER_LINE } from "./origin-marker.ts";

function freshDir(): string {
  return mkdtempSync(join(tmpdir(), "petbox-apply-write-"));
}

test("hasPetboxMarker: true only for our marker inside frontmatter, never body text", () => {
  assert.equal(hasPetboxMarker(`---\nname: x\n${PETBOX_MARKER_LINE}\n---\n\nbody`), true);
  assert.equal(hasPetboxMarker(`---\nname: x\n---\n\nbody mentions petbox: managed here`), false);
  assert.equal(hasPetboxMarker("no frontmatter at all"), false);
  assert.equal(hasPetboxMarker(""), false);
});

test("writeArtifact: fresh path (no existing file) is written, reason 'new'", () => {
  const dir = freshDir();
  try {
    const abs = join(dir, "sub", "petbox-worker.md");
    const outcome = writeArtifact(abs, "hello");
    assert.deepEqual(outcome, { kind: "written", path: abs, reason: "new" });
    assert.equal(readFileSync(abs, "utf8"), "hello");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("writeArtifact: an existing file WITHOUT our marker is refused — content untouched, loud outcome", () => {
  const dir = freshDir();
  try {
    const abs = join(dir, "worker.md");
    const foreignContent = "# My own worker agent\n\nI wrote this by hand.\n";
    writeFileSync(abs, foreignContent, "utf8");

    const outcome = writeArtifact(abs, "petbox content");
    assert.deepEqual(outcome, { kind: "blocked", path: abs });
    // The file must be byte-for-byte exactly what the user had — apply never touches it.
    assert.equal(readFileSync(abs, "utf8"), foreignContent);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("writeArtifact: an existing file WITH our marker is overwritten silently (routine re-apply)", () => {
  const dir = freshDir();
  try {
    const abs = join(dir, "petbox-worker.md");
    const oldOurs = `---\nname: petbox-worker\n${PETBOX_MARKER_LINE}\n---\n\nold body\n`;
    writeFileSync(abs, oldOurs, "utf8");

    const newContent = `---\nname: petbox-worker\n${PETBOX_MARKER_LINE}\n---\n\nnew body\n`;
    const outcome = writeArtifact(abs, newContent);
    assert.deepEqual(outcome, { kind: "written", path: abs, reason: "own" });
    assert.equal(readFileSync(abs, "utf8"), newContent);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("writeArtifact: an existing marker-carrying file for a DIFFERENT role is still ours — overwritten (marker is the only signal)", () => {
  // Documents the guard's actual contract: it trusts the marker, not path/name matching.
  const dir = freshDir();
  try {
    const abs = join(dir, "petbox-worker.md");
    writeFileSync(abs, `---\nname: petbox-utility\n${PETBOX_MARKER_LINE}\n---\n\nstale\n`, "utf8");
    const outcome = writeArtifact(abs, "fresh");
    assert.equal(outcome.kind, "written");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("cleanupLegacyArtifact: absent path → 'absent', no-op", () => {
  const dir = freshDir();
  try {
    assert.equal(cleanupLegacyArtifact(join(dir, "worker.md")), "absent");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("cleanupLegacyArtifact: an OWNED pre-rename file is removed", () => {
  const dir = freshDir();
  try {
    const abs = join(dir, "worker.md");
    writeFileSync(abs, `---\nname: worker\n${PETBOX_MARKER_LINE}\n---\n\nold pre-rename body\n`, "utf8");
    assert.equal(cleanupLegacyArtifact(abs), "removed");
    assert.equal(existsSync(abs), false);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("cleanupLegacyArtifact: a FOREIGN file at the legacy path is left alone — never deleted", () => {
  const dir = freshDir();
  try {
    const abs = join(dir, "worker.md");
    const foreign = "# a real user agent named worker, no marker\n";
    writeFileSync(abs, foreign, "utf8");
    assert.equal(cleanupLegacyArtifact(abs), "kept-foreign");
    assert.equal(existsSync(abs), true);
    assert.equal(readFileSync(abs, "utf8"), foreign);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// --- end-to-end-ish: a real renderAgentMarkdown output round-trips through the guard ---

test("a freshly rendered role file always carries the marker, so a second apply run is a silent no-op re-write", () => {
  const role = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === "worker")!;
  const content = renderAgentMarkdown(role);
  assert.ok(hasPetboxMarker(content));

  const dir = freshDir();
  try {
    const abs = join(dir, "petbox-worker.md");
    const first = writeArtifact(abs, content);
    assert.equal(first.kind, "written");
    assert.equal(first.reason, "new");

    const second = writeArtifact(abs, content);
    assert.equal(second.kind, "written");
    assert.equal(second.reason, "own");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("simulated apply flow: a real user's own worker.md survives the FIRST apply that now writes petbox-worker.md alongside it", () => {
  // This is the scenario from the bug report: a friend has their own .claude/agents/worker.md.
  // apply now writes .claude/agents/petbox-worker.md (namespacing) and must neither touch nor
  // delete the user's worker.md (it has no marker, so it is never mistaken for a pre-rename
  // leftover of OURS either).
  const role = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === "worker")!;
  const content = renderAgentMarkdown(role);

  const dir = freshDir();
  try {
    const agentsDir = join(dir, ".claude", "agents");
    mkdirSync(agentsDir, { recursive: true });
    const userFile = join(agentsDir, "worker.md");
    const userContent = "# worker\n\nMy hand-written worker agent. Do not touch.\n";
    writeFileSync(userFile, userContent, "utf8");

    const newAbs = join(agentsDir, "petbox-worker.md");
    const writeOutcome = writeArtifact(newAbs, content);
    assert.equal(writeOutcome.kind, "written");

    // Legacy cleanup targets the bare name — but it is NOT ours (no marker), so it must survive.
    const legacyOutcome = cleanupLegacyArtifact(userFile);
    assert.equal(legacyOutcome, "kept-foreign");
    assert.equal(readFileSync(userFile, "utf8"), userContent, "user's own file must be untouched");
    assert.equal(readFileSync(newAbs, "utf8"), content);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
