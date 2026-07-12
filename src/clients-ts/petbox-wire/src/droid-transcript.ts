// Factory Droid transcript parsing — the droid analogue of transcript.ts, kept as a thin
// adapter over the SHARED text-extraction/exclusion rules so what a "session turn" is cannot
// drift between agents (spec: agent-wiring, wiring-single-source).
//
// A droid transcript is JSONL, but its record shape differs from Claude Code's:
//   - line 1 is `{type:"session_start", id, title, cwd, ...}` (skipped);
//   - each turn is `{type:"message", message:{role, content}}` where content is either a
//     string or an array of parts `{type:"text"|"thinking"|"tool_use"|"tool_result", ...}`.
// We keep the user/assistant TEXT turns in order and drop tool_use/tool_result/thinking dumps
// and harness chrome — reusing extractText (text-parts only) + isExcluded (`<system-reminder>`
// and friends) from transcript.ts so both agents share one definition. Droid also flags
// injected context with `visibility:"llm_only"`; we skip those too as a belt-and-suspenders
// guard alongside the system-reminder prefix check.
//
// Plain TS for native node type-stripping: zero deps.

import { createReadStream } from "node:fs";
import { createInterface } from "node:readline";
import { extractText, isExcluded, type Msg, type SubagentRun } from "./transcript.ts";

// Droid's own subagent-spawn tool, observed in real ~/.factory/sessions data as "Task"
// (`{type:"tool_use", name:"Task", input:{subagent_type, description, prompt, ...}}`).
// "Agent" is accepted too in case droid ever renames it to match Claude Code's current name.
const DROID_SPAWN_TOOL_NAMES = new Set(["Task", "Agent"]);

function nonEmptyString(v: unknown): string | undefined {
  return typeof v === "string" && v.trim().length > 0 ? v.trim() : undefined;
}

// Per-session subagent-run provenance for droid (spec: subagent-run-provenance).
//
// What IS recoverable: role (input.subagent_type) and, if droid ever passes one, an explicit
// spawn-time `model` override — both live on the same Task tool_use the main transcript
// already carries.
//
// What is NOT recoverable, verified empirically against real droid session data
// (~/.factory/sessions/**/*.jsonl, 2026-07-12): droid's `message` records never carry a
// `model` field, on ANY turn — main-agent or subagent. There is no per-task sibling transcript
// (unlike Claude Code's subagents/agent-<id>.jsonl) to recover it from either. So `actualModel`
// is never set here — it would have to be invented, which is exactly the bug this chore fixes.
// If a future droid version starts stamping a model on messages, this should be revisited.
export async function collectDroidSubagentRuns(transcriptPath: string): Promise<SubagentRun[]> {
  const rl = createInterface({
    input: createReadStream(transcriptPath, { encoding: "utf8" }),
    crlfDelay: Infinity,
  });
  const runs: SubagentRun[] = [];
  for await (const line of rl) {
    if (!line || line.trim().length === 0) continue;
    let e: any;
    try {
      e = JSON.parse(line);
    } catch {
      continue;
    }
    if (e.type !== "message" || !e.message || e.message.role !== "assistant") continue;
    if (!Array.isArray(e.message.content)) continue;
    for (const c of e.message.content) {
      if (!c || c.type !== "tool_use" || !DROID_SPAWN_TOOL_NAMES.has(c.name)) continue;
      const input = (c.input ?? {}) as Record<string, unknown>;
      const role = nonEmptyString(input.subagent_type);
      if (!role) continue;
      const spawnModel = nonEmptyString(input.model);
      const run: { role: string; modelSource: "override" | "roster"; spawnModel?: string } = {
        role,
        modelSource: spawnModel ? "override" : "roster",
      };
      if (spawnModel) run.spawnModel = spawnModel;
      runs.push(run);
    }
  }
  return runs;
}

// Collect the user/assistant text messages in droid transcript order. No rendering and no
// cap: the server needs the full, ordered transcript to assign stable per-message ordinals.
export async function buildDroidMessages(transcriptPath: string): Promise<Msg[]> {
  const rl = createInterface({
    input: createReadStream(transcriptPath, { encoding: "utf8" }),
    crlfDelay: Infinity,
  });
  const msgs: Msg[] = [];
  for await (const line of rl) {
    if (!line || line.trim().length === 0) continue;
    let e: any;
    try {
      e = JSON.parse(line);
    } catch {
      continue;
    }
    if (e.type !== "message" || !e.message) continue;
    const role = e.message.role;
    if (role !== "user" && role !== "assistant") continue;
    if (e.message.visibility === "llm_only") continue; // injected context, not a real turn
    const text = extractText(e.message);
    if (text.length === 0) continue; // thinking/tool_use/tool_result-only turn → no text
    if (isExcluded(text)) continue; // <system-reminder> / harness chrome
    msgs.push({ role, content: text });
  }
  return msgs;
}
