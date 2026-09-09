// Qwen Code chat-transcript parsing — the qwen analogue of codex-transcript.ts, kept as a thin
// adapter over the SHARED text-extraction/exclusion rules (transcript.ts's extractText /
// isExcluded) so "what is a session turn" cannot drift between agents (spec: agent-wiring,
// wiring-single-source).
//
// The Claude Code parser (transcript.ts's buildMessages) does NOT work on a Qwen transcript —
// this is the whole reason this file exists (qwen-spec.md §4, source-anchored:
// core/src/services/chatRecordingService.ts:277-390's `ChatRecord`):
//
//   - Claude Code: `message.content` — a string, or an array of typed blocks
//     ({type:"text"|"tool_use"|"tool_result"}).
//   - Qwen: `message.parts` — an array of GEMINI parts: {text}, {thought}, {functionCall},
//     {functionResponse}, {inlineData}. A parser reading `record.message.content` gets
//     `undefined` on every Qwen record.
//
// Two more divergences the spec calls out explicitly:
//   1. `type === "system"` records carry `subtype` + `systemPayload` and NO `message` at all —
//      a parser assuming `message` exists crashes on one. The `ui_telemetry` subtype in
//      particular is high-volume and can be ~50KB per line, so `system` is filtered out FIRST,
//      before any message access.
//   2. A `{thought: true}` part is Qwen's internal reasoning trace, not dialogue — skipped, the
//      same way tool dumps are skipped, never surfaced to the server.
//
// `type` has four values: 'user' | 'assistant' | 'tool_result' | 'system'. Only 'user' and
// 'assistant' are text turns; 'tool_result' is a tool dump (excluded, same policy as Claude
// Code's tool_use/tool_result blocks) and 'system' is filtered first as above.
//
// toGenericMessage relabels Qwen's `{text}` parts to transcript.ts's shared `{type:"text",text}`
// tag (dropping `{thought:true}` parts first) so extractText — Claude Code's own part-type
// filter — applies unchanged; functionCall/functionResponse/inlineData parts are left as-is,
// which extractText already ignores (no `type:"text"`), so tool calls never leak into the
// pushed dialogue, matching Claude Code's and Codex's exclusion of tool_use/function_call.
//
// Plain TS for native node type-stripping: zero deps.

import { createReadStream } from "node:fs";
import { createInterface } from "node:readline";
import {
  extractText,
  isExcluded,
  mainRunFromModels,
  type MainRun,
  type Msg,
  type SubagentRun,
} from "./transcript.ts";

// Qwen's built-in subagent-spawn tool (qwen-spec.md §6, verified live in the installed clone,
// packages/core/src/tools/agent/agent.ts:679-680: `static readonly Name: string =
// ToolNames.AGENT` where `ToolNames.AGENT = 'agent'` — the canonical/internal tool name that
// appears as a functionCall part's `name`, NOT the "Agent" display name). Params match Claude
// Code's Task/Agent tool field-for-field: `subagent_type` + optional `model` (agent.ts:225/718).
const QWEN_SPAWN_TOOL_NAME = "agent";

function nonEmptyString(v: unknown): string | undefined {
  return typeof v === "string" && v.trim().length > 0 ? v.trim() : undefined;
}

type QwenPart = {
  readonly text?: string;
  readonly thought?: boolean;
  readonly functionCall?: { readonly name?: unknown; readonly args?: unknown };
  readonly functionResponse?: unknown;
  readonly inlineData?: unknown;
};

/** Relabel Qwen's Gemini {text} parts to transcript.ts's shared {type:"text",text} tag, skipping
 * {thought:true} parts entirely, so extractText (Claude Code's own part-type filter) applies
 * unchanged. functionCall/functionResponse/inlineData parts pass through untouched — extractText
 * already ignores anything without `type:"text"`. */
