// Qwen Code SessionStart hook (global) — the qwen port of pull-memory.ts / droid-pull-memory.ts
// / codex-pull-memory.ts.
//
// Injects the PetBox memory protocol + curated canon so the agent recalls relevant memory at
// session start and captures learnings as it works, via the already-connected petbox MCP.
//
// Qwen names MCP tools the SAME way Claude Code, Droid and Codex do — `mcp__petbox__<verb>`
// (qwen-spec.md §10: `mcp__${serverName}__${serverToolName}`) — so this reuses protocol.ts's
// qwenPetboxTool (a plain alias of mcpPetboxTool).
//
// Qwen's SessionStart stdin is snake_case and Claude-Code-shaped (qwen-spec.md §3,
// core/src/hooks/types.ts:1030): `session_id`, `transcript_path`, `cwd`, `hook_event_name`,
// `timestamp`, plus `permission_mode`, `source` (startup|resume|clear|compact), `model`,
// `agent_type?` — the same shape Claude Code/Droid/Codex use for the fields this hook actually
// reads (cwd, source), so we resolve the project from `cwd` and pass `source` through for the
// resume nudge exactly as the other three ports do.
//
// Output contract (qwen-spec.md §3, types.ts:1040-1045) — the SAME key Claude Code/Droid/Codex
// use: `{ hookSpecificOutput: { hookEventName: "SessionStart", additionalContext } }` on stdout.
//
// CRITICAL: this hook must NEVER be registered with `"async": true` (wire.ts's installGlobalHooks
// — qwen-spec.md §3, empirically proven: the async path returns a synthetic `{continue:true}`
// with no `hookSpecificOutput` at the moment SessionStart is consumed, and the real stdout lands
// too late, after the first model request is already built). This file has no control over how
// it is registered — the invariant lives at the install site — but the reasoning is recorded
// here too since this is the file whose output would silently go nowhere if that were violated.
//
// Best-effort, always exit 0, no output for an unregistered cwd — identical contract to
// pull-memory.ts / droid-pull-memory.ts / codex-pull-memory.ts; see those files' headers for the
// fuller rationale on the file-cascade banner, the broken-layer marker, and the Windows-pipe-
// flush / process-exit concerns this file mirrors verbatim.

import { resolveApplyRoot } from "./apply-root.ts";
import { fetchCanonBlock } from "./canon.ts";
import { resolveDefinitionForSession } from "./definition-source.ts";
import { unrefLingeringHandles } from "./hook-drain.ts";
import { buildProtocol, qwenPetboxTool } from "./protocol.ts";
import { resolveProject, UnresolvedEnvRefError } from "./registry.ts";
import { buildOwnerOnlySkillsBlock } from "./skill-files.ts";
import { buildStaleBaseWarning } from "./worktree-base-guard.ts";

// Same short budget as pull-memory.ts / droid-pull-memory.ts / codex-pull-memory.ts — see those
// files for the stale-but-instant-beats-fresh-but-late rationale; kept identical across all
// four ports.
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
    // Started concurrently, NOT awaited yet — same pattern as the other three ports: the
    // guard's git-only latency hides behind the fetch sequence below instead of stacking in
    // front of it, and it stays out of SESSION_FETCH_BUDGET_MS entirely.
    const stalePromise = buildStaleBaseWarning({ cwd: cwd || process.cwd() });

    // File-only, no timeout to spend: the layers are already on this disk.
    const applyRoot = resolveApplyRoot(cwd || process.cwd()).root;
    const defResult = resolveDefinitionForSession({
      root: applyRoot,
      logSource: `qwen-pull-memory[${resolved.project}]`,
    });
    const canon = await fetchCanonBlock(resolved, { timeoutMs: SESSION_FETCH_BUDGET_MS });

    let context = buildProtocol(resolved.project, qwenPetboxTool, {
      source,
      harness: "qwen",
      definition: defResult.definition,
    });
    // Append the curated memory canon when available (best-effort; degrades to nothing).
    if (canon) context += `\n\n${canon}`;
    // Owner-only skills (work: user-invocable-skills-invisible-to-model) — best-effort, degrades
    // to nothing when the project has none materialized. Qwen has no skill surface this kit
    // writes to (skill-files.ts's SKILL_SURFACES does not include a qwen entry — see this
    // task's report), so this always returns null today; kept for parity with the other three
    // ports and to pick up a future qwen skill surface without another hook rewrite.
    const ownerOnlySkills = buildOwnerOnlySkillsBlock(applyRoot, "qwen");
    if (ownerOnlySkills) context += `\n\n${ownerOnlySkills}`;
    // Broken-layer marker (spec broken-layer-fails-loudly) — same rationale and position as the
    // other three ports: PREPENDED, not appended.
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

// Same exit-cleanly-not-hard-exit rationale as pull-memory.ts / droid-pull-memory.ts /
// codex-pull-memory.ts — see those files' identical comment for the full Windows libuv/socket-
// teardown race explanation.
main().finally(() => {
  process.exitCode = 0;
  unrefLingeringHandles();
});
