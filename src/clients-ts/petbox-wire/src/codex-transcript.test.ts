// Unit tests for codex-transcript.ts — the codex port of transcript.ts/droid-transcript.ts.
// Rollout record shapes are exactly codex-spec.md §5's (source-anchored): `{timestamp, type,
// payload}` envelope; payload.type distinguishes message/function_call/function_call_output;
// `arguments` on a function_call is a RAW JSON STRING, not an object; non-response_item line
// types must be skipped, not crashed on.
//
// Run: node --test src/codex-transcript.test.ts

import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { buildCodexMessages, collectCodexSubagentRuns } from "./codex-transcript.ts";

function tmpFile(prefix: string): { dir: string; transcriptPath: string } {
  const dir = mkdtempSync(join(tmpdir(), prefix));
  return { dir, transcriptPath: join(dir, "rollout-test.jsonl") };
}

function ndjson(lines: unknown[]): string {
  return lines.map((l) => JSON.stringify(l)).join("\n") + "\n";
}

function responseItem(payload: unknown) {
  return { timestamp: "2026-09-08T00:00:00Z", type: "response_item", payload };
}

// ---- buildCodexMessages ------------------------------------------------------------------------

test("buildCodexMessages: user/assistant message turns are collected in order, text joined from input_text/output_text parts", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-codex-msgs-basic-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        { timestamp: "t0", type: "session_meta", payload: { id: "s1" } }, // skipped, not response_item
        responseItem({ type: "message", role: "user", content: [{ type: "input_text", text: "hello" }] }),
        responseItem({
          type: "message",
          role: "assistant",
          content: [{ type: "output_text", text: "hi there" }],
        }),
        { timestamp: "t1", type: "event_msg", payload: { type: "user_message", message: "hello" } }, // skipped
      ]),
    );
    const msgs = await buildCodexMessages(transcriptPath);
    assert.deepEqual(msgs, [
      { role: "user", content: "hello" },
      { role: "assistant", content: "hi there" },
    ]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("buildCodexMessages: function_call and function_call_output payloads are NOT text turns", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-codex-msgs-toolcalls-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        responseItem({ type: "message", role: "user", content: [{ type: "input_text", text: "do X" }] }),
        responseItem({
          type: "function_call",
          name: "spawn_agent",
          arguments: JSON.stringify({ agent_type: "petbox-worker", message: "go" }),
          call_id: "c1",
        }),
        responseItem({ type: "function_call_output", call_id: "c1", output: "done" }),
        responseItem({ type: "message", role: "assistant", content: [{ type: "output_text", text: "done!" }] }),
      ]),
    );
    const msgs = await buildCodexMessages(transcriptPath);
    assert.deepEqual(msgs, [
      { role: "user", content: "do X" },
      { role: "assistant", content: "done!" },
    ]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("buildCodexMessages: an unrecognized/malformed line type is skipped, never crashes", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-codex-msgs-malformed-");
  try {
    writeFileSync(
      transcriptPath,
      [
        "not even json",
        "",
        JSON.stringify({ timestamp: "t", type: "compacted", payload: {} }),
        JSON.stringify(responseItem({ type: "message", role: "user", content: [{ type: "input_text", text: "ok" }] })),
      ].join("\n") + "\n",
    );
    const msgs = await buildCodexMessages(transcriptPath);
    assert.deepEqual(msgs, [{ role: "user", content: "ok" }]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("buildCodexMessages: a turn with no text parts (thinking/empty content) is dropped, not emitted blank", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-codex-msgs-empty-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        responseItem({ type: "message", role: "assistant", content: [] }),
        responseItem({ type: "message", role: "user", content: [{ type: "input_text", text: "real" }] }),
      ]),
    );
    const msgs = await buildCodexMessages(transcriptPath);
    assert.deepEqual(msgs, [{ role: "user", content: "real" }]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ---- collectCodexSubagentRuns -------------------------------------------------------------------

test("collectCodexSubagentRuns: no spawn_agent calls → no runs", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-codex-runs-none-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([responseItem({ type: "message", role: "user", content: [{ type: "input_text", text: "hi" }] })]),
    );
    assert.deepEqual(await collectCodexSubagentRuns(transcriptPath), []);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("collectCodexSubagentRuns: spawn WITHOUT model → roster; role from agent_type; arguments parsed from its JSON STRING", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-codex-runs-roster-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        responseItem({
          type: "function_call",
          name: "spawn_agent",
          // arguments is a raw JSON STRING (codex-spec.md §5) — not an object literal here.
          arguments: JSON.stringify({ message: "go", task_name: "t1", agent_type: "petbox-worker" }),
          call_id: "c1",
        }),
      ]),
    );
    const runs = await collectCodexSubagentRuns(transcriptPath);
    assert.deepEqual(runs, [{ role: "petbox-worker", modelSource: "roster" }]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("collectCodexSubagentRuns: spawn WITH an explicit model → override; actualModel never set (no per-turn model telemetry documented)", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-codex-runs-override-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        responseItem({
          type: "function_call",
          name: "spawn_agent",
          arguments: JSON.stringify({
            message: "go",
            task_name: "t1",
            agent_type: "petbox-explore",
            model: "deepseek-v4-flash",
          }),
          call_id: "c1",
        }),
      ]),
    );
    const runs = await collectCodexSubagentRuns(transcriptPath);
    assert.deepEqual(runs, [
      { role: "petbox-explore", modelSource: "override", spawnModel: "deepseek-v4-flash" },
    ]);
    assert.equal("actualModel" in runs[0]!, false, "actualModel must never be invented");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("collectCodexSubagentRuns: a function_call that is NOT spawn_agent is ignored", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-codex-runs-othertool-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        responseItem({
          type: "function_call",
          name: "shell",
          arguments: JSON.stringify({ command: "ls" }),
          call_id: "c1",
        }),
      ]),
    );
    assert.deepEqual(await collectCodexSubagentRuns(transcriptPath), []);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("collectCodexSubagentRuns: malformed arguments (not JSON, or missing agent_type) never throws, just skips", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-codex-runs-malformed-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        responseItem({ type: "function_call", name: "spawn_agent", arguments: "{ not json", call_id: "c1" }),
        responseItem({
          type: "function_call",
          name: "spawn_agent",
          arguments: JSON.stringify({ message: "go" }), // no agent_type at all
          call_id: "c2",
        }),
      ]),
    );
    assert.deepEqual(await collectCodexSubagentRuns(transcriptPath), []);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
