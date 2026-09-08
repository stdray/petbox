// Tests for the codex PreToolUse model-pin gate (codex-subagent-model-gate.ts) — the codex port
// of subagent-model-gate.ts. Same structure as subagent-model-gate.test.ts: pure decision tests
// against evaluateCodexModelGate (which must reuse evaluateModelGate's policy unchanged, just
// translated field names — see the file's own header), plus a process-level spawn test proving
// the real stdin -> stdout -> exit-code contract.
//
// Run: node --test src/codex-subagent-model-gate.test.ts

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { dirname, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { evaluateCodexModelGate } from "./codex-subagent-model-gate.ts";

const HERE = dirname(fileURLToPath(import.meta.url));
const SCRIPT = join(HERE, "codex-subagent-model-gate.ts");

// ---- (a) petbox-* + model → block, via codex's agent_type field -------------------------------

test("agent_type=petbox-worker + model → blocked, with a message telling the caller to remove `model`", () => {
  const decision = evaluateCodexModelGate({
    tool_name: "spawn_agent",
    tool_input: { agent_type: "petbox-worker", model: "deepseek-v4-pro", message: "m", task_name: "t" },
  });
  assert.equal(decision.blocked, true);
  assert.match((decision as { reason: string }).reason, /petbox-worker/);
  assert.match((decision as { reason: string }).reason, /`model`/);
});

test("any petbox-* role + model is blocked, not just petbox-worker", () => {
  for (const role of ["petbox-orchestrator", "petbox-explore", "petbox-worker-highstakes", "petbox-reserve"]) {
    const decision = evaluateCodexModelGate({
      tool_name: "spawn_agent",
      tool_input: { agent_type: role, model: "grok-4.6" },
    });
    assert.equal(decision.blocked, true, `expected ${role} + model to be blocked`);
  }
});

// ---- (b) petbox-* without model → pass ---------------------------------------------------------

test("agent_type=petbox-worker without model (optional field absent) → pass", () => {
  const decision = evaluateCodexModelGate({
    tool_name: "spawn_agent",
    tool_input: { agent_type: "petbox-worker", message: "m", task_name: "t" },
  });
  assert.equal(decision.blocked, false);
});

test("agent_type=petbox-worker + an empty/whitespace-only model string → pass", () => {
  assert.equal(
    evaluateCodexModelGate({ tool_input: { agent_type: "petbox-worker", model: "" } }).blocked,
    false,
  );
  assert.equal(
    evaluateCodexModelGate({ tool_input: { agent_type: "petbox-worker", model: "   " } }).blocked,
    false,
  );
});

// ---- (c) a non-petbox agent_type + model → pass (same rejected branch as native CC types) -----

test("agent_type=researcher + model → pass — non-petbox roles are never gated here", () => {
  const decision = evaluateCodexModelGate({
    tool_name: "spawn_agent",
    tool_input: { agent_type: "researcher", model: "deepseek-v4-pro" },
  });
  assert.equal(decision.blocked, false);
});

// ---- (d) garbage input → pass, never throws ----------------------------------------------------

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
    { tool_input: { agent_type: 123, model: "deepseek-v4-pro" } }, // wrong type
    { tool_input: { agent_type: "petbox-worker", model: 123 } }, // model wrong type
    { tool_name: "shell", tool_input: { command: "ls" } }, // an unrelated tool call
  ];
  for (const input of inputs) {
    assert.doesNotThrow(() => evaluateCodexModelGate(input));
    assert.equal(evaluateCodexModelGate(input).blocked, false);
  }
});

test("an agent_type that merely CONTAINS petbox- (not a prefix) does not match", () => {
  const decision = evaluateCodexModelGate({
    tool_input: { agent_type: "not-petbox-worker", model: "deepseek-v4-pro" },
  });
  assert.equal(decision.blocked, false);
});

// ---- the top-level `model` (current turn's model, NOT the spawn request) must be ignored -------

test("a top-level `model` sibling to tool_input is ignored — only tool_input.model matters (codex-spec.md §3)", () => {
  const decision = evaluateCodexModelGate({
    model: "deepseek-v4-pro", // hook_runtime.rs: this is the CURRENT TURN's model, not the spawn request
    tool_input: { agent_type: "petbox-worker" },
  });
  assert.equal(decision.blocked, false);
});

// ---- process-level: the real stdin/stdout/exit-code contract -----------------------------------

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

test("process: agent_type=petbox-worker + model on real stdin → exit 0, deny JSON on stdout", async () => {
  const input = JSON.stringify({
    hook_event_name: "PreToolUse",
    tool_name: "spawn_agent",
    tool_input: { agent_type: "petbox-worker", model: "deepseek-v4-pro" },
  });
  const result = await runHook(input);
  assert.equal(result.code, 0);
  const parsed = JSON.parse(result.stdout);
  assert.equal(parsed.hookSpecificOutput.hookEventName, "PreToolUse");
  assert.equal(parsed.hookSpecificOutput.permissionDecision, "deny");
  assert.match(parsed.hookSpecificOutput.permissionDecisionReason, /petbox-worker/);
});

test("process: agent_type=petbox-worker without model on real stdin → exit 0, empty stdout (silent pass)", async () => {
  const input = JSON.stringify({
    hook_event_name: "PreToolUse",
    tool_name: "spawn_agent",
    tool_input: { agent_type: "petbox-worker", message: "m", task_name: "t" },
  });
  const result = await runHook(input);
  assert.equal(result.code, 0);
  assert.equal(result.stdout, "");
});

test("process: non-petbox agent_type + model on real stdin → exit 0, empty stdout", async () => {
  const input = JSON.stringify({
    hook_event_name: "PreToolUse",
    tool_name: "spawn_agent",
    tool_input: { agent_type: "researcher", model: "deepseek-v4-pro" },
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
