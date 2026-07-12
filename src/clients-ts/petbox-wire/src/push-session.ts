// Claude Code Stop hook (global) — port of push-session.ps1.
//
// Mirrors the session conversation into PetBox's Session module so the board auto-populates.
// The project + API key are resolved from cwd via the shared registry; if the cwd is not a
// registered project this exits immediately (first guard, before any work).
//
// Reads the full transcript JSONL, extracts the user/assistant text turns (tool dumps and
// system reminders excluded), and pushes only the INCREMENT via the server-authoritative
// append cursor (see append.ts): this process is fresh each turn, so it optimistically
// resends a small idempotent overlap window; a contiguity gap comes back as a structured
// 409 with the server's lastOrdinal and the tail is resent from there. Old servers without
// the append route fall back to the legacy full-snapshot push. Best-effort: every failure
// is swallowed and we ALWAYS exit 0 — never break the user's session.

import { pushTranscript } from "./append.ts";
import { unrefLingeringHandles } from "./hook-drain.ts";
import { resolveProject } from "./registry.ts";
import { buildMessages, type Msg } from "./transcript.ts";
// Observed role binding is stamped inside pushTranscript (X-PetBox-Session-Meta via
// resolveObservedBinding) — server stores observation only; local roles.json is SoT.

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

    // Fresh process each turn → no remembered cursor (null): pushTranscript guesses an
    // idempotent overlap window and self-heals off the server's structured gap reject.
    await pushTranscript(
      {
        baseUrl: resolved.baseUrl,
        project: resolved.project,
        sessionId: sid,
        apiKey: resolved.apiKey,
        agent: "claude-code",
        timeoutMs: FETCH_TIMEOUT_MS,
      },
      msgs,
      null,
    );
  } catch {
    // best-effort: never break the user's session
  }
}

// Exit cleanly instead of tearing the process down mid-close: a hard process.exit() while
// libuv handles from the HTTP push above are still closing can race Windows' async handle
// teardown (`Assertion failed: !(handle->flags & UV_HANDLE_CLOSING), src\win\async.c`) — the
// same crash observed in pull-memory.ts (see its exit comment). Setting exitCode and returning
// lets Node drain the event loop naturally instead — `Connection: close` (append.ts) covers a
// completed push, and unrefLingeringHandles covers a push aborted mid-flight against a stalled
// server (measured to leave its TLSSocket alive for several more seconds otherwise; see
// hook-drain.ts) so a slow Stop hook can't turn into a multi-second stall on a handle nothing
// is still using.
main().finally(() => {
  process.exitCode = 0;
  unrefLingeringHandles();
});
