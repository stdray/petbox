// Exit taxonomy: usage vs truthfulness must stay distinct (CI signal).
//
// Run: node --test src/wire-exit.test.ts

import assert from "node:assert/strict";
import { test } from "node:test";
import { classifyApplyExit, WIRE_EXIT } from "./wire-exit.ts";

test("WIRE_EXIT taxonomy is distinct", () => {
  assert.equal(WIRE_EXIT.ok, 0);
  assert.equal(WIRE_EXIT.hard, 1);
  assert.equal(WIRE_EXIT.usage, 2);
  assert.equal(WIRE_EXIT.truthfulness, 3);
  const codes = new Set(Object.values(WIRE_EXIT));
  assert.equal(codes.size, 4, "each exit class must have a unique code");
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

test("usage code is 2 (convention) and truthfulness block is 3", () => {
  // Simulated outcomes a CI script would branch on:
  const usageTypo = WIRE_EXIT.usage; // e.g. `petbox-wire apply --definiton` → usage()
  const policyBlock = classifyApplyExit({ hadTruthfulnessBlock: true });
  assert.equal(usageTypo, 2);
  assert.equal(policyBlock, 3);
  assert.notEqual(usageTypo, policyBlock);
});
