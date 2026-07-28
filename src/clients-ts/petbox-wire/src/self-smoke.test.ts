// Unit tests for self-smoke classification and the final-line policy
// (bug: selfsmoke-failure-prints-done — a failed self-smoke must never be followed by "done.").
//
// Run: node --test src/self-smoke.test.ts

import assert from "node:assert/strict";
import { test } from "node:test";
import { classifySelfSmokeResponse, finishWireRun } from "./self-smoke.ts";
import { WIRE_EXIT } from "./wire-exit.ts";

// ---- classifySelfSmokeResponse ----

test("classifySelfSmokeResponse: non-OK HTTP status is a failure", () => {
  const r = classifySelfSmokeResponse(false, 500, "internal error");
  assert.equal(r.ok, false);
  assert.match(r.message, /HTTP 500/);
  assert.match(r.message, /internal error/);
});

test("classifySelfSmokeResponse: 200 with a numeric version is success", () => {
  const r = classifySelfSmokeResponse(
    true,
    200,
    JSON.stringify({ sessionId: "s1", version: 3, messageCount: 1 }),
  );
  assert.equal(r.ok, true);
  assert.match(r.message, /OK/);
  assert.match(r.message, /sessionId=s1/);
  assert.match(r.message, /version=3/);
});

test("classifySelfSmokeResponse: 200 with non-JSON body is a failure", () => {
  const r = classifySelfSmokeResponse(true, 200, "not json");
  assert.equal(r.ok, false);
  assert.match(r.message, /did not return a numeric version/);
});

test("classifySelfSmokeResponse: 200 with JSON but no numeric version is a failure", () => {
  const r = classifySelfSmokeResponse(true, 200, JSON.stringify({ sessionId: "s1" }));
  assert.equal(r.ok, false);
  assert.match(r.message, /did not return a numeric version/);
});

// ---- finishWireRun ----

test("finishWireRun: failed smoke suppresses 'done.' entirely and goes to stderr", () => {
  const f = finishWireRun({
    smokeOk: false,
    applyCode: WIRE_EXIT.ok,
    envVar: "PETBOX_X_API_KEY",
    envVarPresentInProcess: true,
    platform: "linux",
  });
  assert.equal(f.printDone, false);
  assert.equal(f.toStderr, true);
  assert.ok(f.lines.length > 0);
  for (const line of f.lines) {
    assert.doesNotMatch(line, /^done\.?/, "no line may read like the success banner");
  }
  // The literal regression this bug reported: "done." must not appear anywhere in the failure output.
  assert.ok(!f.lines.join("\n").includes("done."));
});

test("finishWireRun: successful smoke + env var already present in this process STILL prints the new-terminal NOTE (idempotent — the NOTE is about other/future terminals, not this process)", () => {
  const f = finishWireRun({
    smokeOk: true,
    applyCode: WIRE_EXIT.ok,
    envVar: "PETBOX_X_API_KEY",
    envVarPresentInProcess: true,
    platform: "linux",
  });
  assert.equal(f.printDone, true);
  assert.equal(f.toStderr, false);
  assert.equal(f.lines.length, 1);
  const [line] = f.lines;
  assert.ok(line, "finishWireRun must produce exactly one line here");
  assert.match(line, /^done\. NOTE:/);
});

test("finishWireRun: successful smoke without the env var in-process adds the new-terminal NOTE, still to stdout", () => {
  const f = finishWireRun({
    smokeOk: true,
    applyCode: WIRE_EXIT.ok,
    envVar: "PETBOX_X_API_KEY",
    envVarPresentInProcess: false,
    platform: "win32",
  });
  assert.equal(f.printDone, true);
  assert.equal(f.toStderr, false);
  assert.equal(f.lines.length, 1);
  const [line] = f.lines;
  assert.ok(line, "finishWireRun must produce exactly one line here");
  assert.match(line, /^done\. NOTE:/);
  assert.match(line, /PETBOX_X_API_KEY/);
  // win32 branch omits "(login shell)"
  assert.doesNotMatch(line, /login shell/);
});

