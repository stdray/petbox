// Qwen Code Stop + StopFailure hook (global) — the qwen port of push-session.ts /
// droid-push-session.ts / codex-push-session.ts.
//
// Mirrors the session conversation into PetBox's Session module so the board auto-populates.
// The project + API key are resolved from cwd via the shared registry; if the cwd is not a
// registered project this exits immediately (first guard, before any work).
//
// WHY Stop, NOT SessionEnd (qwen-spec.md §3): `SessionEnd` fires ONLY from UI/ACP call sites
// (acpAgent.ts:2951, AppContainer.tsx:1290, clearCommand.ts:67, start-opentui-ui.tsx:410) —
// nothing in `cli/src/nonInteractiveCli.ts` ever dispatches it, so it is dead code for every
// headless `qwen`/`qwen exec` run this kit's hooks are meant to cover. `Stop` instead fires from
// `packages/core` on the shared path both TUI and headless use (core/src/core/client.ts:4356-
// 4390), corroborated by `nonInteractiveCli.ts`'s handling of the `'stop-hook-cap'` interruption
// cause — dead code unless Stop hooks run there too. `StopFailure` is hooked ALONGSIDE it
// (registered as a second event pointing at this SAME script) because it fires INSTEAD of Stop
// when a turn dies on an API error (qwen-spec.md §3, types.ts:60-61) — without it, a session
// that ends on an API error would never get pushed at all.
//
// CRITICAL — Stop fires ONCE PER TURN END, not once per process (qwen-spec.md §3): a multi-turn
// headless run fires this hook repeatedly, so the push MUST be idempotent. It already is, for
// the exact same reason push-session.ts's Claude Code Stop hook already is: this process is
// fresh each invocation (no remembered cursor), so it hands `pushTranscript` a `null` cursor and
// that function optimistically resends a small idempotent overlap window; a contiguity gap comes
// back as a structured 409 with the server's lastOrdinal and the tail is resent from there (see
// append.ts). No qwen-specific idempotency logic is needed — this is the SAME mechanism Claude
// Code's own Stop hook already relies on, because Claude Code's Stop ALSO fires more than once
// per session (once per turn, there too) and push-session.ts already solved this.
//
// We parse the qwen chat-recording JSONL (buildQwenMessages: user/assistant TEXT turns only,
// tool calls/outputs and `system`-type records excluded — see qwen-transcript.ts) and push only
// the INCREMENT via the server-authoritative append cursor. Old servers without the append route
// fall back to the legacy full-snapshot push. Best-effort: every failure is swallowed and we
// ALWAYS exit 0 — never break the user's session.
//
// Qwen's Stop/StopFailure stdin is snake_case and Claude-Code-shaped (qwen-spec.md §3,
// core/src/hooks/types.ts:260-268): `session_id`, `transcript_path`, `cwd`, `hook_event_name`,
// `timestamp`. Unlike Codex's SessionEnd, `transcript_path` is NOT documented as nullable here —
// still guarded the same defensive way (a blank/absent value is a silent no-op, never a throw),
// since `--chat-recording false` makes `getTranscriptPath()` return `''` (qwen-spec.md §9) and a
// hook must survive that configuration too.

import { pushTranscript } from "./append.ts";
import { collectQwenMainRun, collectQwenSubagentRuns, buildQwenMessages } from "./qwen-transcript.ts";
import { unrefLingeringHandles } from "./hook-drain.ts";
import { resolveProject, UnresolvedEnvRefError } from "./registry.ts";
import type { MainRun, Msg } from "./transcript.ts";

const FETCH_TIMEOUT_MS = 12000;

type HookInput = {
  session_id?: string;
  transcript_path?: string;
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
    let resolved: ReturnType<typeof resolveProject>;
    try {
      resolved = resolveProject(j.cwd ?? "");
    } catch (e) {
      // UnresolvedEnvRefError (registry.ts) is NOT the ordinary best-effort case this hook's
      // outer catch swallows — see push-session.ts's identical catch for the full rationale.
      // A Stop hook has no additionalContext-style channel back into the session it just ended,
      // so stderr plus registry.ts's own wire.log trace is the loudest this hook can be.
      if (e instanceof UnresolvedEnvRefError) console.error(e.message);
      return;
    }
    if (!resolved) return;

    const sid = (j.session_id ?? "").trim();
    const tp = (j.transcript_path ?? "").trim();
    if (!sid || !tp) return;

    let msgs: Msg[];
    try {
      msgs = await buildQwenMessages(tp);
    } catch {
      return; // transcript missing/unreadable
    }
    if (msgs.length === 0) return; // empty body → server returns 400, don't push

    // Best-effort: a failure to collect subagent-run provenance must never block the push.
    // See qwen-transcript.ts: role + spawn-time override are recoverable; actual model is NOT.
    let subagentRuns: Awaited<ReturnType<typeof collectQwenSubagentRuns>> = [];
    try {
      subagentRuns = await collectQwenSubagentRuns(tp);
    } catch {
      subagentRuns = [];
    }
    // The main loop's OWN model, recovered from qwen's `ui_telemetry` api_response records
    // (qwen-transcript.ts's collectQwenMainRun). qwen is the harness measured to read the
    // SessionStart banner and decline the self-intro line, so prose is exactly what cannot be
    // trusted here. Best-effort, same as subagentRuns.
    let mainRun: MainRun | undefined;
    try {
      mainRun = await collectQwenMainRun(tp);
    } catch {
      mainRun = undefined;
    }
    const extraMeta =
      subagentRuns.length > 0 || mainRun
        ? { ...(subagentRuns.length > 0 ? { subagentRuns } : {}), ...(mainRun ? { mainRun } : {}) }
        : undefined;

    // Fresh process each turn → no remembered cursor (null): pushTranscript guesses an
    // idempotent overlap window and self-heals off the server's structured gap reject — the
    // SAME mechanism that already makes Claude Code's own (also-fires-per-turn) Stop hook safe.
    await pushTranscript(
      {
        baseUrl: resolved.baseUrl,
        project: resolved.project,
        sessionId: sid,
        apiKey: resolved.apiKey,
        agent: "qwen",
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

// Same exit-cleanly-not-hard-exit rationale as push-session.ts / droid-push-session.ts /
// codex-push-session.ts — see those files' identical comment for the full Windows libuv/socket-
// teardown race explanation.
main().finally(() => {
  process.exitCode = 0;
  unrefLingeringHandles();
});
