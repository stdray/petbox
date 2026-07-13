// Claude Code SessionStart hook (global) — port of pull-memory.ps1.
//
// Injects the PetBox memory protocol so the agent recalls relevant memory at session start
// and captures learnings as it works, via the already-connected petbox MCP (native memory.*
// tools). Stdout is added to the session context by Claude Code.
//
// The project is resolved from cwd via the shared registry; if the cwd is not a registered
// project this prints nothing and exits 0. Best-effort, never blocks — always exit 0.
//
// The banner's orchestrator notes resolve server → LKG cache → built-in default, same as
// `apply` (resolveAgentDefinitionForSession, wrapping agent-def-fetch.ts's
// resolveAgentDefinitionWithLkg). That fetch and the canon fetch run SEQUENTIALLY under one
// shared SESSION_FETCH_BUDGET_MS wall-clock budget (not Promise.all'd): the happy path for
// both requests together is ~100-200ms, so concurrency bought nothing there, and it made the
// two independent 8s timeouts stack in the worst case anyway if reasoned about naively.
// The real worst case — PetBox stalling under load from parallel agents — is handled by
// giving the whole hook one budget: whatever the agent-def fetch doesn't spend, the canon
// fetch inherits as its own timeout, so the combined worst case stays ~SESSION_FETCH_BUDGET_MS,
// not 2x it. A fetch that starts with little/no budget left degrades to its own fallback
// (LKG cache / built-in default / no canon) rather than blocking — see canon.ts's fetchCanon.

import { resolveAgentDefinitionForSession } from "./agent-def-fetch.ts";
import { fetchCanonBlock } from "./canon.ts";
import { unrefLingeringHandles } from "./hook-drain.ts";
import { buildProtocol, mcpPetboxTool } from "./protocol.ts";
import { resolveProject } from "./registry.ts";

// Shared wall-clock budget for BOTH fetches combined (agent-def, then canon) — see the
// module comment above.
//
// Deliberately SHORT. Waiting long for the server only pays off when the alternative is
// nothing — and it isn't: the LKG cache holds the same definition and canon, which change
// on the order of weeks, not sessions. So a slow server (a redeploy restarting the
// container is the common case) must cost the session start ~2s, not ~8s. Freshness is
// only lost in the narrow window where the documents changed AND the server is down right
// now; staleness there is cheap, an 8s stall on every session start is not.
const SESSION_FETCH_BUDGET_MS = 2000;

type HookInput = { cwd?: string; source?: string };

function readStdin(): Promise<string> {
  return new Promise((resolve) => {
    let buf = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (c) => (buf += c));
    process.stdin.on("end", () => resolve(buf));
    process.stdin.on("error", () => resolve(buf));
  });
}

// process.stdout.write() on a Windows pipe is asynchronous — the call can return before the
// OS-level write completes. Awaiting the write callback guarantees the banner is fully flushed
// before main() resolves, so the process never ends mid-write and truncates the banner (a
// slower server / bigger canon could otherwise ship a partial banner into the agent's context,
// silently — see the exit comment below for why we no longer race this against process.exit()).
function writeStdout(text: string): Promise<void> {
  return new Promise((resolve) => {
    if (text.length === 0) {
      resolve();
      return;
    }
    process.stdout.write(text, () => resolve());
  });
}

async function main(): Promise<void> {
  let source = "startup";
  let cwd = "";
  try {
    const raw = await readStdin();
    const j: HookInput = JSON.parse(raw);
    if (typeof j.source === "string" && j.source.trim()) source = j.source.trim();
    if (typeof j.cwd === "string") cwd = j.cwd;
  } catch {
    // fall through with defaults; cwd stays empty → resolves to null below
  }

  try {
    const resolved = resolveProject(cwd);
    if (!resolved) return; // not a registered project → no output

    // Sequential under one shared budget (not Promise.all): whatever the first fetch doesn't
    // spend is what the second gets, so the combined worst case is bounded by
    // SESSION_FETCH_BUDGET_MS instead of stacking two independent timeouts.
    const budgetStart = Date.now();
    const defResult = await resolveAgentDefinitionForSession(resolved, {
      timeoutMs: SESSION_FETCH_BUDGET_MS,
    });
    const remainingMs = SESSION_FETCH_BUDGET_MS - (Date.now() - budgetStart);
    const canon = await fetchCanonBlock(resolved, { timeoutMs: remainingMs });

    let out = buildProtocol(resolved.project, mcpPetboxTool, {
      source,
      harness: "claude-code",
      definition: defResult.definition,
    });
    // Append the curated memory canon when available (best-effort; degrades to nothing).
    if (canon) out += `\n\n${canon}`;
    await writeStdout(out);
  } catch {
    // best-effort
  }
}

// Exit cleanly instead of tearing the process down mid-close: a hard process.exit() while
// libuv handles from the HTTP fetches above are still closing raced Windows' async
// handle teardown (`Assertion failed: !(handle->flags & UV_HANDLE_CLOSING), src\win\async.c`)
// and could truncate the stdout write above (fire-and-forget on a Windows pipe). Setting
// exitCode and returning lets Node drain the event loop naturally instead — `Connection:
// close` (canon.ts / agent-def-fetch.ts) means a request that got a full response never
// leaves a keep-alive socket behind, and unrefLingeringHandles covers the other case
// (a request aborted mid-flight against a genuinely stalled server can leave its TLSSocket
// alive for several MORE seconds even with Connection: close — measured, not assumed; see
// hook-drain.ts) so a slow session start can't turn into an ~18s stall waiting on a socket
// nothing is still using.
main().finally(() => {
  process.exitCode = 0;
  unrefLingeringHandles();
});
