// Worktree base staleness guard — folded into the SessionStart hook (pull-memory.ts) as a
// loud, non-blocking warning instead of yet another memory note.
//
// WHY THIS EXISTS: the primary checkout can sit in a DETACHED HEAD parked far behind
// origin/main for weeks — worktrees and orchestrator measurements silently inherit that stale
// base, so gates pass but prove nothing against current main. This exact fact has been
// auto-captured into memory 4x already and still gets stepped on every time, because memory is
// read-if-you-look, not read-if-you-don't. This is the mechanical fix instead: a SessionStart
// line the agent cannot avoid seeing, not another fact competing for attention in a canon block.
//
// THE DISCRIMINATOR (ahead===0 && behind>=threshold): a real feature/work branch has ahead>0
// (it carries its own commits on top of the base) and MUST stay silent — this guard exists
// specifically to catch a checkout that is a pure ANCESTOR of the remote default branch, i.e.
// parked, not diverged-and-working. Firing on ahead>0 would be a false alarm on every normal
// branch, which is precisely the kind of noise that trains people to ignore the warning.
//
// CONTRACT mirrors the rest of this kit (registry.ts's resolveProject, apply-root.ts's
// defaultGitToplevel): NEVER throws, best-effort only. Any uncertainty (no git, no network, no
// remote ref, garbage output, non-git cwd) resolves to "" — silence, not a false alarm and not
// a crash. The `.claude/allow-stale-base` marker lets a deliberately-stale checkout (a bisect,
// a frozen demo) opt out without touching the owner's global settings.json.

import { execFileSync } from "node:child_process";
import { existsSync } from "node:fs";
import { join } from "node:path";

// Injectable so tests need neither real git nor network — see worktree-base-guard.test.ts.
// Synchronous by design: this mirrors apply-root.ts's defaultGitToplevel and lets the default
// implementation be a plain execFileSync wrapper with no promise plumbing of its own.
export type GitRunner = (args: string[], timeoutMs?: number) => { code: number; stdout: string };

const DEFAULT_FETCH_TIMEOUT_MS = 3500;
const DEFAULT_THRESHOLD = 10;
const FALLBACK_BRANCH = "origin/main";
const DEFAULT_FETCH_MIN_INTERVAL_MS = 60_000;

// MODULE-LEVEL throttle for the best-effort fetch (step 3 below), not for the count (step 4).
//
// One-shot processes (pull-memory.ts / droid-pull-memory.ts) get a fresh module instance per
// invocation, so this is a no-op there — they always fetch once, same as before.
//
// opencode-plugin.ts is long-lived and calls buildStaleBaseWarning from
// "experimental.chat.system.transform", which can fire on EVERY turn. An unthrottled 3.5s git
// fetch per turn would be a latency regression on every opencode turn in every project. The
// rev-list COUNT stays untouched by this throttle — it is instant, network-free, and alone
// already catches every observed staleness incident; only the network fetch that FRESHENS the
// ref before counting is rate-limited.
let lastFetchMs = 0;

/**
 * Default GitRunner: a real `git` invocation via execFileSync with a per-call timeout. Callers
 * (buildStaleBaseWarning below) are responsible for putting `-C <cwd>` first in `args` — this
 * function is a generic, unopinionated runner. On ANY failure (git missing, non-zero exit,
 * timeout-triggered kill) this returns {code:1, stdout:""} — never throws, same contract as
 * apply-root.ts's defaultGitToplevel.
 */
export function defaultRunGit(
  args: string[],
  timeoutMs: number = DEFAULT_FETCH_TIMEOUT_MS,
): { code: number; stdout: string } {
  try {
    const out = execFileSync("git", args, {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "ignore"],
      timeout: timeoutMs,
    });
    return { code: 0, stdout: out };
  } catch {
    return { code: 1, stdout: "" };
  }
}

