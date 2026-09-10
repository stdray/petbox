// Claude Code SessionStart hook (global) — port of pull-memory.ps1.
//
// Injects the PetBox memory protocol so the agent recalls relevant memory at session start
// and captures learnings as it works, via the already-connected petbox MCP (native memory_*
// tools). Stdout is added to the session context by Claude Code.
//
// The project is resolved from cwd via the shared registry; if the cwd is not a registered
// project this prints nothing and exits 0. Best-effort, never blocks — always exit 0.
//
// The banner's orchestrator notes come from the FILE cascade base < user < project
// (definition-source.ts's resolveDefinitionForSession) — the same resolve `apply` compiles from,
// and no network at all. It used to be an HTTP fetch, and it was the FIRST thing every session
// start on every harness did (card wire-stops-fetching-definition). Removing it leaves the canon
// fetch as the only network call on this path, so it now gets the whole
// SESSION_FETCH_BUDGET_MS instead of the remainder of it.
//
// A BROKEN layer does not stop the session — a SessionStart hook that crashes is worse than one
// that degrades — but it does not pass silently either: the banner LEADS with the path of the
// file that broke, ~/.petbox/wire.log gets a Class-B trace, and the protocol underneath is
// rendered from the kit base. Loudness lives in stdout here, not in the exit code.

import { resolveApplyRoot } from "./apply-root.ts";
import { fetchCanonBlock } from "./canon.ts";
import { resolveDefinitionForSession } from "./definition-source.ts";
import { unrefLingeringHandles } from "./hook-drain.ts";
import { buildProtocol, mcpPetboxTool } from "./protocol.ts";
import { resolveProject, UnresolvedEnvRefError } from "./registry.ts";
import {
  assembleSessionBanner,
  describeCanonDegradation,
  HARNESS_INLINE_HARD_LIMIT_BYTES,
  logBudgetOverage,
  SESSION_BANNER_BUDGET_BYTES,
} from "./session-budget.ts";
import { buildOwnerOnlySkillsBlock } from "./skill-files.ts";
import { buildStaleBaseWarning } from "./worktree-base-guard.ts";

