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
import { extractText, isExcluded, type Msg } from "./transcript.ts";

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
