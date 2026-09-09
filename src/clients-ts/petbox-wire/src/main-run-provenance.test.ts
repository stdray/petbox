// Unit tests for MAIN-session run provenance (work: agents-ignore-session-start-self-intro).
//
// The point of the feature, and therefore of these tests: "which model actually ran this main
// session" must come from the harness's own API-response records, never from the model's
// SessionStart self-intro prose. Measured 2026-09-09, that prose fails in both directions — a
// qwen main session read the banner and declined to emit the line, and a role bound to
// qwen3.8-max introduced itself as `claude-opus-4-6`.
//
// Run: node --test src/main-run-provenance.test.ts

import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { collectQwenMainRun } from "./qwen-transcript.ts";
import { collectMainRun, mainRunFromModels } from "./transcript.ts";

function tmpFile(prefix: string): { dir: string; transcriptPath: string } {
  const dir = mkdtempSync(join(tmpdir(), prefix));
  return { dir, transcriptPath: join(dir, "chat-test.jsonl") };
}

function ndjson(lines: unknown[]): string {
  return lines.map((l) => JSON.stringify(l)).join("\n") + "\n";
}

function qwenApiResponse(model: string): unknown {
  return {
    type: "system",
    subtype: "ui_telemetry",
    systemPayload: { uiEvent: { "event.name": "qwen-code.api_response", model, status_code: 200 } },
  };
}

// ---- mainRunFromModels ----------------------------------------------------------------------

test("mainRunFromModels: no models at all → undefined (omit the key, never invent one)", () => {
  assert.equal(mainRunFromModels([]), undefined);
});

test("mainRunFromModels: one model → actualModel, and no modelsSeen noise", () => {
  assert.deepEqual(mainRunFromModels(["deepseek-v4-pro", "deepseek-v4-pro"]), {
    actualModel: "deepseek-v4-pro",
  });
});

test("mainRunFromModels: a mid-session switch keeps the LAST as actual and lists both", () => {
  assert.deepEqual(mainRunFromModels(["glm-5.3-flash", "glm-5.3-flash", "deepseek-v4-pro"]), {
    actualModel: "deepseek-v4-pro",
    modelsSeen: ["glm-5.3-flash", "deepseek-v4-pro"],
  });
});

// ---- collectMainRun (Claude Code) -------------------------------------------------------------

test("collectMainRun: reads message.model off the main assistant turns", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-mainrun-cc-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        { type: "user", message: { role: "user", content: "hi" } },
        { type: "assistant", message: { role: "assistant", model: "claude-opus-5", content: [] } },
      ]),
    );
    assert.deepEqual(await collectMainRun(transcriptPath), { actualModel: "claude-opus-5" });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("collectMainRun: a sidechain turn is a SUBAGENT's model and never becomes the main one", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-mainrun-side-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        { type: "assistant", message: { role: "assistant", model: "claude-opus-5", content: [] } },
        {
          type: "assistant",
          isSidechain: true,
          message: { role: "assistant", model: "claude-sonnet-5", content: [] },
        },
      ]),
    );
    assert.deepEqual(await collectMainRun(transcriptPath), { actualModel: "claude-opus-5" });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("collectMainRun: a missing transcript is absent provenance, not a throw", async () => {
  assert.equal(await collectMainRun(join(tmpdir(), "petbox-no-such-transcript-9e1a.jsonl")), undefined);
});

test("collectMainRun: assistant turns with no model field → undefined, never a guess", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-mainrun-nomodel-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([{ type: "assistant", message: { role: "assistant", content: [] } }]),
    );
    assert.equal(await collectMainRun(transcriptPath), undefined);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ---- collectQwenMainRun ------------------------------------------------------------------------

test("collectQwenMainRun: recovers the model from ui_telemetry api_response records", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-mainrun-qwen-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        { type: "user", message: { role: "user", parts: [{ text: "что по задачам?" }] } },
        qwenApiResponse("deepseek-v4-pro"),
        { type: "assistant", message: { role: "assistant", parts: [{ text: "ok" }] } },
      ]),
    );
    assert.deepEqual(await collectQwenMainRun(transcriptPath), { actualModel: "deepseek-v4-pro" });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("collectQwenMainRun: other ui_telemetry events carry no model claim", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-mainrun-qwen-other-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        {
          type: "system",
          subtype: "ui_telemetry",
          systemPayload: {
            uiEvent: { "event.name": "qwen-code.api_request", model: "not-the-answer" },
          },
        },
      ]),
    );
    assert.equal(await collectQwenMainRun(transcriptPath), undefined);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("collectQwenMainRun: the prose of the reply is NEVER the source — telemetry wins", async () => {
  // The reserve-on-qwen3.8-max incident in one file: the model's own text names a Claude model,
  // the harness's api_response record names what actually served the turn.
  const { dir, transcriptPath } = tmpFile("petbox-mainrun-qwen-liar-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        qwenApiResponse("qwen3.8-max"),
        {
          type: "assistant",
          message: { role: "assistant", parts: [{ text: "claude-opus-4-6 · reserve" }] },
        },
      ]),
    );
    assert.deepEqual(await collectQwenMainRun(transcriptPath), { actualModel: "qwen3.8-max" });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("collectQwenMainRun: a session with no api_response record yields no claim", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-mainrun-qwen-empty-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([{ type: "user", message: { role: "user", parts: [{ text: "hi" }] } }]),
    );
    assert.equal(await collectQwenMainRun(transcriptPath), undefined);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
