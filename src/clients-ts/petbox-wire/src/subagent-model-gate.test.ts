// Tests for the PreToolUse model-pin gate (subagent-model-gate.ts) — the kit's first PreToolUse
// hook. Pins exactly the one branch the card decided to build (subagent-model-enforcement-hook):
// petbox-* + explicit `model` → block; everything else (including every native subagent type,
// no matter what parameters it carries) → silent pass-through.
//
// Two layers, same as the rest of this kit's hook tests:
//   - unit tests against the pure evaluateModelGate() for the decision logic itself
//   - a process-level spawn test (like pull-memory.test.ts) proving the REAL stdin→stdout
//     contract a running Claude Code hook actually uses: JSON in on stdin, either nothing or a
//     hookSpecificOutput JSON blob on stdout, exit code always 0.
//
// Run: node --test src/subagent-model-gate.test.ts

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { dirname, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { evaluateModelGate } from "./subagent-model-gate.ts";

const HERE = dirname(fileURLToPath(import.meta.url));
const SCRIPT = join(HERE, "subagent-model-gate.ts");

// ---- (a) petbox-* + model → block ------------------------------------------------------------

test("petbox-worker + model → blocked, with a message telling the caller to remove `model`", () => {
  const decision = evaluateModelGate({
    tool_name: "Task",
    tool_input: { subagent_type: "petbox-worker", model: "sonnet", description: "d", prompt: "p" },
  });
  assert.equal(decision.blocked, true);
  assert.match((decision as { reason: string }).reason, /petbox-worker/);
  assert.match((decision as { reason: string }).reason, /`model`/);
});

test("any petbox-* role + model is blocked, not just petbox-worker", () => {
  for (const role of ["petbox-orchestrator", "petbox-explore", "petbox-utility", "petbox-reserve"]) {
    const decision = evaluateModelGate({
      tool_name: "Task",
      tool_input: { subagent_type: role, model: "opus" },
    });
    assert.equal(decision.blocked, true, `expected ${role} + model to be blocked`);
  }
});

// ---- (b) petbox-* without model → pass -------------------------------------------------------

test("petbox-worker without model → pass (no judgment needed, no pin conflict possible)", () => {
  const decision = evaluateModelGate({
    tool_name: "Task",
    tool_input: { subagent_type: "petbox-worker", description: "d", prompt: "p" },
  });
  assert.equal(decision.blocked, false);
});

test("petbox-worker + an empty/whitespace-only model string → pass (nothing was actually asked for)", () => {
  assert.equal(
    evaluateModelGate({ tool_input: { subagent_type: "petbox-worker", model: "" } }).blocked,
    false,
  );
  assert.equal(
    evaluateModelGate({ tool_input: { subagent_type: "petbox-worker", model: "   " } }).blocked,
    false,
  );
});

// ---- (c) native types with model → pass (decided branch: natives are never touched) ----------

test("general-purpose + model → pass — native types are the rejected branch, not a silent block", () => {
  const decision = evaluateModelGate({
    tool_name: "Task",
    tool_input: { subagent_type: "general-purpose", model: "opus" },
  });
  assert.equal(decision.blocked, false);
});

test("Explore / Plan + model → pass, same reasoning", () => {
  for (const type of ["Explore", "Plan"]) {
    const decision = evaluateModelGate({ tool_input: { subagent_type: type, model: "opus" } });
    assert.equal(decision.blocked, false, `expected native type ${type} to pass through`);
  }
});

// ---- (d) garbage input → pass, never throws ---------------------------------------------------

test("garbage/missing shapes never throw and always pass through", () => {
  const inputs: unknown[] = [
    undefined,
    null,
    "not an object",
    42,
    {},
    { tool_input: null },
    { tool_input: "also not an object" },
    { tool_input: {} },
    { tool_input: { subagent_type: 123, model: "sonnet" } }, // wrong type, not a string
    { tool_input: { subagent_type: "petbox-worker", model: 123 } }, // model wrong type
    { tool_name: "Bash", tool_input: { command: "ls" } }, // an unrelated tool call
  ];
  for (const input of inputs) {
    assert.doesNotThrow(() => evaluateModelGate(input));
    assert.equal(evaluateModelGate(input).blocked, false);
  }
});

test("a subagent_type that merely CONTAINS petbox- (not a prefix) does not match", () => {
  const decision = evaluateModelGate({
    tool_input: { subagent_type: "not-petbox-worker", model: "sonnet" },
  });
  assert.equal(decision.blocked, false);
});

// ---- process-level: the real stdin/stdout/exit-code contract ---------------------------------

type SpawnResult = { code: number | null; stdout: string; stderr: string };

function runHook(input: string): Promise<SpawnResult> {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [SCRIPT]);
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (c) => (stdout += c));
    child.stderr.on("data", (c) => (stderr += c));
    child.on("error", reject);
    child.on("close", (code) => resolve({ code, stdout, stderr }));
    child.stdin.write(input);
    child.stdin.end();
  });
}

test("process: petbox-worker + model on real stdin → exit 0, deny JSON on stdout", async () => {
  const input = JSON.stringify({
    hook_event_name: "PreToolUse",
    tool_name: "Task",
    tool_input: { subagent_type: "petbox-worker", model: "sonnet" },
  });
  const result = await runHook(input);
  assert.equal(result.code, 0);
  const parsed = JSON.parse(result.stdout);
  assert.equal(parsed.hookSpecificOutput.hookEventName, "PreToolUse");
  assert.equal(parsed.hookSpecificOutput.permissionDecision, "deny");
  assert.match(parsed.hookSpecificOutput.permissionDecisionReason, /petbox-worker/);
});

test("process: petbox-worker without model on real stdin → exit 0, empty stdout (silent pass)", async () => {
  const input = JSON.stringify({
    hook_event_name: "PreToolUse",
    tool_name: "Task",
    tool_input: { subagent_type: "petbox-worker", description: "d", prompt: "p" },
  });
  const result = await runHook(input);
  assert.equal(result.code, 0);
  assert.equal(result.stdout, "");
});

test("process: general-purpose + model on real stdin → exit 0, empty stdout (native untouched)", async () => {
  const input = JSON.stringify({
    hook_event_name: "PreToolUse",
    tool_name: "Task",
    tool_input: { subagent_type: "general-purpose", model: "opus" },
  });
  const result = await runHook(input);
  assert.equal(result.code, 0);
  assert.equal(result.stdout, "");
});

test("process: unparsable stdin → exit 0, empty stdout, no crash", async () => {
  const result = await runHook("{ not json at all ]]]");
  assert.equal(result.code, 0);
  assert.equal(result.stdout, "");
});

test("process: completely empty stdin → exit 0, empty stdout, no crash", async () => {
  const result = await runHook("");
  assert.equal(result.code, 0);
  assert.equal(result.stdout, "");
});
