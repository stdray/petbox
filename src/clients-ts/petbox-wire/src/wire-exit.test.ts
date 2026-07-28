// Exit taxonomy: every class stays distinct, and the PRIORITY between them is pinned here rather
// than left to the order of if-statements in wire.ts (CI signal).
//
// Run: node --test src/wire-exit.test.ts

import assert from "node:assert/strict";
import { test } from "node:test";
import { abortRun, classifyApplyExit, RunAbort, strongestExitCode, WIRE_EXIT } from "./wire-exit.ts";

test("WIRE_EXIT taxonomy is distinct", () => {
  assert.equal(WIRE_EXIT.ok, 0);
  assert.equal(WIRE_EXIT.hard, 1);
  assert.equal(WIRE_EXIT.usage, 2);
  assert.equal(WIRE_EXIT.truthfulness, 3);
  assert.equal(WIRE_EXIT.incomplete, 4);
  const codes = new Set(Object.values(WIRE_EXIT));
  assert.equal(codes.size, 5, "each exit class must have a unique code");
});

test("incomplete (4) is its OWN code — never folded into truthfulness (3)", () => {
  // wire-exit-incomplete-is-invisible-to-automation, the owner's explicit decision: 3 means
  // "policy blocked this on purpose". A step that failed for a reason outside the user's control
  // is not policy, and merging the two would make 3 mean two things — the exact disease this
  // taxonomy exists to prevent. Locked so a later "simplification" cannot quietly re-merge them.
  assert.notEqual(WIRE_EXIT.incomplete, WIRE_EXIT.truthfulness);
  assert.notEqual(WIRE_EXIT.incomplete, WIRE_EXIT.hard);
  assert.notEqual(WIRE_EXIT.incomplete, WIRE_EXIT.usage);
  assert.notEqual(WIRE_EXIT.incomplete, WIRE_EXIT.ok);
});

test("classifyApplyExit: usage errors are NOT truthfulness (different codes)", () => {
  // Bad flags go through usage() → WIRE_EXIT.usage (2), never classifyApplyExit.
  // This test locks the contract that truthfulness is 3 and usage is 2.
  assert.notEqual(WIRE_EXIT.usage, WIRE_EXIT.truthfulness);
  assert.equal(classifyApplyExit({ hadTruthfulnessBlock: true }), WIRE_EXIT.truthfulness);
  assert.equal(classifyApplyExit({ hardError: true }), WIRE_EXIT.hard);
  assert.equal(classifyApplyExit({ hardError: true, hadTruthfulnessBlock: true }), WIRE_EXIT.hard);
  assert.equal(classifyApplyExit({}), WIRE_EXIT.ok);
  assert.equal(classifyApplyExit({ hadTruthfulnessBlock: false }), WIRE_EXIT.ok);
});

test("classifyApplyExit PRIORITY is decided, not incidental: hard (1) > truthfulness (3) > incomplete (4) > ok (0)", () => {
  // The card's explicit requirement: when several conditions hold at once the winner must be a
  // decision pinned by a test, not whatever order the if-statements happen to sit in. A refusal
  // to write and a policy block are both statements about what the run REFUSED to do; "a step
  // did not get to run" is the weaker claim and yields to both. It is never lost — both failure
  // branches still print the skip inside their `summary` JSON (asserted in apply-skills-skip).
  assert.equal(classifyApplyExit({ unintendedIncomplete: true }), WIRE_EXIT.incomplete);

  assert.equal(
    classifyApplyExit({ hardError: true, unintendedIncomplete: true }),
    WIRE_EXIT.hard,
    "a clobber refusal outranks an incomplete run",
  );
  assert.equal(
    classifyApplyExit({ hadTruthfulnessBlock: true, unintendedIncomplete: true }),
    WIRE_EXIT.truthfulness,
    "a policy block outranks an incomplete run",
  );
  assert.equal(
    classifyApplyExit({ hardError: true, hadTruthfulnessBlock: true, unintendedIncomplete: true }),
    WIRE_EXIT.hard,
    "all three at once: hard failure still wins",
  );

  // And the carve-out that keeps the new code trustworthy: an INTENTIONAL skip is not "incomplete"
  // at all, so it never reaches this classifier as unintendedIncomplete.
  assert.equal(classifyApplyExit({ unintendedIncomplete: false }), WIRE_EXIT.ok);
});

test("usage code is 2 (convention) and truthfulness block is 3", () => {
  // Simulated outcomes a CI script would branch on:
  const usageTypo = WIRE_EXIT.usage; // e.g. `petbox-wire apply --definiton` → usage()
  const policyBlock = classifyApplyExit({ hadTruthfulnessBlock: true });
  assert.equal(usageTypo, 2);
  assert.equal(policyBlock, 3);
  assert.notEqual(usageTypo, policyBlock);
});

test("doctor and apply share truthfulness exit 3 (not hard 1)", () => {
  // Both tools call classifyApplyExit({ hadTruthfulnessBlock: true }) for policy fails.
  assert.equal(classifyApplyExit({ hadTruthfulnessBlock: true }), WIRE_EXIT.truthfulness);
  assert.notEqual(WIRE_EXIT.truthfulness, WIRE_EXIT.hard);
  assert.notEqual(WIRE_EXIT.truthfulness, WIRE_EXIT.usage);
});

