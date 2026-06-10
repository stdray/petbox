// Claude Code Stop hook (global) — port of push-session.ps1.
//
// Mirrors the session conversation into PetBox's Session module so the board auto-populates.
// The project + API key are resolved from cwd via the shared registry; if the cwd is not a
// registered project this exits immediately (first guard, before any work).
//
// Reads the full transcript JSONL and POSTs the user/assistant text turns as ndjson — one
// {role, content} message per line, in order (tool dumps and system reminders excluded). The
// server numbers the messages (ordinal) and stores the latest snapshot; the endpoint is
// last-write-wins, so each turn re-pushes the whole transcript and it self-heals. Best-effort:
// every failure is swallowed and we ALWAYS exit 0 — never break the user's session.

import { createReadStream } from "node:fs";
import { createInterface } from "node:readline";
import { resolveProject } from "./registry.ts";

const FETCH_TIMEOUT_MS = 12000;

type HookInput = {
  session_id?: string;
  transcript_path?: string;
  cwd?: string;
  stop_hook_active?: boolean;
};

type Msg = { role: string; content: string };

function readStdin(): Promise<string> {
  return new Promise((resolve) => {
    let buf = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (c) => (buf += c));
    process.stdin.on("end", () => resolve(buf));
    process.stdin.on("error", () => resolve(buf));
  });
}

function extractText(message: unknown): string {
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

function isExcluded(text: string): boolean {
  return (
    text.startsWith("<system-reminder") ||
    text.startsWith("<command-name>") ||
    text.startsWith("<local-command")
  );
}

// Collect the user/assistant text messages in transcript order. No rendering and no cap:
// the server needs the full, ordered transcript to assign stable per-message ordinals.
async function buildMessages(transcriptPath: string): Promise<Msg[]> {
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

async function main(): Promise<void> {
  try {
    const raw = await readStdin();
    let j: HookInput;
    try {
      j = JSON.parse(raw);
    } catch {
      return;
    }
    if (j.stop_hook_active) return;

    // FIRST guard: not a registered project → silent no-op.
    const resolved = resolveProject(j.cwd ?? "");
    if (!resolved) return;

    const sid = (j.session_id ?? "").trim();
    const tp = (j.transcript_path ?? "").trim();
    if (!sid || !tp) return;

    let msgs: Msg[];
    try {
      msgs = await buildMessages(tp);
    } catch {
      return; // transcript missing/unreadable
    }
    if (msgs.length === 0) return; // empty body → server returns 400, don't push

    const body = msgs.map((m) => JSON.stringify(m)).join("\n");

    const uri = `${resolved.baseUrl}/api/sessions/${resolved.project}/${encodeURIComponent(sid)}?agent=claude-code`;
    const ctrl = new AbortController();
    const timer = setTimeout(() => ctrl.abort(), FETCH_TIMEOUT_MS);
    try {
      await fetch(uri, {
        method: "POST",
        headers: { "X-Api-Key": resolved.apiKey, "Content-Type": "application/x-ndjson; charset=utf-8" },
        body,
        signal: ctrl.signal,
      });
    } finally {
      clearTimeout(timer);
    }
  } catch {
    // best-effort: never break the user's session
  }
}

main().finally(() => process.exit(0));
