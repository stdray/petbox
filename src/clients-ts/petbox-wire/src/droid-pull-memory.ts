// Factory Droid SessionStart hook (global) — the droid port of pull-memory.ts.
//
// Injects the PetBox memory protocol + curated canon so the agent recalls relevant memory at
// session start and captures learnings as it works, via the already-connected petbox MCP.
//
// Droid names MCP tools `<server>___<tool>` (triple underscore) at runtime — observed live
// in exec mode; the docs' `mcp__<server>__<tool>` form did not match. The protocol renders
// with droidPetboxTool (`petbox___*`); the canon block stays byte-identical across agents. Droid's SessionStart stdin
// is snake_case (`session_id`, `transcript_path`, `cwd`, `source`), the same shape Claude Code
// uses, so we resolve the project from `cwd` and pass `source` through for the resume nudge.
//
// Output contract (docs): a SessionStart hook returns context to the model via the structured
// JSON `{ hookSpecificOutput: { hookEventName: "SessionStart", additionalContext } }` on stdout
// (stdout-as-context is also accepted, but the structured form is the documented preference).
//
// Best-effort, always exit 0, no output for an unregistered cwd.
//
// The banner's orchestrator notes resolve server → LKG cache → built-in default, same as
// `apply` (resolveAgentDefinitionForSession, wrapping agent-def-fetch.ts's
// resolveAgentDefinitionWithLkg). That fetch runs CONCURRENTLY with the canon fetch (both
// bounded by their own ~8s timeout) so the two budgets don't stack serially on session start.

import { resolveAgentDefinitionForSession } from "./agent-def-fetch.ts";
import { fetchCanonBlock } from "./canon.ts";
import { unrefLingeringHandles } from "./hook-drain.ts";
import { buildProtocol, droidPetboxTool } from "./protocol.ts";
import { resolveProject } from "./registry.ts";

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
// OS-level write completes. Awaiting the write callback guarantees the context JSON is fully
// flushed before main() resolves, so the process never ends mid-write and truncates it (see
// pull-memory.ts's identical helper for the full rationale).
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
    // Run concurrently: each is independently bounded, so total added wait stays ~8s, not ~16s.
    const [defResult, canon] = await Promise.all([
      resolveAgentDefinitionForSession(resolved),
      fetchCanonBlock(resolved),
    ]);
    let context = buildProtocol(resolved.project, droidPetboxTool, {
      source,
      harness: "droid",
      definition: defResult.definition,
    });
    // Append the curated memory canon when available (best-effort; degrades to nothing).
    if (canon) context += `\n\n${canon}`;
    const out = {
      hookSpecificOutput: {
        hookEventName: "SessionStart",
        additionalContext: context,
      },
    };
    await writeStdout(JSON.stringify(out));
  } catch {
    // best-effort
  }
}

// Exit cleanly instead of tearing the process down mid-close: a hard process.exit() while
// libuv handles from the concurrent HTTP fetches are still closing raced Windows' async
// handle teardown (`Assertion failed: !(handle->flags & UV_HANDLE_CLOSING), src\win\async.c`)
// and could truncate the stdout write above (fire-and-forget on a Windows pipe) — the same
// crash observed in pull-memory.ts (see its exit comment). Setting exitCode and returning lets
// Node drain the event loop naturally instead — `Connection: close` (canon.ts /
// agent-def-fetch.ts) covers a completed fetch, and unrefLingeringHandles covers a fetch
// aborted mid-flight against a stalled server (measured to leave its TLSSocket alive for
// several more seconds otherwise; see hook-drain.ts) so a slow session start can't turn into
// a multi-second stall on a handle nothing is still using.
main().finally(() => {
  process.exitCode = 0;
  unrefLingeringHandles();
});
