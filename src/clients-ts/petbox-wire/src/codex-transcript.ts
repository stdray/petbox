// Codex CLI rollout-transcript parsing — the codex analogue of droid-transcript.ts, kept as a
// thin adapter over the SHARED text-extraction/exclusion rules (transcript.ts's extractText /
// isExcluded) so "what is a session turn" cannot drift between agents (spec: agent-wiring,
// wiring-single-source).
//
// Rollout shape (codex-spec.md §5, source-anchored — history/src/lib.rs:257-264,
// rollout_payload.rs:22-63): one JSON object per line,
//   `{ timestamp, ordinal?, type, payload }`
// where `type` is the RolloutLine's OWN item-kind tag (session_meta | response_item |
// inter_agent_communication | inter_agent_communication_metadata | compacted | turn_context |
// token_usage_record | world_state | retained_context | security_risk_score | event_msg |
// realtime_item) and `payload` carries that item's own data. We only ever care about
// `type: "response_item"` lines — every other item type is skipped, never crashed on (spec
// warns this explicitly: a rollout can carry line types this kit has no reason to parse).
//
// Inside a response_item payload (protocol/src/models.rs:980-1101), the payload itself carries
// a SECOND `type` tag distinguishing the ResponseItem variant:
//   - message: {type:"message", role:"user"|"assistant", content:[{type:"input_text"|
//     "output_text", text}], id?, phase?}
//   - function_call: {type:"function_call", name, arguments:"<JSON AS A STRING>", call_id} —
//     `arguments` is a raw JSON string, NOT an object; must be JSON.parse'd separately.
//   - function_call_output: {type:"function_call_output", call_id, output}
//
// Codex's message parts use "input_text"/"output_text", not Claude Code's "text" — a thin
// adapter (toGenericMessage below) relabels them before handing off to extractText, so the
// actual text-joining/trim rule stays the ONE shared implementation, never reimplemented here.
//
// Plain TS for native node type-stripping: zero deps.

import { createReadStream } from "node:fs";
import { createInterface } from "node:readline";
import { extractText, isExcluded, type Msg, type SubagentRun } from "./transcript.ts";

// codex's subagent-spawn tool (codex-spec.md §3/§4; multi_agents_v2/spawn.rs's SpawnAgentArgs).
const CODEX_SPAWN_TOOL_NAME = "spawn_agent";

function nonEmptyString(v: unknown): string | undefined {
  return typeof v === "string" && v.trim().length > 0 ? v.trim() : undefined;
}

/** Relabel codex's input_text/output_text parts to transcript.ts's shared "text" tag so
 * extractText (Claude Code's own part-type filter) applies unchanged. */
function toGenericMessage(item: { readonly content?: unknown }): { content: unknown } {
  if (!Array.isArray(item.content)) return { content: item.content };
  const parts = item.content.map((p: any) => {
    if (p && (p.type === "input_text" || p.type === "output_text") && typeof p.text === "string") {
      return { type: "text", text: p.text };
    }
    return p;
  });
  return { content: parts };
}

function parseLine(line: string): any {
  if (!line || line.trim().length === 0) return undefined;
  try {
    return JSON.parse(line);
  } catch {
    return undefined;
  }
}

/**
 * Per-session subagent-run provenance for codex (spec: subagent-run-provenance). Recoverable:
 * role (function_call arguments' `agent_type`) and, if passed, an explicit spawn-time `model`
 * override — both live on the same `function_call` entry the main rollout already carries (no
 * evidence of a separate per-subagent sibling transcript this kit needs to cross-reference, the
 * way Claude Code's is — codex-spec.md documents no such file).
 *
 * NOT recoverable, same reasoning as droid-transcript.ts: no `response_item` shape in the spec
 * stamps which model a spawned agent's OWN turns actually ran on, attributable back to this
 * spawn call — so `actualModel` is never set here. Never guessed, never backfilled.
 */
export async function collectCodexSubagentRuns(transcriptPath: string): Promise<SubagentRun[]> {
  const rl = createInterface({
    input: createReadStream(transcriptPath, { encoding: "utf8" }),
    crlfDelay: Infinity,
  });
  const runs: SubagentRun[] = [];
  for await (const line of rl) {
    const e = parseLine(line);
    if (!e || e.type !== "response_item") continue;
    const payload = e.payload;
    if (!payload || payload.type !== "function_call" || payload.name !== CODEX_SPAWN_TOOL_NAME) continue;
    const rawArgs = payload.arguments;
    if (typeof rawArgs !== "string") continue; // malformed — not the documented shape, skip
    let args: unknown;
    try {
      args = JSON.parse(rawArgs);
    } catch {
      continue; // arguments did not parse as JSON — nothing trustworthy to record
    }
    if (typeof args !== "object" || args === null) continue;
    const input = args as Record<string, unknown>;
    const role = nonEmptyString(input["agent_type"]);
    if (!role) continue;
    const spawnModel = nonEmptyString(input["model"]);
    const run: { role: string; modelSource: "override" | "roster"; spawnModel?: string } = {
      role,
      modelSource: spawnModel ? "override" : "roster",
    };
    if (spawnModel) run.spawnModel = spawnModel;
    runs.push(run);
  }
  return runs;
}

// Collect the user/assistant text messages in rollout order. No rendering and no cap: the
// server needs the full, ordered transcript to assign stable per-message ordinals.
export async function buildCodexMessages(transcriptPath: string): Promise<Msg[]> {
  const rl = createInterface({
    input: createReadStream(transcriptPath, { encoding: "utf8" }),
    crlfDelay: Infinity,
  });
  const msgs: Msg[] = [];
  for await (const line of rl) {
    const e = parseLine(line);
    if (!e || e.type !== "response_item") continue; // skip session_meta/event_msg/etc, never crash
    const payload = e.payload;
    if (!payload || payload.type !== "message") continue; // function_call(_output) — not a text turn
    const role = payload.role;
    if (role !== "user" && role !== "assistant") continue;
    const text = extractText(toGenericMessage(payload));
    if (text.length === 0) continue; // no text parts on this turn
    if (isExcluded(text)) continue; // <system-reminder> / harness chrome
    msgs.push({ role, content: text });
  }
  return msgs;
}
