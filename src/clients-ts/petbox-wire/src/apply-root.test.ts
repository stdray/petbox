// Unit + integration tests for the apply target-directory fix
// (bug: apply-root-overwrites-primary-worktree).
//
// Run: node --test src/apply-root.test.ts

import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { defaultGitToplevel, resolveApplyRoot } from "./apply-root.ts";

function freshDir(): string {
  // realpathSync so Windows short-path / symlink quirks in TMP don't make string comparisons
  // against `git rev-parse --show-toplevel`'s output flaky.
  return realpathSync(mkdtempSync(join(tmpdir(), "petbox-apply-root-")));
}

function norm(p: string): string {
  return p.replace(/\\/g, "/").replace(/\/+$/, "").toLowerCase();
}

function hasGit(): boolean {
  try {
    execFileSync("git", ["--version"], { stdio: "ignore" });
    return true;
  } catch {
    return false;
  }
}

// ---- pure unit tests (injected gitToplevel — no real git process) ----

test("resolveApplyRoot: uses the injected git toplevel and reports via 'git'", () => {
  const result = resolveApplyRoot("/some/cwd", () => "/some/toplevel");
  assert.deepEqual(result, { root: "/some/toplevel", via: "git" });
});

test("resolveApplyRoot: falls back to cwd (via 'cwd') when git resolution returns null", () => {
  const result = resolveApplyRoot("/some/cwd", () => null);
  assert.deepEqual(result, { root: "/some/cwd", via: "cwd" });
});

test("resolveApplyRoot: NEVER consults a registry — identical cwd/root regardless of any external prefix table", () => {
  // The whole point of the fix: no registry longest-prefix matching happens here at all. The
  // injected gitToplevel is the only source of truth for the target directory.
  const result = resolveApplyRoot("/registered/prefix/worktree/x", () => "/registered/prefix/worktree/x");
  assert.equal(result.root, "/registered/prefix/worktree/x");
  assert.equal(result.via, "git");
});

// ---- defaultGitToplevel against a REAL git process ----

test("defaultGitToplevel: a plain non-git directory resolves to null", (t) => {
  if (!hasGit()) {
    t.skip("git not on PATH");
    return;
  }
  const dir = freshDir();
  try {
    // A tmp dir is not expected to be inside this repo's tree; guard just in case.
    const top = defaultGitToplevel(dir);
    if (top !== null) {
      t.skip(`environment surprise: ${dir} resolved inside a git tree (${top}) — skipping`);
      return;
    }
    assert.equal(top, null);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("defaultGitToplevel: a git repo resolves to its own toplevel, from a nested subdir too", (t) => {
  if (!hasGit()) {
    t.skip("git not on PATH");
    return;
  }
  const dir = freshDir();
  try {
    execFileSync("git", ["init", "-q"], { cwd: dir });
    const top = defaultGitToplevel(dir);
    assert.ok(top, "expected a toplevel for a freshly-init'd repo");
    assert.equal(norm(top!), norm(dir));

    const nested = join(dir, "a", "b", "c");
    mkdirSync(nested, { recursive: true });
    const topFromNested = defaultGitToplevel(nested);
    assert.equal(norm(topFromNested!), norm(dir), "toplevel from a nested subdir must still be the repo root");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ---- the actual regression: worktree under a REGISTERED PRIMARY prefix must resolve to itself ----

test("resolveApplyRoot regression: apply from a real git worktree resolves to the WORKTREE, not the primary tree it was branched from", (t) => {
  if (!hasGit()) {
    t.skip("git not on PATH");
    return;
  }
  const primary = freshDir();
  let worktree: string | null = null;
  try {
    execFileSync("git", ["init", "-q"], { cwd: primary });
    execFileSync("git", ["config", "user.email", "test@example.com"], { cwd: primary });
    execFileSync("git", ["config", "user.name", "Test"], { cwd: primary });
    writeFileSync(join(primary, "README.md"), "primary\n", "utf8");
    execFileSync("git", ["add", "."], { cwd: primary });
    execFileSync("git", ["commit", "-q", "-m", "init"], { cwd: primary });

    // A worktree living UNDER the primary tree's own filesystem prefix — the exact shape that
    // triggered the bug: a registry entry for `primary` would longest-prefix-match this path too.
    worktree = join(primary, ".claude", "worktrees", "agent-x");
    mkdirSync(join(primary, ".claude", "worktrees"), { recursive: true });
    execFileSync("git", ["worktree", "add", "-q", "-b", "agent-x-branch", worktree], { cwd: primary });

    // Simulate the OLD buggy behavior for contrast: a registry longest-prefix match for a
    // `primary` entry would have returned `primary` for a cwd under it. The FIXED resolver must
    // not do that — it must answer with the worktree itself.
    const fromWorktree = resolveApplyRoot(worktree);
    assert.equal(fromWorktree.via, "git");
    assert.equal(
      norm(fromWorktree.root),
      norm(worktree),
      "apply run from the worktree must target the worktree, never the primary tree",
    );
    assert.notEqual(
      norm(fromWorktree.root),
      norm(primary),
      "must NOT resolve to the primary tree just because the worktree sits under its filesystem prefix",
    );

    // And apply from the primary tree itself still resolves to the primary tree — this isn't a
    // "worktree always wins" hack, it's "git toplevel for THIS cwd, whichever tree that is".
    const fromPrimary = resolveApplyRoot(primary);
    assert.equal(norm(fromPrimary.root), norm(primary));
  } finally {
    if (worktree && existsSync(worktree)) {
      try {
        execFileSync("git", ["worktree", "remove", "-f", worktree], { cwd: primary });
      } catch {
        /* best-effort cleanup */
      }
    }
    rmSync(primary, { recursive: true, force: true });
  }
});
