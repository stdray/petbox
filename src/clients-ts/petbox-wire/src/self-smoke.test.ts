// Unit tests for self-smoke classification and the final-line policy
// (bug: selfsmoke-failure-prints-done — a failed self-smoke must never be followed by "done.").
//
// Run: node --test src/self-smoke.test.ts

import assert from "node:assert/strict";
import { test } from "node:test";
import { classifySelfSmokeResponse, finishWireRun } from "./self-smoke.ts";

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

test("finishWireRun: successful smoke + env var already present prints exactly 'done.'", () => {
  const f = finishWireRun({
    smokeOk: true,
    envVar: "PETBOX_X_API_KEY",
    envVarPresentInProcess: true,
    platform: "linux",
  });
  assert.equal(f.printDone, true);
  assert.equal(f.toStderr, false);
  assert.deepEqual(f.lines, ["done."]);
});

test("finishWireRun: successful smoke without the env var in-process adds the new-terminal NOTE, still to stdout", () => {
  const f = finishWireRun({
    smokeOk: true,
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
    envVar: "PETBOX_X_API_KEY",
    envVarPresentInProcess: false,
    platform: "linux",
  });
  assert.equal(f.lines.length, 1);
  const [line] = f.lines;
  assert.ok(line, "finishWireRun must produce exactly one line here");
  assert.match(line, /login shell/);
});
