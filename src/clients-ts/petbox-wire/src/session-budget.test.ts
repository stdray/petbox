// Unit + regression coverage for the SessionStart-hook byte budget (session-budget.ts).
//
// The hard edge (HARNESS_INLINE_HARD_LIMIT_BYTES = 10 000) is a MEASURED fact about
// claude-code 2.1.209, not something a unit test can re-derive — that measurement lives in
// session-budget.ts's module comment and the work node startup-banner-truncated-86-percent.
// What this file protects against is regression on our SIDE of that edge: assembleSessionBanner
// must never hand the harness a byte stream that risks its own truncation, and the mandatory
// protocol block (buildProtocol's output) must never grow past the budget on its own — if it
// does, canon has nowhere left to go and the whole point of this module is defeated.
//
// Run: node --test src/session-budget.test.ts

import assert from "node:assert/strict";
import { test } from "node:test";
import { DEFAULT_AGENT_DEFINITION } from "./agent-definition.ts";
import { CANON_PROJECT_SECTION_MARKER, CANON_WORKSPACE_SECTION_MARKER } from "./canon.ts";
import { buildProtocol, mcpPetboxTool } from "./protocol.ts";
import {
  assembleSessionBanner,
  describeCanonDegradation,
  HARNESS_INLINE_HARD_LIMIT_BYTES,
  SESSION_BANNER_BUDGET_BYTES,
} from "./session-budget.ts";

test("assembleSessionBanner: no canon — ships the protocol block as-is, not over budget", () => {
  const protocol = "## PetBox memory\n\nshort protocol block";
  const result = assembleSessionBanner(protocol, null);
  assert.equal(result.text, protocol);
  assert.equal(result.canonIncluded, false);
  assert.equal(result.canonBytes, 0);
  assert.equal(result.overBudget, false);
});

test("assembleSessionBanner: protocol+canon fit — both included, not over budget", () => {
  const protocol = "A".repeat(3000);
  const canon = "B".repeat(2000);
  const result = assembleSessionBanner(protocol, canon, 8000);
  assert.equal(result.canonIncluded, true);
  assert.ok(result.text.includes(protocol));
  assert.ok(result.text.includes(canon));
  assert.equal(result.overBudget, false);
  assert.equal(result.totalBytes, Buffer.byteLength(result.text, "utf8"));
});

test("assembleSessionBanner: canon alone would blow the budget — DROPPED, protocol survives intact, overBudget flagged", () => {
  const protocol = "## PetBox memory\n\nRULE 7: the agent ceiling is Review.";
  const canon = "C".repeat(50_000); // stand-in for an oversized real canon (measured: 9347B today)
  const result = assembleSessionBanner(protocol, canon, SESSION_BANNER_BUDGET_BYTES);
  assert.equal(result.canonIncluded, false, "an oversized canon must be dropped, not truncated in place");
  assert.equal(result.text, protocol, "the mandatory protocol block must survive byte-for-byte");
  assert.ok(result.text.includes("RULE 7"), "the gate rule must never be a casualty of canon size");
  assert.equal(result.overBudget, true);
  assert.equal(result.canonBytes, Buffer.byteLength(canon, "utf8"));
});

// --- canon-degrade-by-legs-not-all-or-nothing ---
//
// The canon block carries two independent legs. Before this, ONE byte of overage cost the agent
// BOTH — the project canon (the specific, expensive-to-re-derive one) went out with the
// workspace canon in a single jump. The ladder is: whole block → project leg only → nothing.
//
// The section markers come from canon.ts rather than being retyped here on purpose: the cut is
// only correct while it uses the same text the renderer wrote, and a rename must break the
// renderer and the cut together, not silently turn the cut into a no-op.

function canonBlock(projectBody: string, workspaceBody: string | null): string {
  let out = "## PetBox memory canon\n\nThe curated memory index (canon) for this project.";
  out += `${CANON_PROJECT_SECTION_MARKER}demo)\n\n${projectBody}`;
  if (workspaceBody !== null) out += `${CANON_WORKSPACE_SECTION_MARKER}\n\n${workspaceBody}`;
  return out;
}