test("finishWireRun: POSIX platform's NOTE mentions the login shell", () => {
  const f = finishWireRun({
    smokeOk: true,
    applyCode: WIRE_EXIT.ok,
    envVar: "PETBOX_X_API_KEY",
    envVarPresentInProcess: false,
    platform: "linux",
  });
  assert.equal(f.lines.length, 1);
  const [line] = f.lines;
  assert.ok(line, "finishWireRun must produce exactly one line here");
  assert.match(line, /login shell/);
});

// ---- finishWireRun × step 11 (full-wire-exit-ignores-step-11) ----
//
// Step 11 (apply) is the OTHER step that fails without aborting the run, so it is the other way a
// non-zero run could still sign off with "done." — the exact shape of selfsmoke-failure-prints-done.

test("finishWireRun: a passing smoke with a FAILED step 11 still suppresses 'done.'", () => {
  const f = finishWireRun({
    smokeOk: true,
    applyCode: WIRE_EXIT.incomplete,
    envVar: "PETBOX_X_API_KEY",
    envVarPresentInProcess: true,
    platform: "linux",
  });
  assert.equal(f.printDone, false, "the run exits non-zero — it is not 'done.'");
  assert.equal(f.toStderr, true, "a non-zero outcome belongs on stderr with the other failures");
  assert.ok(!f.lines.join("\n").includes("done."));
  assert.match(f.lines.join("\n"), /step 11/);
  assert.match(f.lines.join("\n"), new RegExp(`exit ${WIRE_EXIT.incomplete}`));
  assert.match(f.lines.join("\n"), /petbox-wire apply/, "must name the command that retries it");
});

test("finishWireRun: step 11's exit code is REPORTED, not just flagged (1 vs 3 vs 4 are different problems)", () => {
  for (const code of [WIRE_EXIT.hard, WIRE_EXIT.truthfulness, WIRE_EXIT.incomplete]) {
    const f = finishWireRun({
      smokeOk: true,
      applyCode: code,
      envVar: "PETBOX_X_API_KEY",
      envVarPresentInProcess: true,
      platform: "linux",
    });
    assert.equal(f.printDone, false, `apply exit ${code} must suppress "done."`);
    assert.match(
      f.lines.join("\n"),
      new RegExp(`exit ${code}`),
      `the final message must name the actual code (${code}), not a generic "failed"`,
    );
  }
});

test("finishWireRun: BOTH failed — both are reported, and the last line is still a failure", () => {
  const f = finishWireRun({
    smokeOk: false,
    applyCode: WIRE_EXIT.hard,
    envVar: "PETBOX_X_API_KEY",
    envVarPresentInProcess: true,
    platform: "linux",
  });
  assert.equal(f.printDone, false);
  assert.equal(f.toStderr, true);
  assert.equal(f.lines.length, 2, "two different things are wrong; neither may be swallowed");
  assert.match(f.lines[0]!, /self-smoke FAILED/, "chronological: step 10 first");
  assert.match(f.lines[1]!, /step 11/, "…then step 11 — so the LAST line is still a failure");
  assert.ok(!f.lines.join("\n").includes("done."));
});

test("finishWireRun: neither failed — the success banner is unchanged (no new false alarm)", () => {
  // The other half of the contract: this fix must not turn clean runs into scary ones.
  const f = finishWireRun({
    smokeOk: true,
    applyCode: WIRE_EXIT.ok,
    envVar: "PETBOX_X_API_KEY",
    envVarPresentInProcess: true,
    platform: "linux",
  });
  assert.equal(f.printDone, true);
  assert.equal(f.toStderr, false);
  assert.equal(f.lines.length, 1);
  assert.match(f.lines[0]!, /^done\. NOTE:/);
  assert.doesNotMatch(f.lines[0]!, /step 11/);
});
