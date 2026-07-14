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
import { buildProtocol, mcpPetboxTool } from "./protocol.ts";
import {
  assembleSessionBanner,
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