test("assembleSessionBanner: over budget by a little — the WORKSPACE leg is shed and the project leg survives", () => {
  const protocol = "P".repeat(4000);
  const projectBody = "J".repeat(1000);
  const whole = canonBlock(projectBody, "W".repeat(3000));
  const projectOnly = canonBlock(projectBody, null);
  // Exactly enough room for protocol + join + the project leg, and not one byte more.
  const budget = 4000 + 2 + Buffer.byteLength(projectOnly, "utf8");

  const result = assembleSessionBanner(protocol, whole, budget);
  assert.equal(result.canonLegs, "project-only");
  assert.equal(result.canonIncluded, true, "the project leg must survive an overage the workspace leg caused");
  assert.ok(result.text.startsWith(protocol), "the mandatory protocol block still leads, byte-for-byte");
  assert.ok(result.text.includes(projectBody), "project canon must still be in the shipped banner");
  assert.ok(!result.text.includes(CANON_WORKSPACE_SECTION_MARKER), "the workspace section must be gone");
  assert.equal(result.canonBytes, Buffer.byteLength(whole, "utf8"), "canonBytes still reports what was CONSIDERED");
  assert.equal(result.canonIncludedBytes, Buffer.byteLength(projectOnly, "utf8"));
  assert.equal(result.totalBytes, Buffer.byteLength(result.text, "utf8"));
  assert.ok(result.totalBytes <= budget);
  assert.equal(result.overBudget, true, "a degraded banner is still a reportable overage");
});

test("assembleSessionBanner: shedding the workspace leg is not enough — both legs go, protocol intact", () => {
  const protocol = "P".repeat(4000);
  const projectOnly = canonBlock("J".repeat(1000), null);
  const budget = 4000 + 2 + Buffer.byteLength(projectOnly, "utf8") - 1; // one byte short of the project leg

  const result = assembleSessionBanner(protocol, canonBlock("J".repeat(1000), "W".repeat(3000)), budget);
  assert.equal(result.canonLegs, "none");
  assert.equal(result.canonIncluded, false);
  assert.equal(result.canonIncludedBytes, 0);
  assert.equal(result.text, protocol, "the mandatory protocol block must survive byte-for-byte");
  assert.equal(result.overBudget, true);
});

test("assembleSessionBanner: a workspace-ONLY canon has no intermediate rung — it is dropped whole, never reduced to a bare heading", () => {
  const protocol = "P".repeat(4000);
  const workspaceOnly = `## PetBox memory canon${CANON_WORKSPACE_SECTION_MARKER}\n\n${"W".repeat(5000)}`;
  const result = assembleSessionBanner(protocol, workspaceOnly, 5000);
  assert.equal(result.canonLegs, "none");
  assert.equal(result.text, protocol, "shipping a lone '## PetBox memory canon' heading would be noise, not context");
});

test("assembleSessionBanner: a canon with no workspace leg at all reports 'none', never a phantom shed leg", () => {
  const protocol = "P".repeat(4000);
  const result = assembleSessionBanner(protocol, canonBlock("J".repeat(6000), null), 5000);
  assert.equal(result.canonLegs, "none");
  assert.equal(result.canonIncluded, false);
});

test("assembleSessionBanner: a fitting two-leg canon is untouched — the ladder only runs on an overage", () => {
  const protocol = "P".repeat(1000);
  const whole = canonBlock("J".repeat(500), "W".repeat(500));
  const result = assembleSessionBanner(protocol, whole, SESSION_BANNER_BUDGET_BYTES);
  assert.equal(result.canonLegs, "both");
  assert.equal(result.overBudget, false);
  assert.ok(result.text.includes(CANON_WORKSPACE_SECTION_MARKER));
  assert.equal(result.canonIncludedBytes, Buffer.byteLength(whole, "utf8"));
});

// --- owner-only-skills block woven into the ladder (bug:
// owner-only-skills-block-silently-dropped-in-real-project) ---
//
// Found by measuring the FINAL tree against the one real project the fix was for: in `$system`
// (Claude Code), protocol(compact) = 5207B, canon (real cache file) = 4058B whole / 2773B
// project-leg-only, the owner-only-skills block (3 real skills, unshrunk) = 1281B, and the
// stale-base warning (pull-memory.ts prepends it OUTSIDE this ladder's own budget accounting) =
// 317B on a parked branch. Appending the block unconditionally after an already-budget-fitting
// protocol+canon banner (9267B, ≤ the 9400B budget) pushed the shipped stdout to 10550B — over
// the harness's 10000B hard limit — on EVERY session in that project, silently (the drop was
// logged to ~/.petbox/wire.log, which nothing reads at session start). All the numbers below are
// those measured values, not round numbers, so a regression that changes the ladder's shape
// re-triggers this exact failure rather than a smaller one a round-number fixture would miss.
const REAL_PROTOCOL_COMPACT_BYTES = 5207;
const REAL_CANON_WHOLE_BYTES = 4058;
const REAL_CANON_PROJECT_ONLY_BYTES = 2773; // whole minus the ~1285B workspace leg
const REAL_OWNER_ONLY_SKILLS_BLOCK_BYTES = 1281; // 3 real skills, unshrunk (skill-files.test.ts pins the text)
const REAL_STALE_BASE_WARNING_BYTES = 317; // prepended by pull-memory.ts, OUTSIDE this ladder's budget

