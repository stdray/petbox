// Codex CLI SessionStart hook (global) — the codex port of pull-memory.ts / droid-pull-memory.ts.
//
// Injects the PetBox memory protocol + curated canon so the agent recalls relevant memory at
// session start and captures learnings as it works, via the already-connected petbox MCP.
//
// Codex names MCP tools the SAME way Claude Code and Droid do — `mcp__petbox__<verb>` (codex-
// spec.md §9: `"mcp__" + sanitize(server) + "__" + sanitize(tool)`, and `petbox` sanitizes to
// itself) — so this reuses protocol.ts's codexPetboxTool (a plain alias of mcpPetboxTool).
//
// Codex's SessionStart stdin is snake_case (codex-spec.md §3, the generated JSON Schema
// fixtures): `session_id`, `transcript_path` (string|null), `cwd`, `hook_event_name`, `model`,
// `permission_mode`, `source` (startup|resume|clear|compact) — the same shape Claude Code/Droid
// use for the fields this hook actually reads (cwd, source), so we resolve the project from
// `cwd` and pass `source` through for the resume nudge exactly as the other two ports do.
//
// Output contract (codex-spec.md §3, schema.rs:398-403) — the SAME key Claude Code/Droid use:
// `{ hookSpecificOutput: { hookEventName: "SessionStart", additionalContext } }` on stdout.
//
// Best-effort, always exit 0, no output for an unregistered cwd — identical contract to
// pull-memory.ts / droid-pull-memory.ts; see those files' headers for the fuller rationale on
// the file-cascade banner, the broken-layer marker, and the Windows-pipe-flush / process-exit
// concerns this file mirrors verbatim.

import { resolveApplyRoot } from "./apply-root.ts";
import { fetchCanonBlock } from "./canon.ts";
import { resolveDefinitionForSession } from "./definition-source.ts";
import { unrefLingeringHandles } from "./hook-drain.ts";
import { buildProtocol, codexPetboxTool } from "./protocol.ts";
import { resolveProject } from "./registry.ts";
import { buildOwnerOnlySkillsBlock } from "./skill-files.ts";
import { buildStaleBaseWarning } from "./worktree-base-guard.ts";

// Same short budget as pull-memory.ts / droid-pull-memory.ts — see those files for the
// stale-but-instant-beats-fresh-but-late rationale; kept identical across all three ports.
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

// Same Windows-pipe-flush concern as pull-memory.ts's writeStdout — see that file for the
// full rationale.
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

    // Started concurrently, NOT awaited yet — same pattern as pull-memory.ts / droid-pull-
    // memory.ts: the guard's git-only latency hides behind the fetch sequence below instead of
    // stacking in front of it, and it stays out of SESSION_FETCH_BUDGET_MS entirely.
    const stalePromise = buildStaleBaseWarning({ cwd: cwd || process.cwd() });

    // File-only, no timeout to spend: the layers are already on this disk.
    const applyRoot = resolveApplyRoot(cwd || process.cwd()).root;
    const defResult = resolveDefinitionForSession({
      root: applyRoot,
      logSource: `codex-pull-memory[${resolved.project}]`,
    });
    const canon = await fetchCanonBlock(resolved, { timeoutMs: SESSION_FETCH_BUDGET_MS });

    let context = buildProtocol(resolved.project, codexPetboxTool, {
      source,
      harness: "codex",
      definition: defResult.definition,
    });
    // Append the curated memory canon when available (best-effort; degrades to nothing).
    if (canon) context += `\n\n${canon}`;
    // Owner-only skills (work: user-invocable-skills-invisible-to-model) — best-effort, degrades
    // to nothing when the project has none materialized. Codex has no skill surface this kit
    // writes to (skill-files.ts's SKILL_SURFACES does not include a codex entry — see this
    // task's report), so this always returns null today; kept for parity with the other two
    // ports and to pick up a future codex skill surface without another hook rewrite.
    const ownerOnlySkills = buildOwnerOnlySkillsBlock(applyRoot, "codex");
    if (ownerOnlySkills) context += `\n\n${ownerOnlySkills}`;
    // Broken-layer marker (spec broken-layer-fails-loudly) — same rationale and position as
    // pull-memory.ts / droid-pull-memory.ts: PREPENDED, not appended.
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

// Same exit-cleanly-not-hard-exit rationale as pull-memory.ts / droid-pull-memory.ts — see
// those files' identical comment for the full Windows libuv/socket-teardown race explanation.
main().finally(() => {
  process.exitCode = 0;
  unrefLingeringHandles();
});
