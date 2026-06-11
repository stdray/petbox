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

import { resolveProject } from "./registry.ts";
import { buildMessages, type Msg } from "./transcript.ts";

const FETCH_TIMEOUT_MS = 12000;

type HookInput = {
  session_id?: string;
  transcript_path?: string;
  cwd?: string;
  stop_hook_active?: boolean;
};

function readStdin(): Promise<string> {
  return new Promise((resolve) => {
    let buf = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (c) => (buf += c));
    process.stdin.on("end", () => resolve(buf));
    process.stdin.on("error", () => resolve(buf));
  });
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
