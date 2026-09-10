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
// The banner's orchestrator notes come from the FILE cascade base < user < project
// (definition-source.ts's resolveDefinitionForSession) — verbatim the same model as
// pull-memory.ts (this file's Claude Code counterpart), and no network at all. The canon fetch
// is now the only network call left on this path and gets the whole SESSION_FETCH_BUDGET_MS.
// A broken layer degrades the banner loudly (a marker line naming the file, a wire.log trace,
// the protocol rendered from the kit base) and never crashes the hook.

import { resolveApplyRoot } from "./apply-root.ts";
import { fetchCanonBlock } from "./canon.ts";
import { resolveDefinitionForSession } from "./definition-source.ts";
import { unrefLingeringHandles } from "./hook-drain.ts";
import { buildProtocol, droidPetboxTool } from "./protocol.ts";
import { resolveProject, UnresolvedEnvRefError } from "./registry.ts";
import { buildOwnerOnlySkillsBlock } from "./skill-files.ts";
import { buildStaleBaseWarning } from "./worktree-base-guard.ts";

// Wall-clock budget for the one remaining fetch on this path (canon) — see the module comment
// above. Kept in step with pull-memory.ts: short on purpose, because the canon has an offline
// cache and a redeploy restarting the server must not tax every session start with an 8s stall.
// Stale-but-instant beats fresh-but-late here.
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

  let resolved: ReturnType<typeof resolveProject>;
  try {
    resolved = resolveProject(cwd);
  } catch (e) {
    // UnresolvedEnvRefError (registry.ts) is NOT the ordinary best-effort case below — see
    // pull-memory.ts's identical catch for the full rationale (decision 2, card
    // keys-json-supports-env-var-references). registry.ts already traced this to wire.log; this
    // ALSO surfaces it in-session, via the SAME additionalContext channel this hook's normal
    // output uses, plus stderr. Every other exception here still falls through to the ordinary
    // best-effort catch below untouched.
    if (e instanceof UnresolvedEnvRefError) {
      console.error(e.message);
      await writeStdout(
        JSON.stringify({
          hookSpecificOutput: { hookEventName: "SessionStart", additionalContext: `⚠ ${e.message}` },
        }),
      );
    }
    return;
  }
  if (!resolved) return; // not a registered project → no output

  try {
    // Started concurrently, NOT awaited yet — same pattern as pull-memory.ts (its Claude Code
    // counterpart): the guard's git-only latency hides behind the fetch sequence below instead
    // of stacking in front of it, and it stays out of SESSION_FETCH_BUDGET_MS entirely.
    const stalePromise = buildStaleBaseWarning({ cwd: cwd || process.cwd() });

    // File-only, no timeout to spend: the layers are already on this disk.
    const applyRoot = resolveApplyRoot(cwd || process.cwd()).root;
    const defResult = resolveDefinitionForSession({
      root: applyRoot,
      logSource: `droid-pull-memory[${resolved.project}]`,
    });
    const canon = await fetchCanonBlock(resolved, { timeoutMs: SESSION_FETCH_BUDGET_MS });

    let context = buildProtocol(resolved.project, droidPetboxTool, {
      source,
      harness: "droid",
      definition: defResult.definition,
    });
    // Append the curated memory canon when available (best-effort; degrades to nothing).
    if (canon) context += `\n\n${canon}`;
    // Owner-only skills (work: user-invocable-skills-invisible-to-model) — best-effort, degrades
    // to nothing when the project has none materialized. Droid reads the same SKILL.md shape as
    // Claude Code, so it gets the Claude-Code wording (skill-files.ts's buildOwnerOnlySkillsBlock).
    const ownerOnlySkills = buildOwnerOnlySkillsBlock(applyRoot, "droid");
    if (ownerOnlySkills) context += `\n\n${ownerOnlySkills}`;
    // Broken-layer marker (spec broken-layer-fails-loudly) — same rationale, and same POSITION,
    // as pull-memory.ts's: PREPENDED, not appended. The one line that explains why the protocol
    // below it is the kit base rather than this machine's layers must not be the part a tail
    // truncation eats. (It used to be appended here, which put it last in the context.)
    const defNote = defResult.note;
    if (defNote) context = `${defNote}\n\n${context}`;
    // Prepend the stale-base warning, highest priority so it heads the context.
    const staleWarn = await stalePromise;
    context = staleWarn + context;
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
// Node drain the event loop naturally instead — `Connection: close` (canon.ts)
// covers a completed fetch, and unrefLingeringHandles covers a fetch
// aborted mid-flight against a stalled server (measured to leave its TLSSocket alive for
// several more seconds otherwise; see hook-drain.ts) so a slow session start can't turn into
// a multi-second stall on a handle nothing is still using.
main().finally(() => {
  process.exitCode = 0;
  unrefLingeringHandles();
});
