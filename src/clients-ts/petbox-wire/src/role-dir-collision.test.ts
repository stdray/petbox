// Unit tests for the collision guard's path comparison (card: wire-apply-guard-registered-dir).
//
// The defect these pin was NOT "the sweep is wrong" — the sweep did exactly what it was told. It
// was that nothing ever asked whether the directory it was told to sweep was the very directory
// the same command had just rendered into. So the axis under test here is: given a root and a
// harness, is `join(root, agentFilesDir(h))` the same directory as `userAgentFilesRoot(h)` — and
// is that answer robust to the four ways two identical Windows paths can be spelled differently.
//
// The end-to-end proof that apply actually acts on this answer lives in
// apply-home-root-guard.test.ts; this file is the pure half.
//
// Run: node --test src/role-dir-collision.test.ts

import assert from "node:assert/strict";
import { mkdtempSync, realpathSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { agentFilesDir } from "./apply-artifacts.ts";
import { HARNESS_IDS } from "./harness-capabilities.ts";
import { isSameDirectory, userProfileCollision } from "./role-dir-collision.ts";
import { userAgentFilesRoot } from "./role-scope.ts";

const CASE_INSENSITIVE_FS = process.platform === "win32" || process.platform === "darwin";

// A stand-in HOME. Not the real one: these are pure path computations, and a test that depended on
// the developer's actual profile would pass or fail for reasons that have nothing to do with the
// code. Absolute, because `resolve` would otherwise anchor a relative fixture to the test runner's
// own cwd and quietly make both sides agree for the wrong reason.
const FAKE_HOME = process.platform === "win32" ? "C:\\Users\\somebody" : "/home/somebody";

// ---- the per-harness verdict ---------------------------------------------------------------
//
// THE point of the guard, and the reason it is asked per harness rather than once per run: two of
// the three harnesses collide and the third does not. opencode's survival in the live incident was
// not protection, it was luck — its project layout is `.opencode/agent` and its user layout is
// `.config/opencode/agents`, one character apart. A guard shaped like "is root the home
// directory?" would have looked correct on the same evidence while being wrong about WHY, and
// would have started deleting opencode's profile the day either name changed.

test("root=HOME: claude-code collides — its project dir IS ~/.claude/agents", () => {
  const c = userProfileCollision(FAKE_HOME, "claude-code", FAKE_HOME);
  assert.notEqual(c, null, "the sweep would have deleted the user profile it just rendered");
  assert.equal(c?.projectDir, join(FAKE_HOME, ".claude", "agents"));
  assert.equal(c?.userDir, userAgentFilesRoot("claude-code", FAKE_HOME));
});

test("root=HOME: droid collides — its project dir IS ~/.factory/droids", () => {
  const c = userProfileCollision(FAKE_HOME, "droid", FAKE_HOME);
  assert.notEqual(c, null, "the sweep would have deleted the user profile it just rendered");
  assert.equal(c?.projectDir, join(FAKE_HOME, ".factory", "droids"));
  assert.equal(c?.userDir, userAgentFilesRoot("droid", FAKE_HOME));
});

test("root=HOME: opencode does NOT collide — `.opencode/agent` is not `.config/opencode/agents`", () => {
  // This is the accident the guard must not be resting on. Asserted as its own case so that if
  // opencode's layout ever changes to collide, THIS test fails loudly and the guard — which asks
  // the paths, not the harness name — starts protecting it without anyone editing wire.ts.
  assert.equal(userProfileCollision(FAKE_HOME, "opencode", FAKE_HOME), null);
  assert.notEqual(
    join(FAKE_HOME, agentFilesDir("opencode")),
    userAgentFilesRoot("opencode", FAKE_HOME),
    "if these ever become equal, opencode collides too and the previous assertion must change",
  );
});

test("an ordinary project root collides for NO harness — the guard never fires where a real sweep belongs", () => {
  const projectRoot = process.platform === "win32" ? "C:\\src\\some-project" : "/src/some-project";
  for (const harness of HARNESS_IDS) {
    assert.equal(
      userProfileCollision(projectRoot, harness, FAKE_HOME),
      null,
      `${harness}: a real project's copies must still be swept — over-firing would leave stale ` +
        `project copies forever`,
    );
  }
});

// ---- the four ways two identical paths get spelled differently -------------------------------

test("separators: forward and backslashes name the same directory", () => {
  assert.equal(isSameDirectory("C:/Users/x/.claude/agents", "C:\\Users\\x\\.claude\\agents"), process.platform === "win32");
  // POSIX has no such equivalence to assert; the win32 branch above is the one that matters.
  assert.equal(isSameDirectory("/home/x/.claude/agents", "/home/x/.claude/agents"), true);
});

test("dot segments: a path routed through `..` is the same directory", () => {
  const base = join(FAKE_HOME, ".claude", "agents");
  const detour = join(FAKE_HOME, "downloads", "..", ".claude", "agents");
  assert.equal(isSameDirectory(base, detour), true);
});

test("trailing separator does not make a different directory", () => {
  const base = join(FAKE_HOME, ".claude", "agents");
  assert.equal(isSameDirectory(base, base + (process.platform === "win32" ? "\\" : "/")), true);
});

test("CASE (Windows/macOS): C:\\Users\\X and C:\\users\\x are one directory — the naive `===` this guard replaces", () => {
  // The live trap, not a hypothetical: cwd is whatever the parent process handed us, so the SAME
  // machine yields `C:\Users\stdray` from one shell and `c:\users\stdray` from another. Under
  // string equality the guard would silently not fire for the second one and the sweep would eat
  // the profile — the exact defect, back again, on a machine that "already had the fix".
  const upper = process.platform === "win32" ? "C:\\Users\\Somebody\\.claude\\agents" : "/home/Somebody/.claude/agents";
  const lower = process.platform === "win32" ? "c:\\users\\somebody\\.claude\\agents" : "/home/somebody/.claude/agents";
  assert.equal(
    isSameDirectory(upper, lower),
    CASE_INSENSITIVE_FS,
    CASE_INSENSITIVE_FS
      ? "case-insensitive filesystem: these ARE the same directory and the guard must say so"
      : "case-sensitive filesystem: these are genuinely two directories",
  );
});

test("CASE (Windows/macOS): the collision itself survives a case-mangled root", () => {
  // The composed form of the case test above: not two hand-written strings, but the real pair the
  // guard computes — a root spelled in a different case than homedir() reports.
  if (!CASE_INSENSITIVE_FS) return;
  const mangledRoot = FAKE_HOME.toLowerCase();
  assert.notEqual(mangledRoot, FAKE_HOME, "fixture must actually differ in case, or it proves nothing");
  assert.notEqual(
    userProfileCollision(mangledRoot, "claude-code", FAKE_HOME),
    null,
    "a lowercased cwd must not slip the guard",
  );
  assert.notEqual(userProfileCollision(mangledRoot, "droid", FAKE_HOME), null);
});

// ---- symlinks / junctions, on the real filesystem ---------------------------------------------

test("realpath: two spellings of a directory that EXISTS resolve to one — and two that differ stay different", () => {
  const a = realpathSync(mkdtempSync(join(tmpdir(), "petbox-collide-a-")));
  const b = realpathSync(mkdtempSync(join(tmpdir(), "petbox-collide-b-")));
  try {
    assert.equal(isSameDirectory(a, a), true);
    assert.equal(isSameDirectory(a, join(a, "sub", "..")), true);
    assert.equal(isSameDirectory(a, b), false, "two genuinely different directories must never match");
  } finally {
    rmSync(a, { recursive: true, force: true });
    rmSync(b, { recursive: true, force: true });
  }
});

test("a directory that does not exist yet still compares — realpath is best-effort, not required", () => {
  // A fresh machine has no ~/.factory/droids until the first render, and the guard has to answer
  // correctly BEFORE that write. If canonical() ever started throwing instead of falling back to
  // the lexical form, this is what would catch it.
  const ghost = join(FAKE_HOME, "definitely", "not", "on", "disk", ".claude", "agents");
  assert.equal(isSameDirectory(ghost, ghost), true);
  assert.equal(isSameDirectory(ghost, join(FAKE_HOME, "elsewhere")), false);
});
