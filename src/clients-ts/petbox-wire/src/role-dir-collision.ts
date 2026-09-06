// Does the PROJECT role directory apply is about to sweep happen to BE the harness's own USER
// profile directory?
//
// The bug this closes (card: wire-apply-guard-registered-dir). `apply` run from the home
// directory: HOME is not a git working tree, so resolveApplyRoot falls back to cwd and
// root = HOME. Under roleScope=user the run first renders the 15 role files into the harness
// profiles (applyUserRoles) and then, in the same command, calls sweepProjectRoleArtifacts(root)
// to delete "the project's leftover copies" — and under root=HOME those two directories are the
// SAME directory:
//
//   join(HOME, agentFilesDir("claude-code")) === userAgentFilesRoot("claude-code")  →  ~/.claude/agents
//   join(HOME, agentFilesDir("droid"))       === userAgentFilesRoot("droid")        →  ~/.factory/droids
//
// So apply wrote 15 files and immediately deleted 10 of them as "project copies", quietly, exit 0.
// Measured on the owner's machine 2026-09-06: `removals=10`.
//
// opencode survived only BY ACCIDENT: its project layout is `.opencode/agent` and its user layout
// is `.config/opencode/agents` — different strings, so the two never collided. A guard that keyed
// on "is this HOME?" would therefore be testing the wrong thing, and a guard that assumed all
// three harnesses behave alike would be resting on that accident. The question is per-harness and
// it is the only question worth asking: are these two paths the same directory?
//
// WHY THE COMPARISON IS NOT `a === b`. Naive string equality is what made the defect invisible in
// the first place, and on Windows it is wrong in at least four independent ways:
//   - CASE: `C:\Users\stdray` and `C:\users\stdray` are the same directory on NTFS. cwd can carry
//     either, because it comes from whatever the shell/parent process handed us.
//   - SEPARATORS and dot segments: `C:/Users/x/.claude/agents`, `C:\Users\x\.claude\agents` and
//     `C:\Users\x\foo\..\.claude\agents` are all one path. `resolve` collapses all three.
//   - TRAILING SLASH: `.../agents\` vs `.../agents`. `resolve` drops it.
//   - SYMLINKS / junctions / 8.3 short names (`C:\Users\RUNNER~1`): only the filesystem can answer
//     these, so `realpathSync.native` is asked when the path exists. `.native` specifically,
//     because on Windows it is the call that returns the FILESYSTEM's own canonical casing
//     (`C:\Users\stdray`), not merely the string it was handed.
//
// realpath is best-effort by construction: the directory may not exist yet (a fresh machine has no
// `~/.factory/droids` until the first render), and one side may exist while the other does not. So
// BOTH pairs are compared — the lexically resolved pair and the realpath'd pair — and a match on
// either is a collision. Over-matching is the safe direction here and under-matching is not: a
// false positive skips a deletion (a stale project copy survives one more run, and the run says
// so out loud), while a false negative deletes the user's live role profile.
//
// Plain TS for native node type-stripping: zero deps.

import { realpathSync } from "node:fs";
import { homedir } from "node:os";
import { resolve } from "node:path";
import { agentFilesDir } from "./apply-artifacts.ts";
import type { HarnessId } from "./harness-capabilities.ts";
import { userAgentFilesRoot } from "./role-scope.ts";

/**
 * Platforms whose filesystems compare paths case-insensitively. Windows always; macOS by default
 * (APFS/HFS+ are case-insensitive unless the volume was deliberately formatted otherwise). Being
 * wrong on a case-SENSITIVE macOS volume can only over-match, which per the module comment is the
 * direction that cannot destroy data.
 */
const CASE_INSENSITIVE_FS = process.platform === "win32" || process.platform === "darwin";

function fold(p: string): string {
  return CASE_INSENSITIVE_FS ? p.toLowerCase() : p;
}

/** realpath if the path exists; the lexically resolved path otherwise. Never throws. */
function canonical(p: string): string {
  const lexical = resolve(p);
  try {
    return realpathSync.native(lexical);
  } catch {
    return lexical; // does not exist yet (or is unreadable) — the lexical form is all there is
  }
}

/**
 * Are `a` and `b` the same directory? Compares the lexically resolved forms AND the realpath'd
 * forms, case-folded on case-insensitive filesystems; a match on either pair is a match. See the
 * module comment for why each of those four normalizations is load-bearing.
 */
export function isSameDirectory(a: string, b: string): boolean {
  if (fold(resolve(a)) === fold(resolve(b))) return true;
  return fold(canonical(a)) === fold(canonical(b));
}

export type UserProfileCollision = {
  readonly harness: HarnessId;
  /** `join(root, agentFilesDir(harness))` — what the project sweep would delete from. */
  readonly projectDir: string;
  /** `userAgentFilesRoot(harness, homeDir)` — where the user-scope render just wrote. */
  readonly userDir: string;
};

/**
 * The project role directory for `harness` under `root`, when it is the SAME directory as that
 * harness's user profile — otherwise null. Asked per harness, never per platform and never per
 * "does root look like HOME": the whole point is that claude-code and droid collide while
 * opencode does not, and only the paths themselves know that.
 */
export function userProfileCollision(
  root: string,
  harness: HarnessId,
  homeDir: string = homedir(),
): UserProfileCollision | null {
  const projectDir = resolve(root, agentFilesDir(harness));
  const userDir = userAgentFilesRoot(harness, homeDir);
  if (!isSameDirectory(projectDir, userDir)) return null;
  return { harness, projectDir, userDir };
}