/** A canon fixture whose WHOLE and PROJECT-ONLY byte counts hit the two constants above exactly
 * (computed once by construction, not asserted after the fact) — `canonBlock` is the same helper
 * every other test in this file uses, so this fixture exercises the exact code path
 * `dropWorkspaceLeg` runs in production. */
function realScaleCanon(): string {
  const overheadNoWorkspace = Buffer.byteLength(canonBlock("", null), "utf8");
  const projectBody = "X".repeat(REAL_CANON_PROJECT_ONLY_BYTES - overheadNoWorkspace);
  const overheadWithEmptyWorkspace = Buffer.byteLength(canonBlock(projectBody, ""), "utf8");
  const workspaceBody = "Y".repeat(REAL_CANON_WHOLE_BYTES - overheadWithEmptyWorkspace);
  const whole = canonBlock(projectBody, workspaceBody);
  // Self-check: if a future edit to canonBlock's fixed prose changes its overhead, fail here
  // with a clear message rather than silently testing a different scale than intended.
  assert.equal(Buffer.byteLength(whole, "utf8"), REAL_CANON_WHOLE_BYTES);
  assert.equal(Buffer.byteLength(canonBlock(projectBody, null), "utf8"), REAL_CANON_PROJECT_ONLY_BYTES);
  return whole;
}

test("assembleSessionBanner: real $system scale — protocol(compact) + canon(4KB) + owner-only-skills block ALL survive, workspace leg sheds instead", () => {
  const protocol = "P".repeat(REAL_PROTOCOL_COMPACT_BYTES);
  const canon = realScaleCanon();
  const extra = "E".repeat(REAL_OWNER_ONLY_SKILLS_BLOCK_BYTES);

  // Rung 1 (whole canon + extra) does NOT fit — this is the failure as measured, reproduced here.
  const wholePlusExtraBytes = REAL_PROTOCOL_COMPACT_BYTES + 2 + REAL_CANON_WHOLE_BYTES + 2 + REAL_OWNER_ONLY_SKILLS_BLOCK_BYTES;
  assert.ok(wholePlusExtraBytes > SESSION_BANNER_BUDGET_BYTES, "precondition: this is the scale that broke in $system");

  const result = assembleSessionBanner(protocol, canon, SESSION_BANNER_BUDGET_BYTES, extra);

  // THE acceptance criterion: the block must still reach the agent, not be the casualty.
  assert.equal(result.extraIncluded, true, "the owner-only-skills block must survive at real $system scale");
  assert.ok(result.text.includes(extra), "the block's bytes must actually be IN the shipped text, not just flagged included");
  // It survives by the canon WORKSPACE leg shedding, not by the block being cut down or the
  // project leg (this project's own curated rules) being sacrificed instead.
  assert.equal(result.canonLegs, "project-only", "the workspace leg — not the block — is what pays for the room");
  assert.ok(result.text.includes("### Project ("), "the project leg's content must still be present");
  assert.ok(!result.text.includes("### Workspace"), "the workspace leg is what was shed");
  assert.ok(result.totalBytes <= SESSION_BANNER_BUDGET_BYTES, `${result.totalBytes}B must fit the ${SESSION_BANNER_BUDGET_BYTES}B budget`);

  // THE worst case named in the card: compact + a present stale-base warning. staleWarn is
  // prepended by pull-memory.ts OUTSIDE this ladder's own accounting (same treatment as defNote),
  // so the real acceptance test is on the FINAL shipped stdout, against the harness's hard limit.
  const finalStdoutBytes = REAL_STALE_BASE_WARNING_BYTES + result.totalBytes;
  assert.ok(
    finalStdoutBytes <= HARNESS_INLINE_HARD_LIMIT_BYTES,
    `worst case (compact + stale-base + real canon + owner-only-skills block) is ${finalStdoutBytes}B, ` +
      `over the harness's ${HARNESS_INLINE_HARD_LIMIT_BYTES}B hard limit — the exact scenario M1's probe 4 measures`,
  );
});

test("assembleSessionBanner: real $system scale — WITHOUT the ladder fix, the old unconditional-append behavior would have dropped the block (regression pin)", () => {
  // Reproduces the pre-fix arithmetic exactly, so a future refactor that reintroduces
  // "append extra after an already-assembled protocol+canon banner" fails HERE, in a unit test,
  // rather than silently in a real project's session start again.
  const protocol = "P".repeat(REAL_PROTOCOL_COMPACT_BYTES);
  const canon = realScaleCanon();
  const extra = "E".repeat(REAL_OWNER_ONLY_SKILLS_BLOCK_BYTES);

  const oldStyleBanner = assembleSessionBanner(protocol, canon, SESSION_BANNER_BUDGET_BYTES); // no `extra` arg — old call shape
  assert.equal(oldStyleBanner.canonLegs, "both", "old shape: canon fits fine on its own at this scale");
  const oldStyleWithUnconditionalAppend = `${oldStyleBanner.text}\n\n${extra}`;
  assert.ok(
    Buffer.byteLength(oldStyleWithUnconditionalAppend, "utf8") > HARNESS_INLINE_HARD_LIMIT_BYTES,
    "sanity: the old unconditional-append shape really did exceed the hard limit at this scale — proves the fix is load-bearing, not decorative",
  );
});

