// Unit tests for qwen-transcript.ts — the qwen port of transcript.ts/codex-transcript.ts.
// Record shapes are Gemini-shaped (qwen-spec.md §4, source-anchored: core/src/services/
// chatRecordingService.ts:277-390's `ChatRecord`): `{type, message:{role, parts}}` where
// `parts` are `{text}` / `{thought:true}` / `{functionCall}` / `{functionResponse}` /
// `{inlineData}`; `type === "system"` records carry NO `message` at all and must be filtered
// before any field access.
//
// Run: node --test src/qwen-transcript.test.ts

import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { buildQwenMessages, collectQwenSubagentRuns } from "./qwen-transcript.ts";

function tmpFile(prefix: string): { dir: string; transcriptPath: string } {
  const dir = mkdtempSync(join(tmpdir(), prefix));
  return { dir, transcriptPath: join(dir, "chat-test.jsonl") };
}

function ndjson(lines: unknown[]): string {
  return lines.map((l) => JSON.stringify(l)).join("\n") + "\n";
}

// ---- buildQwenMessages ---------------------------------------------------------------------

test("buildQwenMessages: a user record with one text part produces one user message", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-qwen-msgs-user-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([{ type: "user", message: { role: "user", parts: [{ text: "hello there" }] } }]),
    );
    const msgs = await buildQwenMessages(transcriptPath);
    assert.deepEqual(msgs, [{ role: "user", content: "hello there" }]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("buildQwenMessages: an assistant record's several text parts are concatenated in order", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-qwen-msgs-assistant-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        {
          type: "assistant",
          message: {
            role: "model",
            parts: [{ text: "first part" }, { text: "second part" }, { text: "third part" }],
          },
        },
      ]),
    );
    const msgs = await buildQwenMessages(transcriptPath);
    assert.deepEqual(msgs, [{ role: "assistant", content: "first part\nsecond part\nthird part" }]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("buildQwenMessages: thought:true parts are skipped — only the visible text survives", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-qwen-msgs-thought-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        {
          type: "assistant",
          message: {
            role: "model",
            parts: [
              { text: "inner", thought: true },
              { text: "visible" },
            ],
          },
        },
      ]),
    );
    const msgs = await buildQwenMessages(transcriptPath);
    assert.deepEqual(msgs, [{ role: "assistant", content: "visible" }]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("buildQwenMessages: type:system records are filtered out entirely, including one with no message key at all", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-qwen-msgs-system-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        { type: "user", message: { role: "user", parts: [{ text: "before" }] } },
        {
          type: "system",
          subtype: "ui_telemetry",
          systemPayload: { uiEvent: { "event.name": "qwen-code.api_response" } },
          // deliberately NO `message` key — this is the shape that crashes a parser assuming
          // `message` always exists.
        },
        { type: "assistant", message: { role: "model", parts: [{ text: "after" }] } },
      ]),
    );
    const msgs = await buildQwenMessages(transcriptPath);
    assert.deepEqual(msgs, [
      { role: "user", content: "before" },
      { role: "assistant", content: "after" },
    ]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("buildQwenMessages: functionCall/functionResponse-only turns produce no text and are dropped", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-qwen-msgs-toolcalls-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        { type: "user", message: { role: "user", parts: [{ text: "do X" }] } },
        {
          type: "assistant",
          message: {
            role: "model",
            parts: [
              { functionCall: { id: "call_1", name: "read_file", args: { absolute_path: "/x" } } },
            ],
          },
        },
        {
          type: "tool_result",
          message: {
            role: "user",
            parts: [{ functionResponse: { id: "call_1", name: "read_file", response: { output: "contents" } } }],
          },
        },
        { type: "assistant", message: { role: "model", parts: [{ text: "done!" }] } },
      ]),
    );
    const msgs = await buildQwenMessages(transcriptPath);
    assert.deepEqual(msgs, [
      { role: "user", content: "do X" },
      { role: "assistant", content: "done!" },
    ]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("buildQwenMessages: a text part alongside a functionCall part keeps the text, drops the call", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-qwen-msgs-mixed-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        {
          type: "assistant",
          message: {
            role: "model",
            parts: [
              { text: "let me check that file" },
              { functionCall: { id: "call_1", name: "read_file", args: { absolute_path: "/x" } } },
            ],
          },
        },
      ]),
    );
    const msgs = await buildQwenMessages(transcriptPath);
    assert.deepEqual(msgs, [{ role: "assistant", content: "let me check that file" }]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("buildQwenMessages: malformed input (blank line, invalid JSON, missing parts) never throws", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-qwen-msgs-malformed-");
  try {
    writeFileSync(
      transcriptPath,
      [
        "",
        "{ not json at all",
        JSON.stringify({ type: "user", message: { role: "user" } }), // message present, parts missing
        JSON.stringify({ type: "user", message: { role: "user", parts: [{ text: "ok" }] } }),
      ].join("\n") + "\n",
    );
    const msgs = await buildQwenMessages(transcriptPath);
    assert.deepEqual(msgs, [{ role: "user", content: "ok" }]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ---- collectQwenSubagentRuns ----------------------------------------------------------------

test("collectQwenSubagentRuns: no agent spawn calls → no runs", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-qwen-runs-none-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([{ type: "user", message: { role: "user", parts: [{ text: "hi" }] } }]),
    );
    assert.deepEqual(await collectQwenSubagentRuns(transcriptPath), []);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("collectQwenSubagentRuns: spawn WITHOUT model → roster; role from subagent_type", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-qwen-runs-roster-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        {
          type: "assistant",
          message: {
            role: "model",
            parts: [
              {
                functionCall: {
                  id: "call_1",
                  name: "agent",
                  args: { subagent_type: "petbox-worker", message: "go" },
                },
              },
            ],
          },
        },
      ]),
    );
    const runs = await collectQwenSubagentRuns(transcriptPath);
    assert.deepEqual(runs, [{ role: "petbox-worker", modelSource: "roster" }]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("collectQwenSubagentRuns: spawn WITH an explicit model → override; actualModel never set", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-qwen-runs-override-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        {
          type: "assistant",
          message: {
            role: "model",
            parts: [
              {
                functionCall: {
                  id: "call_1",
                  name: "agent",
                  args: { subagent_type: "petbox-explore", model: "deepseek-v4-flash", message: "go" },
                },
              },
            ],
          },
        },
      ]),
    );
    const runs = await collectQwenSubagentRuns(transcriptPath);
    assert.deepEqual(runs, [
      { role: "petbox-explore", modelSource: "override", spawnModel: "deepseek-v4-flash" },
    ]);
    assert.equal("actualModel" in runs[0]!, false, "actualModel must never be invented");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("collectQwenSubagentRuns: a functionCall that is NOT the agent tool is ignored", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-qwen-runs-othertool-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        {
          type: "assistant",
          message: {
            role: "model",
            parts: [
              { functionCall: { id: "call_1", name: "read_file", args: { absolute_path: "/x" } } },
            ],
          },
        },
      ]),
    );
    assert.deepEqual(await collectQwenSubagentRuns(transcriptPath), []);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("collectQwenSubagentRuns: malformed spawn args (missing subagent_type) never throws, just skips; system records are filtered first", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-qwen-runs-malformed-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        {
          type: "system",
          subtype: "ui_telemetry",
          systemPayload: { uiEvent: {} },
          // no `message` at all — must not throw when scanning for spawns either
        },
        {
          type: "assistant",
          message: {
            role: "model",
            parts: [{ functionCall: { id: "call_1", name: "agent", args: { message: "go" } } }], // no subagent_type
          },
        },
      ]),
    );
    assert.deepEqual(await collectQwenSubagentRuns(transcriptPath), []);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
