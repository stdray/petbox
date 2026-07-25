// Unit tests for subagent-run provenance (spec: subagent-run-provenance).
//
// Covers the FACT half of session meta — which subagents actually ran, in what role, on what
// model — as distinct from roleBinding's INTENTION half (append-meta.test.ts). See
// transcript.ts (Claude Code) and droid-transcript.ts (droid) for the collectors under test.
//
// Run: node --test src/subagent-runs.test.ts

import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { buildSessionMetaHeader } from "./append.ts";
import { collectDroidSubagentRuns } from "./droid-transcript.ts";
import { collectSubagentRuns } from "./transcript.ts";

function tmpFile(prefix: string): { dir: string; transcriptPath: string; sessionId: string } {
  const dir = mkdtempSync(join(tmpdir(), prefix));
  const sessionId = "session-under-test";
  const transcriptPath = join(dir, `${sessionId}.jsonl`);
  return { dir, transcriptPath, sessionId };
}

function ndjson(lines: unknown[]): string {
  return lines.map((l) => JSON.stringify(l)).join("\n") + "\n";
}

// ---- Claude Code: collectSubagentRuns --------------------------------------------------------

test("collectSubagentRuns: no Agent/Task tool_use → no runs at all", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-subagent-runs-none-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        { type: "user", message: { role: "user", content: "hello" } },
        { type: "assistant", message: { role: "assistant", content: "hi there" } },
      ]),
    );
    const runs = await collectSubagentRuns(transcriptPath);
    assert.deepEqual(runs, []);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("collectSubagentRuns: spawn WITHOUT a model → roster-sourced, no spawnModel", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-subagent-runs-roster-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        {
          type: "assistant",
          message: {
            role: "assistant",
            content: [
              {
                type: "tool_use",
                id: "toolu_roster1",
                name: "Agent",
                input: { subagent_type: "worker", description: "no explicit model" },
              },
            ],
          },
        },
      ]),
    );
    const runs = await collectSubagentRuns(transcriptPath);
    assert.deepEqual(runs, [{ role: "worker", modelSource: "roster" }]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("collectSubagentRuns: spawn WITH an explicit model → override, spawnModel set", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-subagent-runs-override-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        {
          type: "assistant",
          message: {
            role: "assistant",
            content: [
              {
                type: "tool_use",
                id: "toolu_override1",
                name: "Agent",
                input: { subagent_type: "general-purpose", model: "haiku", description: "d" },
              },
            ],
          },
        },
      ]),
    );
    const runs = await collectSubagentRuns(transcriptPath);
    assert.deepEqual(runs, [
      { role: "general-purpose", modelSource: "override", spawnModel: "haiku" },
    ]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("collectSubagentRuns: recovers actualModel from the per-task sibling transcript", async () => {
  const { dir, transcriptPath, sessionId } = tmpFile("petbox-subagent-runs-actual-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        {
          type: "assistant",
          message: {
            role: "assistant",
            content: [
              {
                type: "tool_use",
                id: "toolu_hasfile",
                name: "Agent",
                input: { subagent_type: "worker" },
              },
              {
                type: "tool_use",
                id: "toolu_nofile",
                name: "Agent",
                input: { subagent_type: "worker" },
              },
            ],
          },
        },
      ]),
    );
    const subagentsDir = join(dir, sessionId, "subagents");
    mkdirSync(subagentsDir, { recursive: true });
    writeFileSync(
      join(subagentsDir, "agent-abc123.meta.json"),
      JSON.stringify({ agentType: "worker", toolUseId: "toolu_hasfile", spawnDepth: 1 }),
    );
    writeFileSync(
      join(subagentsDir, "agent-abc123.jsonl"),
      ndjson([
        { type: "user", isSidechain: true, message: { role: "user", content: "go" } },
        {
          type: "assistant",
          isSidechain: true,
          message: { role: "assistant", model: "claude-sonnet-5", content: "ok" },
        },
      ]),
    );
    // toolu_nofile deliberately has no matching subagents/*.meta.json (cleaned up, or never
    // written) — actualModel must stay absent, not fall back to spawnModel or a guess.

    const runs = await collectSubagentRuns(transcriptPath);
    assert.deepEqual(runs, [
      { role: "worker", modelSource: "roster", actualModel: "claude-sonnet-5" },
      { role: "worker", modelSource: "roster" },
    ]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ---- droid: collectDroidSubagentRuns -----------------------------------------------------------

test("collectDroidSubagentRuns: no Task tool_use → no runs", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-droid-subagent-runs-none-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([{ type: "message", message: { role: "assistant", content: "just text" } }]),
    );
    const runs = await collectDroidSubagentRuns(transcriptPath);
    assert.deepEqual(runs, []);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("collectDroidSubagentRuns: spawn WITHOUT a model → roster-sourced", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-droid-subagent-runs-roster-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        {
          type: "message",
          message: {
            role: "assistant",
            content: [
              { type: "tool_use", id: "call_1", name: "Task", input: { subagent_type: "worker" } },
            ],
          },
        },
      ]),
    );
    const runs = await collectDroidSubagentRuns(transcriptPath);
    assert.deepEqual(runs, [{ role: "worker", modelSource: "roster" }]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("collectDroidSubagentRuns: spawn WITH an explicit model → override; actualModel never set (droid has no per-turn model telemetry)", async () => {
  const { dir, transcriptPath } = tmpFile("petbox-droid-subagent-runs-override-");
  try {
    writeFileSync(
      transcriptPath,
      ndjson([
        {
          type: "message",
          message: {
            role: "assistant",
            content: [
              {
                type: "tool_use",
                id: "call_2",
                name: "Task",
                input: { subagent_type: "worker", model: "deepseek-v4-pro" },
              },
            ],
          },
        },
        // Even if a later message carried a model-shaped field, droid records don't carry one
        // in real data — this line intentionally does NOT include e.message.model, matching
        // the empirically observed shape (~/.factory/sessions, checked 2026-07-12).
      ]),
    );
    const runs = await collectDroidSubagentRuns(transcriptPath);
    assert.deepEqual(runs, [
      { role: "worker", modelSource: "override", spawnModel: "deepseek-v4-pro" },
    ]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ---- buildSessionMetaHeader: subagentRuns rides alongside roleBinding ---------------------------

function freshHome(): string {
  const home = mkdtempSync(join(tmpdir(), "petbox-subagent-meta-home-"));
  mkdirSync(join(home, ".petbox"), { recursive: true });
  return home;
}

test("buildSessionMetaHeader: extraMeta.subagentRuns merges alongside roleBinding", () => {
  const home = freshHome();
  try {
    writeFileSync(
      join(home, ".petbox", "roles.json"),
      JSON.stringify({
        activeProfile: "default",
        profiles: {
          default: { agents: { "claude-code": { roles: { worker: { model: "claude-sonnet-5" } } } } },
        },
      }),
    );
    const raw = buildSessionMetaHeader("claude-code", home, {
      subagentRuns: [{ role: "worker", modelSource: "roster", actualModel: "claude-sonnet-5" }],
    });
    assert.ok(raw);
    assert.deepEqual(JSON.parse(raw!), {
      roleBinding: {
        profile: "default",
        agent: "claude-code",
        roles: { worker: "claude-sonnet-5" },
      },
      subagentRuns: [{ role: "worker", modelSource: "roster", actualModel: "claude-sonnet-5" }],
    });
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("buildSessionMetaHeader: subagentRuns alone (no local roleBinding) still produces a header", () => {
  const home = freshHome(); // no roles.json written → resolveObservedBinding returns null
  try {
    const raw = buildSessionMetaHeader("claude-code", home, {
      subagentRuns: [{ role: "worker", modelSource: "override", spawnModel: "haiku" }],
    });
    assert.ok(raw);
    assert.deepEqual(JSON.parse(raw!), {
      subagentRuns: [{ role: "worker", modelSource: "override", spawnModel: "haiku" }],
    });
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});

test("buildSessionMetaHeader: no roleBinding and no extraMeta → null (unchanged behavior)", () => {
  const home = freshHome();
  try {
    assert.equal(buildSessionMetaHeader("claude-code", home), null);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
});