function toGenericMessage(message: { readonly parts?: unknown }): { content: unknown } {
  if (!Array.isArray(message.parts)) return { content: undefined };
  const parts = (message.parts as QwenPart[])
    .filter((p) => !(p && p.thought === true))
    .map((p) => {
      if (p && typeof p.text === "string") return { type: "text", text: p.text };
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
 * Per-session subagent-run provenance for qwen (spec: subagent-run-provenance). Recoverable:
 * role (the Agent tool's `subagent_type` functionCall arg) and, if passed, an explicit
 * spawn-time `model` override — both live on the same functionCall part the main chat-recording
 * file already carries (same reasoning as codex-transcript.ts's collectCodexSubagentRuns: no
 * evidence in qwen-spec.md of a separate per-subagent sibling transcript file this kit needs to
 * cross-reference the way Claude Code's is).
 *
 * NOT recoverable, same reasoning as codex-transcript.ts: no documented record shape stamps
 * which model a spawned agent's OWN turns actually ran on, attributable back to this spawn call
 * — so `actualModel` is never set here. Never guessed, never backfilled.
 */
export async function collectQwenSubagentRuns(transcriptPath: string): Promise<SubagentRun[]> {
  const rl = createInterface({
    input: createReadStream(transcriptPath, { encoding: "utf8" }),
    crlfDelay: Infinity,
  });
  const runs: SubagentRun[] = [];
  for await (const line of rl) {
    const e = parseLine(line);
    if (!e || e.type === "system") continue; // filtered FIRST — carries no `message` at all
    if (!e.message || !Array.isArray(e.message.parts)) continue;
    for (const p of e.message.parts as QwenPart[]) {
      const fc = p?.functionCall;
      if (!fc || fc.name !== QWEN_SPAWN_TOOL_NAME) continue;
      const args = fc.args;
      if (typeof args !== "object" || args === null) continue;
      const input = args as Record<string, unknown>;
      const role = nonEmptyString(input["subagent_type"]);
      if (!role) continue; // malformed/unknown spawn shape — nothing trustworthy to record
      const spawnModel = nonEmptyString(input["model"]);
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

// The `system` records this parser otherwise filters out carry the ONE thing a qwen chat record
// never puts on a message: the model the turn actually ran on. `subtype: "ui_telemetry"` with
// `systemPayload.uiEvent["event.name"] === "qwen-code.api_response"` is qwen's own log of an API
// response, and its `model` field is that response's model — verified live 2026-09-09 against
// `~/.qwen/projects/d--my-prj-petsonde/chats/*.jsonl` (values `deepseek-v4-pro`, `glm-5.3-flash`,
// `qwen3.8-max`, matching the roles.json binding each session was launched under).
//
// This is why qwen needs a main-run collector even though it has no per-subagent actual model
// (see collectQwenSubagentRuns): the MAIN loop's model IS recoverable here, and qwen is the one
// harness measured to drop the self-intro line while reading the banner — the exact case where
// prose cannot be the evidence of what ran.
//
// A ui_telemetry line can be ~50KB, so the substring prefilter below decides whether a line is
// worth JSON.parse at all; every other record shape is skipped without allocation.
const QWEN_API_RESPONSE_EVENT = "qwen-code.api_response";

export async function collectQwenMainRun(transcriptPath: string): Promise<MainRun | undefined> {
  const rl = createInterface({
    input: createReadStream(transcriptPath, { encoding: "utf8" }),
    crlfDelay: Infinity,
  });
  const models: string[] = [];
  for await (const line of rl) {
    if (!line.includes(QWEN_API_RESPONSE_EVENT)) continue;
    const e = parseLine(line);
    if (!e || e.type !== "system") continue;
    const ev = e.systemPayload?.uiEvent;
    if (!ev || ev["event.name"] !== QWEN_API_RESPONSE_EVENT) continue;
    const model = nonEmptyString(ev.model);
    if (model) models.push(model);
  }
  return mainRunFromModels(models);
}

// Collect the user/assistant text messages in chat-recording order. No rendering and no cap:
// the server needs the full, ordered transcript to assign stable per-message ordinals.
export async function buildQwenMessages(transcriptPath: string): Promise<Msg[]> {
  const rl = createInterface({
    input: createReadStream(transcriptPath, { encoding: "utf8" }),
    crlfDelay: Infinity,
  });
  const msgs: Msg[] = [];
  for await (const line of rl) {
    const e = parseLine(line);
    if (!e) continue;
    if (e.type === "system") continue; // no `message` at all — filtered before any field access
    if (e.type !== "user" && e.type !== "assistant") continue; // skip tool_result
    if (e.isSidechain) continue; // a subagent's own turn, not this session's dialogue
    if (!e.message) continue;
    const text = extractText(toGenericMessage(e.message));
    if (text.length === 0) continue; // no text parts on this turn (e.g. a pure tool-call turn)
    if (isExcluded(text)) continue; // <system-reminder> / harness chrome
    msgs.push({ role: e.type, content: text });
  }
  return msgs;
}
