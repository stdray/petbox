// Target directory resolution for petbox-wire apply (wiring-registry-resolve).
//
// Two DIFFERENT questions get answered by two DIFFERENT mechanisms — conflating them was the
// bug (apply-root-overwrites-primary-worktree):
//   - PROJECT IDENTITY (projectKey / baseUrl / apiKey) comes from the registry
//     (~/.petbox/projects.json), longest-prefix match — see registry.ts's resolveProject.
//   - the ARTIFACT TARGET DIRECTORY (where apply writes .claude/agents/, .opencode/agent/,
//     .factory/droids/) is the git worktree apply is actually running in.
//
// Before this fix, resolveApplyRoot ALSO used the registry's longest-prefix match to pick the
// target directory. A worktree living under a registered prefix (e.g.
// `<registered>/.claude/worktrees/agent-x`) resolved its apply root to the REGISTERED prefix —
// the PRIMARY tree — not the worktree apply was invoked from. `apply` run from inside a worktree
// silently rewrote the primary tree's `.claude/agents/*`, which may be checked out on an
// entirely different branch (a live, hostile side effect on someone else's working copy).
//
// Fix: the artifact root is git's own toplevel for cwd, full stop — no registry involved.
// Falls back to cwd when cwd is not inside a git working tree at all (plain directory, or a
// git binary that is missing/fails) — same "no better answer, use cwd" fallback the old code
// already had for the "no registry entry" case.
//
// Plain TS for native node type-stripping: zero deps beyond node:child_process.

import { execFileSync } from "node:child_process";

export type ApplyRootVia = "git" | "cwd";

export type ApplyRootResolution = {
  readonly root: string;
  readonly via: ApplyRootVia;
};

/**
 * Default git toplevel probe: `git rev-parse --show-toplevel` from cwd. Returns null on ANY
 * failure — cwd is not inside a git working tree, `git` is not on PATH, a bare/detached repo
 * that cannot resolve a toplevel, etc. Never throws (mirrors registry.ts's resolveProject
 * never-throws contract for the same reason: this runs inside a CLI step that must not crash
 * just because cwd happens to be a plain, non-git directory).
 */
export function defaultGitToplevel(cwd: string): string | null {
  try {
    const out = execFileSync("git", ["rev-parse", "--show-toplevel"], {
      cwd,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "ignore"],
    });
    const trimmed = out.trim();
    return trimmed.length > 0 ? trimmed : null;
  } catch {
    return null;
  }
}

/**
 * Resolve WHERE `apply` writes artifacts: the git worktree toplevel for cwd, or cwd itself when
 * cwd is not inside a git working tree. Deliberately does NOT consult the registry — the
 * registry answers project IDENTITY, not artifact location (see module comment). `gitToplevel`
 * is injectable so tests never have to shell out to a real git process to exercise the pure
 * fallback branch.
 */
export function resolveApplyRoot(
  cwd: string,
  gitToplevel: (cwd: string) => string | null = defaultGitToplevel,
): ApplyRootResolution {
  const top = gitToplevel(cwd);
  if (top) return { root: top, via: "git" };
  return { root: cwd, via: "cwd" };
}
