// Codex CLI SessionEnd hook (global) — the codex port of push-session.ts / droid-push-session.ts.
//
// Mirrors the session conversation into PetBox's Session module so the board auto-populates.
// The project + API key are resolved from cwd via the shared registry; if the cwd is not a
// registered project this exits immediately (first guard, before any work).
//
// Codex's SessionEnd stdin is snake_case (codex-spec.md §3): `session_id`, `transcript_path`
// (string|null), `cwd`, `hook_event_name`, `reason` (currently always "other"). **`transcript_path`
// can be null** — codex flushes the rollout right before dispatching SessionEnd hooks, but a
// flush failure is only WARNED, never surfaced to the hook payload as an error; this hook treats
// a null/blank transcript_path exactly like droid-push-session.ts treats a missing one: a silent,
// best-effort no-op, never a throw. SessionEnd is also root-session-only (codex-spec.md §3:
// "skipped for subagents") — nothing here needs to special-case a subagent invocation because
// codex itself never dispatches this hook for one.
//
// We parse the codex rollout JSONL (buildCodexMessages: user/assistant TEXT turns only, tool
// calls/outputs and non-response_item lines excluded — see codex-transcript.ts) and push only
// the INCREMENT via the server-authoritative append cursor (see append.ts): this process is
// fresh each turn, so it passes null and pushTranscript optimistically resends a small
// idempotent overlap window; a contiguity gap comes back as a structured 409 with the server's
// lastOrdinal and the tail is resent from there. Old servers without the append route fall back
// to the legacy full-snapshot push. Best-effort: every failure is swallowed and we ALWAYS exit
// 0 — never break the user's session.
//
// OPERATIONAL NOTE (codex-spec.md §3): codex caps a SessionEnd hook's own command timeout at 3
// SECONDS regardless of what config.toml requests (normalize_command_hook clamps it to [1,3]) —
// far tighter than the 12s FETCH_TIMEOUT_MS below. A push that takes longer than ~3s wall-clock
// (codex's own process-kill, not this file's fetch timeout) is simply cut off mid-flight; this
// is a real, codex-specific constraint the other two ports do not have — see this task's report.

import { pushTranscript } from "./append.ts";
import { collectCodexSubagentRuns, buildCodexMessages } from "./codex-transcript.ts";
import { unrefLingeringHandles } from "./hook-drain.ts";
import { resolveProject } from "./registry.ts";
import type { Msg } from "./transcript.ts";

const FETCH_TIMEOUT_MS = 12000;

type HookInput = {
  session_id?: string;
  transcript_path?: string | null;
  cwd?: string;
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

    // FIRST guard: not a registered project → silent no-op (before any file/network work).
    const resolved = resolveProject(j.cwd ?? "");
    if (!resolved) return;

    const sid = (j.session_id ?? "").trim();
    // transcript_path CAN BE NULL (codex-spec.md §3) — treat exactly like an absent one.
    const tp = typeof j.transcript_path === "string" ? j.transcript_path.trim() : "";
    if (!sid || !tp) return;

    let msgs: Msg[];
    try {
      msgs = await buildCodexMessages(tp);
    } catch {
      return; // transcript missing/unreadable
    }
    if (msgs.length === 0) return; // empty body → server returns 400, don't push

    // Best-effort: a failure to collect subagent-run provenance must never block the push.
    // See codex-transcript.ts: role + spawn-time override are recoverable; actual model is NOT.
    let subagentRuns: Awaited<ReturnType<typeof collectCodexSubagentRuns>> = [];
    try {
      subagentRuns = await collectCodexSubagentRuns(tp);
    } catch {
      subagentRuns = [];
    }
    const extraMeta = subagentRuns.length > 0 ? { subagentRuns } : undefined;

    // Fresh process each turn → no remembered cursor (null): pushTranscript guesses an
    // idempotent overlap window and self-heals off the server's structured gap reject.
    await pushTranscript(
      {
        baseUrl: resolved.baseUrl,
        project: resolved.project,
        sessionId: sid,
        apiKey: resolved.apiKey,
        agent: "codex",
        timeoutMs: FETCH_TIMEOUT_MS,
      },
      msgs,
      null,
      extraMeta,
    );
  } catch {
    // best-effort: never break the user's session
  }
}

// Same exit-cleanly-not-hard-exit rationale as push-session.ts / droid-push-session.ts — see
// those files' identical comment for the full Windows libuv/socket-teardown race explanation.
main().finally(() => {
  process.exitCode = 0;
  unrefLingeringHandles();
});
