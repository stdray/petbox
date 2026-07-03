// Shared Claude Code transcript parsing — the ONE implementation both the Stop hook
// (push-session.ts) and the history importer (import-sessions.ts) use, so what a session
// "is" cannot drift between live pushes and imports (spec: wiring-single-source).
//
// A transcript is JSONL; we keep the user/assistant TEXT turns in order and exclude tool
// dumps, meta/sidechain entries and harness chrome — tool outputs can carry secrets and
// the server only wants the dialogue (spec: wiring-history-import).
//
// Plain TS for native node type-stripping: zero deps.

import { createReadStream } from "node:fs";
import { createInterface } from "node:readline";

export type Msg = { role: string; content: string };

export function extractText(message: unknown): string {
  const msg = message as { content?: unknown } | null;
  if (!msg || msg.content == null) return "";
  if (typeof msg.content === "string") return msg.content.trim();
  if (Array.isArray(msg.content)) {
    const parts = msg.content
      .filter((p: any) => p && p.type === "text" && typeof p.text === "string")
      .map((p: any) => p.text);
    return parts.join("\n").trim();
  }
  return "";
}

export function isExcluded(text: string): boolean {
  return (
    text.startsWith("<system-reminder") ||
    text.startsWith("<command-name>") ||
    text.startsWith("<local-command")
  );
}

// Collect the user/assistant text messages in transcript order. No rendering and no cap:
// the server needs the full, ordered transcript to assign stable per-message ordinals.
export async function buildMessages(transcriptPath: string): Promise<Msg[]> {
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
    if (e.type !== "user" && e.type !== "assistant") continue;
    if (e.isMeta || e.isSidechain) continue;
    const text = extractText(e.message);
    if (text.length === 0) continue;
    if (isExcluded(text)) continue;
    msgs.push({ role: e.type, content: text });
  }
  return msgs;
}

// The cwd a transcript was recorded in (the first entry that carries one). Lets the
// importer attribute a transcript to a registered project WITHOUT reversing Claude's
// lossy path-encoding of the directory name.
export async function readTranscriptCwd(transcriptPath: string, maxLines = 25): Promise<string | null> {
  const rl = createInterface({
    input: createReadStream(transcriptPath, { encoding: "utf8" }),
    crlfDelay: Infinity,
  });
  let seen = 0;
  for await (const line of rl) {
    if (++seen > maxLines) break;
    if (!line || line.trim().length === 0) continue;
    try {
      const e = JSON.parse(line);
      if (e && typeof e.cwd === "string" && e.cwd.length > 0) {
        rl.close();
        return e.cwd;
      }
    } catch {
      /* skip unparseable line */
    }
  }
  return null;
}