// ---- strongestExitCode (full-wire-exit-ignores-step-11) --------------------
//
// classifyApplyExit ranks CONDITIONS inside one apply pass. strongestExitCode ranks the finished
// CODES of several steps that each failed without aborting the run (full `wire`: step 10's
// self-smoke and step 11's apply). Same ladder, different input — and the reason it exists is that
// the alternative, a bare `process.exitCode = …` per step, makes the answer depend on which step
// assigned last.

test("strongestExitCode: the ladder is 1 > 3 > 4 > 0, the SAME one classifyApplyExit uses", () => {
  assert.equal(strongestExitCode(WIRE_EXIT.hard, WIRE_EXIT.truthfulness), WIRE_EXIT.hard);
  assert.equal(strongestExitCode(WIRE_EXIT.hard, WIRE_EXIT.incomplete), WIRE_EXIT.hard);
  assert.equal(strongestExitCode(WIRE_EXIT.truthfulness, WIRE_EXIT.incomplete), WIRE_EXIT.truthfulness);
  assert.equal(strongestExitCode(WIRE_EXIT.incomplete, WIRE_EXIT.ok), WIRE_EXIT.incomplete);
  assert.equal(
    strongestExitCode(WIRE_EXIT.hard, WIRE_EXIT.truthfulness, WIRE_EXIT.incomplete, WIRE_EXIT.ok),
    WIRE_EXIT.hard,
  );
});

test("strongestExitCode: ORDER of the arguments never decides the winner (the actual bug)", () => {
  // THE regression this function exists for. Full `wire`: step 10 sets 1, step 11 then returns 4.
  // A plain assignment reports whatever came last — i.e. it would downgrade a hard failure to
  // "incomplete" purely because step 11 runs after step 10.
  assert.equal(strongestExitCode(WIRE_EXIT.hard, WIRE_EXIT.incomplete), WIRE_EXIT.hard);
  assert.equal(strongestExitCode(WIRE_EXIT.incomplete, WIRE_EXIT.hard), WIRE_EXIT.hard);
  assert.equal(strongestExitCode(WIRE_EXIT.ok, WIRE_EXIT.truthfulness), WIRE_EXIT.truthfulness);
  assert.equal(strongestExitCode(WIRE_EXIT.truthfulness, WIRE_EXIT.ok), WIRE_EXIT.truthfulness);
});

test("strongestExitCode: a success is never reported over a failure, and 0 needs unanimity", () => {
  assert.equal(strongestExitCode(), WIRE_EXIT.ok, "no steps reported anything → clean");
  assert.equal(strongestExitCode(WIRE_EXIT.ok, WIRE_EXIT.ok, WIRE_EXIT.ok), WIRE_EXIT.ok);
  for (const code of [WIRE_EXIT.hard, WIRE_EXIT.usage, WIRE_EXIT.truthfulness, WIRE_EXIT.incomplete]) {
    assert.equal(
      strongestExitCode(WIRE_EXIT.ok, code, WIRE_EXIT.ok),
      code,
      `a single non-zero (${code}) among successes must survive`,
    );
  }
});

test("strongestExitCode: never DOWNGRADES — the result is at least as strong as every input", () => {
  // The invariant that makes it safe for wire.ts to assign the result unconditionally over a
  // process.exitCode an earlier step already set.
  const codes = Object.values(WIRE_EXIT);
  for (const a of codes) {
    for (const b of codes) {
      const winner = strongestExitCode(a, b);
      assert.ok(winner === a || winner === b, `${winner} must be one of the inputs (${a}, ${b})`);
      if (a !== WIRE_EXIT.ok || b !== WIRE_EXIT.ok) {
        assert.notEqual(winner, WIRE_EXIT.ok, `strongest(${a}, ${b}) must not report success`);
      }
      assert.equal(winner, strongestExitCode(b, a), `strongest(${a}, ${b}) must be commutative`);
    }
  }
});

test("strongestExitCode: an UNKNOWN non-zero code outranks every known one (never silently downgraded)", () => {
  // A code outside the taxonomy cannot be ranked, so it is treated as at least as serious as a
  // hard failure. Losing to a weaker known code is the one outcome this function must not produce.
  const unknown = 77;
  assert.equal(strongestExitCode(WIRE_EXIT.hard, unknown), unknown);
  assert.equal(strongestExitCode(unknown, WIRE_EXIT.hard), unknown);
  assert.equal(strongestExitCode(WIRE_EXIT.ok, unknown), unknown);
});

test("abortRun aborts control flow (like process.exit did) and carries its code", () => {
  // The property that makes converting a hard exit to abortRun safe: it must NOT be a
  // fall-through. If it ever returned, every converted call site would start running code the
  // hard exit used to cut off — the main risk this whole change carries.
  let reachedAfterAbort = false;
  try {
    abortRun(WIRE_EXIT.hard, "boom");
    reachedAfterAbort = true;
  } catch (e) {
    assert.ok(e instanceof RunAbort, "abortRun must throw RunAbort so the entrypoint can tell it from a crash");
    assert.equal((e as RunAbort).code, WIRE_EXIT.hard);
    assert.equal((e as RunAbort).message, "boom");
  }
  assert.equal(reachedAfterAbort, false, "control must never continue past abortRun");
});
