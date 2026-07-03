// Factory Droid Stop hook (global) — the droid port of push-session.ts.
//
// Mirrors the session conversation into PetBox's Session module so the board auto-populates.
// The project + API key are resolved from cwd via the shared registry; if the cwd is not a
// registered project this exits immediately (first guard, before any work).
//
// Droid's Stop stdin is snake_case (`session_id`, `transcript_path`, `cwd`, `stop_hook_active`)
// — the same shape Claude Code uses. We parse the droid JSONL (buildDroidMessages: user/
// assistant TEXT turns only, tool dumps + `<system-reminder>` injections excluded) and push
// only the INCREMENT via the server-authoritative append cursor (see append.ts): this process
// is fresh each turn, so it passes null and pushTranscript optimistically resends a small
// idempotent overlap window; a contiguity gap comes back as a structured 409 with the server's
// lastOrdinal and the tail is resent from there. Old servers without the append route fall back
// to the legacy full-snapshot push. Best-effort: every failure is swallowed and we ALWAYS
// exit 0 — never break the user's session.

import { pushTranscript } from "./append.ts";
import { buildDroidMessages } from "./droid-transcript.ts";
import { resolveProject } from "./registry.ts";
import type { Msg } from "./transcript.ts";

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

    // FIRST guard: not a registered project → silent no-op (before any file/network work).
    const resolved = resolveProject(j.cwd ?? "");
    if (!resolved) return;

    const sid = (j.session_id ?? "").trim();
    const tp = (j.transcript_path ?? "").trim();
    if (!sid || !tp) return;

    let msgs: Msg[];
    try {
      msgs = await buildDroidMessages(tp);
    } catch {
      return; // transcript missing/unreadable
    }
    if (msgs.length === 0) return; // empty body → server returns 400, don't push

    // Fresh process each turn → no remembered cursor (null): pushTranscript guesses an
    // idempotent overlap window and self-heals off the server's structured gap reject.
    await pushTranscript(
      {
        baseUrl: resolved.baseUrl,
        project: resolved.project,
        sessionId: sid,
        apiKey: resolved.apiKey,
        agent: "droid",
        timeoutMs: FETCH_TIMEOUT_MS,
      },
      msgs,
      null,
    );
  } catch {
    // best-effort: never break the user's session
  }
}

main().finally(() => process.exit(0));