// Parses a non-negative int from an env var; any garbage/empty/negative value falls back
// silently — env overrides are a convenience, not something that should ever crash the hook.
function parseIntEnv(raw: string | undefined, fallback: number): number {
  if (!raw) return fallback;
  const n = parseInt(raw, 10);
  return Number.isFinite(n) && n >= 0 ? n : fallback;
}

export async function buildStaleBaseWarning(opts: {
  cwd: string;
  runGit?: GitRunner;
  env?: NodeJS.ProcessEnv;
  now?: () => number;
}): Promise<string> {
  try {
    const cwd = opts.cwd;
    const runGit = opts.runGit ?? defaultRunGit;
    const env = opts.env ?? process.env;
    const now = opts.now ?? Date.now;

    if (!cwd) return "";

    // 1. Opt-out marker — checked first and cheaply, before touching git at all.
    if (existsSync(join(cwd, ".claude", "allow-stale-base"))) return "";

    // 2. Resolve the remote default branch ref; fall back to origin/main on ANY failure/empty
    // (missing remote, detached from any remote-tracking config, not a git repo at all).
    let branch = FALLBACK_BRANCH;
    const headRef = runGit(["-C", cwd, "rev-parse", "--abbrev-ref", "origin/HEAD"]);
    if (headRef.code === 0 && headRef.stdout.trim().length > 0) {
      branch = headRef.stdout.trim();
    }
    const branchShort = branch.startsWith("origin/") ? branch.slice("origin/".length) : branch;

    // 3. Best-effort fetch to freshen the ref before counting. Offline is a legitimate state —
    // any failure or timeout here (including an injected stub that THROWS) is swallowed
    // entirely; we proceed to count against whatever ref already exists locally. Throttled to
    // at most once per min-interval (module-level lastFetchMs) — see the throttle comment
    // above; this matters only for the long-lived opencode plugin, one-shot hooks always fetch.
    const fetchMinIntervalMs = parseIntEnv(
      env["PETBOX_STALE_BASE_FETCH_MS_INTERVAL"],
      DEFAULT_FETCH_MIN_INTERVAL_MS,
    );
    const nowMs = now();
    if (env["PETBOX_STALE_BASE_FETCH"] !== "0" && nowMs - lastFetchMs >= fetchMinIntervalMs) {
      lastFetchMs = nowMs;
      const fetchTimeoutMs = parseIntEnv(env["PETBOX_STALE_BASE_FETCH_MS"], DEFAULT_FETCH_TIMEOUT_MS);
      try {
        runGit(["-C", cwd, "fetch", "origin", branchShort, "--quiet"], fetchTimeoutMs);
      } catch {
        // ignore — offline/timeout is a legitimate, silent state
      }
    }

    // 4. Count ahead/behind against the (possibly stale, and that's fine) remote ref.
    const counted = runGit(["-C", cwd, "rev-list", "--left-right", "--count", `HEAD...${branch}`]);
    if (counted.code !== 0) return "";
    const parts = counted.stdout.trim().split(/\s+/);
    if (parts.length !== 2) return "";
    const ahead = parseInt(parts[0] ?? "", 10);
    const behind = parseInt(parts[1] ?? "", 10);
    if (!Number.isFinite(ahead) || !Number.isFinite(behind)) return "";

    // 5. Threshold, overridable.
    const threshold = parseIntEnv(env["PETBOX_STALE_BASE_THRESHOLD"], DEFAULT_THRESHOLD);

    // 6. Fire IFF HEAD is a pure ancestor of the remote default branch (ahead===0) AND far
    // enough behind to matter. See the module-header discriminator note.
    if (ahead !== 0 || behind < threshold) return "";

    return (
      `\n⚠️ BASE STALE: primary checkout is ${behind} commits behind ${branch}.\n` +
      `  • measure with \`git grep <pat> ${branch}\`, not the work tree\n` +
      `  • create worktrees from ${branch}, NOT current HEAD\n` +
      `  • refresh: git fetch origin (worktrees already do this per AGENTS.md r2)\n` +
      `  (silence: touch .claude/allow-stale-base)\n\n`
    );
  } catch {
    return "";
  }
}
