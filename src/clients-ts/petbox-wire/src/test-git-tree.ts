// Test helper: turn a temp directory into a real git working tree.
//
// Why the whole suite suddenly needs this (card: wire-apply-guard-registered-dir, guard 2).
// `apply` under roleScope=project now REFUSES when resolveApplyRoot had to fall back to cwd —
// i.e. when the directory is not inside a git working tree at all. That fallback is not a project;
// it is "wherever this process happened to be started", and rendering 5 roles x 3 harness layouts
// into it is the scatter the guard exists to stop.
//
// Every `apply` test until now built its "project" with mkdtempSync and nothing else, so its
// project was a plain directory — exactly the shape the guard refuses. The tests were not wrong
// about what they assert; they were merely under-specifying what they were standing in. A project
// is a checkout, so the fixture says so now. This is a fixture correction, not a weakening: none
// of those tests is about root resolution (apply-root.test.ts owns that axis), and the guard's own
// behaviour on a non-git directory is asserted directly by apply-scatter-guard.test.ts.
//
// `git init -q` only — no commit, no remote, no config beyond what `git rev-parse
// --show-toplevel` needs to answer. An empty repository with no commits still has a toplevel,
// which is the single fact resolveApplyRoot asks for. Two existing tests (apply-root.test.ts,
// normalize-default.test.ts) already shell out to git this way, so the dependency is not new.
//
// Plain TS for native node type-stripping: zero deps.

import { execFileSync } from "node:child_process";

/**
 * `git init -q` in `dir`, returning `dir` so it composes at the call site
 * (`const projectDir = makeGitWorkingTree(freshDir("prefix-"))`).
 *
 * Throws if git is missing or fails: a fixture that silently did not become a repository would
 * make every test using it fail later with a confusing REFUSED message instead of naming the real
 * cause here.
 */
export function makeGitWorkingTree(dir: string): string {
  execFileSync("git", ["init", "-q"], { cwd: dir, stdio: ["ignore", "ignore", "pipe"] });
  return dir;
}