// Wall-clock budget for the one remaining fetch on this path (canon).
//
// Deliberately SHORT. Waiting long for the server only pays off when the alternative is
// nothing — and it isn't: the canon has an offline cache on disk and changes on the order of
// weeks, not sessions. So a slow server (a redeploy restarting the container is the common
// case) must cost the session start ~2s, not ~8s. Freshness is only lost in the narrow window
// where the canon changed AND the server is down right now; staleness there is cheap, an 8s
// stall on every session start is not.
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

  let resolved: ReturnType<typeof resolveProject>;
  try {
    resolved = resolveProject(cwd);
  } catch (e) {
    // UnresolvedEnvRefError (registry.ts) is NOT the ordinary best-effort case below: the
    // project IS wired and a key SHOULD exist, so staying quiet here would be indistinguishable
    // from "never wired" (decision 2, card keys-json-supports-env-var-references). registry.ts
    // already traced this to wire.log (Class-Б); this ALSO surfaces it in-session, on both the
    // channel the agent's context is built from and stderr — wire-log.ts's own contract permits
    // an interactive caller to add exactly this. Every other exception here still falls through
    // to the ordinary best-effort catch below untouched.
    if (e instanceof UnresolvedEnvRefError) {
      console.error(e.message);
      await writeStdout(`⚠ ${e.message}\n`);
    }
    return;
  }
  if (!resolved) return; // not a registered project → no output

  try {
    // Started concurrently, NOT awaited yet — its git-only latency hides behind the
    // agent-def/canon fetch sequence below rather than stacking in front of it. It is
    // git-only and independent of SESSION_FETCH_BUDGET_MS, so it is deliberately not folded
    // into that budget or its overage accounting (see worktree-base-guard.ts).
    const stalePromise = buildStaleBaseWarning({ cwd: cwd || process.cwd() });

    // File-only, no timeout to spend: the layers are already on this disk.
    const applyRoot = resolveApplyRoot(cwd || process.cwd()).root;
    const defResult = resolveDefinitionForSession({
      root: applyRoot,
      logSource: `pull-memory[${resolved.project}]`,
    });
    const canon = await fetchCanonBlock(resolved, { timeoutMs: SESSION_FETCH_BUDGET_MS });

    const protocol = buildProtocol(resolved.project, mcpPetboxTool, {
      source,
      harness: "claude-code",
      definition: defResult.definition,
    });
    // Owner-only skills (work: user-invocable-skills-invisible-to-model) — best-effort, `null`
    // when the project has none materialized. Woven into the SAME ladder as canon below, not
    // appended afterward unconditionally: a fixed-size trailing block on top of an
    // already-budget-fitting protocol+canon blew the harness's 10 000 B hard limit on EVERY
    // session in the one real project this fix was for ($system: protocol ~5.2KB + canon ~4KB
    // already used the whole 9 400 B budget before this block's ~1.3KB was even considered) —
    // exactly the "text claims a fix that never reaches the agent" failure this card exists to
    // close. assembleSessionBanner's ladder now ranks it between the canon legs (see that
    // function's own comment for the reasoning) instead.
    const ownerOnlySkills = buildOwnerOnlySkillsBlock(applyRoot, "claude-code");
    // Append the curated memory canon and the owner-only-skills block only as far as the ladder
    // (session-budget.ts) can fit them — the mandatory protocol block (gates, self-intro,
    // search-before-rework) must never be put at risk of the harness's own byte-offset
    // truncation by oversized content riding along after it.
    const banner = assembleSessionBanner(protocol, canon, SESSION_BANNER_BUDGET_BYTES, ownerOnlySkills);
    if (banner.overBudget) {
      // A breakage, not an expected absence (wire-silent-failures-invisible taxonomy) — log
      // loudly rather than silently ship a banner the harness will itself guillotine.
      await logBudgetOverage(
        `pull-memory[${resolved.project}]: session banner exceeded budget — ` +
          `protocol=${banner.protocolBytes}B canon=${banner.canonBytes}B ` +
          `ownerOnlySkills=${banner.extraBytes}B (${banner.extraIncluded ? "kept" : "DROPPED"}) ` +
          `budget=${SESSION_BANNER_BUDGET_BYTES}B hard-limit=${HARNESS_INLINE_HARD_LIMIT_BYTES}B — ` +
          `canon ${describeCanonDegradation(banner)}. ` +
          `Shrink the canon (memory_upsert store canon key index) or raise the budget deliberately.`,
      );
    }
    // Resolve the guard now (it had the whole fetch sequence above to run in the background)
    // and prepend it: highest priority, tiny, so it must survive any tail truncation of the
    // rest of the banner rather than risk being the part that gets cut.
    const staleWarn = await stalePromise;
    // Broken-layer marker (spec broken-layer-fails-loudly): "" whenever the cascade resolved
    // cleanly, which is every healthy session. When it is not empty it names the file that broke,
    // by absolute path, and says the protocol below it came from the kit base instead. Same
    // treatment as staleWarn: tiny and prepended OUTSIDE the byte-budget accounting, so the one
    // line that explains the degradation cannot itself be the part that gets truncated away.
    const defNote = defResult.note;
    await writeStdout(staleWarn + (defNote ? defNote + "\n" : "") + banner.text);
  } catch {
    // best-effort
  }
}

// Exit cleanly instead of tearing the process down mid-close: a hard process.exit() while
// libuv handles from the HTTP fetches above are still closing raced Windows' async
// handle teardown (`Assertion failed: !(handle->flags & UV_HANDLE_CLOSING), src\win\async.c`)
// and could truncate the stdout write above (fire-and-forget on a Windows pipe). Setting
// exitCode and returning lets Node drain the event loop naturally instead — `Connection:
// close` (canon.ts) means a request that got a full response never
// leaves a keep-alive socket behind, and unrefLingeringHandles covers the other case
// (a request aborted mid-flight against a genuinely stalled server can leave its TLSSocket
// alive for several MORE seconds even with Connection: close — measured, not assumed; see
// hook-drain.ts) so a slow session start can't turn into an ~18s stall waiting on a socket
// nothing is still using.
main().finally(() => {
  process.exitCode = 0;
  unrefLingeringHandles();
});