test("describeCanonDegradation NAMES the leg that was shed — the log line the old all-or-nothing path could not write", () => {
  const protocol = "P".repeat(4000);
  const projectOnly = canonBlock("J".repeat(1000), null);
  const budget = 4000 + 2 + Buffer.byteLength(projectOnly, "utf8");

  const degraded = assembleSessionBanner(protocol, canonBlock("J".repeat(1000), "W".repeat(3000)), budget);
  assert.match(describeCanonDegradation(degraded), /WORKSPACE LEG DROPPED, project leg KEPT \(\d+B of \d+B\)/);

  const gone = assembleSessionBanner(protocol, canonBlock("J".repeat(1000), "W".repeat(3000)), budget - 1);
  assert.match(describeCanonDegradation(gone), /DROPPED ENTIRELY, both legs/);

  const kept = assembleSessionBanner(protocol, canonBlock("J".repeat(10), "W".repeat(10)), SESSION_BANNER_BUDGET_BYTES);
  assert.match(describeCanonDegradation(kept), /^KEPT/);

  assert.match(describeCanonDegradation(assembleSessionBanner(protocol, null)), /not available at all/);
});

test("assembleSessionBanner: dropped banner always stays at or under the HARD harness limit", () => {
  // Regression guard for the exact bug this module exists to prevent: even in the degraded
  // (canon-dropped) case, what actually ships must never be large enough to trip the harness's
  // own preview-collapse — that collapse cuts by raw byte offset, not by section, so it could
  // still guillotine the protocol block's own tail if this invariant broke.
  const protocol = "P".repeat(SESSION_BANNER_BUDGET_BYTES - 500);
  const canon = "C".repeat(100_000);
  const result = assembleSessionBanner(protocol, canon);
  assert.equal(result.canonIncluded, false);
  assert.ok(
    result.totalBytes <= HARNESS_INLINE_HARD_LIMIT_BYTES,
    `dropped banner must stay under the harness's hard limit; got ${result.totalBytes}B`,
  );
});

test("assembleSessionBanner: protocol alone already over budget — overBudget flagged even with no canon to blame", () => {
  const protocol = "X".repeat(SESSION_BANNER_BUDGET_BYTES + 1);
  const result = assembleSessionBanner(protocol, null);
  assert.equal(result.overBudget, true);
  assert.equal(result.text, protocol, "still ships best-effort — nothing else to cut");
});

// Regression guard: the MANDATORY protocol block (self-intro, gates 1-7 via the definition
// notes, search-before-rework, entry points) must stay comfortably under the session banner
// budget ON ITS OWN, for every combination this kit actually renders — canon has ZERO room to
// spare if this block alone already eats the budget. This is the test a future edit to
// DEFAULT_AGENT_DEFINITION's orchestrator notes (or to protocol.ts's static prose) must not be
// able to silently break.
test("buildProtocol output (DEFAULT_AGENT_DEFINITION, orchestrator-capable harness, resume suffix) stays under the session banner budget", () => {
  const protocol = buildProtocol("test-project", mcpPetboxTool, {
    source: "resume", // worst case: adds the extra recall-nudge line
    harness: "claude-code", // worst case: orchestrator spawn prescriptions are the longest branch
    definition: DEFAULT_AGENT_DEFINITION,
  });
  const bytes = Buffer.byteLength(protocol, "utf8");
  assert.ok(
    bytes < SESSION_BANNER_BUDGET_BYTES,
    `mandatory protocol block is ${bytes}B, at/over the ${SESSION_BANNER_BUDGET_BYTES}B session banner budget — ` +
      `canon would have zero room, and this block itself risks the harness's ${HARNESS_INLINE_HARD_LIMIT_BYTES}B hard limit ` +
      `once combined with anything else. Trim protocol.ts's static prose or the definition's notes.`,
  );
  assert.ok(
    bytes < HARNESS_INLINE_HARD_LIMIT_BYTES,
    `mandatory protocol block is ${bytes}B — at/over the harness's own ${HARNESS_INLINE_HARD_LIMIT_BYTES}B hard limit; ` +
      `gates 1-7 would themselves be at risk of truncation with NO canon involved at all`,
  );
});
