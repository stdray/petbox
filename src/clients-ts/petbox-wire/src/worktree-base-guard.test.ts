// Unit coverage for the worktree base staleness guard (worktree-base-guard.ts).
//
// WHY THIS SUITE EXISTS: the guard fires a loud SessionStart warning when the primary checkout
// is a pure ancestor of the remote default branch, parked far behind it. The one behavior that
// MUST be bulletproof is the false-alarm guard — a real feature/work branch (ahead>0) must NEVER
// warn, or the signal trains people to ignore it. Every test here uses an INJECTED runGit stub
// (no real git process, no network) so the suite is deterministic and instant; the default
// execFileSync-based implementation is exercised only implicitly (it is a thin, well-understood
// wrapper — see apply-root.ts's defaultGitToplevel for the same pattern).
//
// Run: node --test src/worktree-base-guard.test.ts

import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import type { GitRunner } from "./worktree-base-guard.ts";
import { buildStaleBaseWarning } from "./worktree-base-guard.ts";

type Call = { args: string[]; timeoutMs?: number };

// Builds an injected GitRunner from a small script: rev-parse origin/HEAD, an optional fetch,
// then rev-list --left-right --count. `aheadBehind` is `[ahead, behind]`; pass `revListCode: 1`
// to simulate a failing/garbage rev-list. Records every call for assertions.
function makeStubGit(opts: {
  aheadBehind?: [number, number];
  revListCode?: number;
  revListStdout?: string;
  branch?: string;
  fetchThrows?: boolean;
}): { runGit: GitRunner; calls: Call[] } {
  const calls: Call[] = [];
  const runGit: GitRunner = (args, timeoutMs) => {
    calls.push(timeoutMs === undefined ? { args } : { args, timeoutMs });
    if (args.includes("rev-parse")) {
      return opts.branch ? { code: 0, stdout: opts.branch } : { code: 1, stdout: "" };
    }
    if (args.includes("fetch")) {
      if (opts.fetchThrows) throw new Error("simulated fetch timeout/offline");
      return { code: 1, stdout: "" }; // fetch outcome is ignored by the guard regardless
    }
    if (args.includes("rev-list")) {
      if (opts.revListCode) return { code: opts.revListCode, stdout: "" };
      if (opts.revListStdout !== undefined) return { code: 0, stdout: opts.revListStdout };
      const [ahead, behind] = opts.aheadBehind ?? [0, 0];
      return { code: 0, stdout: `${ahead}\t${behind}` };
    }
    return { code: 1, stdout: "" };
  };
  return { runGit, calls };
}

test("ahead=0, behind=218 (parked stale base): warns, includes the count and branch", async () => {
  const { runGit } = makeStubGit({ aheadBehind: [0, 218], branch: "origin/main" });
  const text = await buildStaleBaseWarning({ cwd: "/fake/repo", runGit });
  assert.match(text, /BASE STALE/);
  assert.match(text, /218/);
  assert.match(text, /origin\/main/);
});

test("ahead=3, behind=100 (real feature branch): stays silent — the key false-alarm guard", async () => {
  const { runGit } = makeStubGit({ aheadBehind: [3, 100], branch: "origin/main" });
  const text = await buildStaleBaseWarning({ cwd: "/fake/repo", runGit });
  assert.equal(text, "");
});

test("ahead=0, behind=2, below default threshold (10): stays silent", async () => {
  const { runGit } = makeStubGit({ aheadBehind: [0, 2], branch: "origin/main" });
  const text = await buildStaleBaseWarning({ cwd: "/fake/repo", runGit });
  assert.equal(text, "");
});

test("ahead=0, behind=50, PETBOX_STALE_BASE_THRESHOLD=100: env override respected, stays silent", async () => {
  const { runGit } = makeStubGit({ aheadBehind: [0, 50], branch: "origin/main" });
  const text = await buildStaleBaseWarning({
    cwd: "/fake/repo",
    runGit,
    env: { PETBOX_STALE_BASE_THRESHOLD: "100" },
  });
  assert.equal(text, "");
});

test("opt-out marker (.claude/allow-stale-base) present: silent even at ahead=0/behind=218", async () => {
  const dir = mkdtempSync(join(tmpdir(), "petbox-stale-guard-optout-"));
  try {
    mkdirSync(join(dir, ".claude"), { recursive: true });
    // Existence is all that matters — an empty file satisfies fs.existsSync.
    mkdirSync(join(dir, ".claude", "allow-stale-base"));
    const { runGit, calls } = makeStubGit({ aheadBehind: [0, 218], branch: "origin/main" });
    const text = await buildStaleBaseWarning({ cwd: dir, runGit });
    assert.equal(text, "");
    assert.equal(calls.length, 0, "opt-out must short-circuit before any git call");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("rev-list returns non-zero: silent", async () => {
  const { runGit } = makeStubGit({ revListCode: 1, branch: "origin/main" });
  const text = await buildStaleBaseWarning({ cwd: "/fake/repo", runGit });
  assert.equal(text, "");
});

test("rev-list returns garbage (unparseable): silent", async () => {
  const { runGit } = makeStubGit({ revListStdout: "not-a-number garbage", branch: "origin/main" });
  const text = await buildStaleBaseWarning({ cwd: "/fake/repo", runGit });
  assert.equal(text, "");
});

test("fetch stub throws/times out but rev-list still returns 0/218: still warns — fetch failure is tolerated", async () => {
  const { runGit } = makeStubGit({ aheadBehind: [0, 218], branch: "origin/main", fetchThrows: true });
  const text = await buildStaleBaseWarning({ cwd: "/fake/repo", runGit });
  assert.match(text, /BASE STALE/);
  assert.match(text, /218/);
});

test("issues rev-list --left-right --count, and a disabled fetch (PETBOX_STALE_BASE_FETCH=0) issues no fetch call", async () => {
  const { runGit, calls } = makeStubGit({ aheadBehind: [0, 218], branch: "origin/main" });
  await buildStaleBaseWarning({ cwd: "/fake/repo", runGit, env: { PETBOX_STALE_BASE_FETCH: "0" } });

  const revListCall = calls.find((c) => c.args.includes("rev-list"));
  assert.ok(revListCall, "expected a rev-list call");
  assert.ok(revListCall!.args.includes("--left-right"));
  assert.ok(revListCall!.args.includes("--count"));

  const fetchCall = calls.find((c) => c.args.includes("fetch"));
  assert.equal(fetchCall, undefined, "PETBOX_STALE_BASE_FETCH=0 must suppress the fetch call entirely");
});

test("fetch throttle: two calls in quick succession (injected clock) issue the fetch only once", async () => {
  const { runGit, calls } = makeStubGit({ aheadBehind: [0, 218], branch: "origin/main" });
  // lastFetchMs is MODULE-LEVEL (by design — see worktree-base-guard.ts) and persists across
  // every test in this process, all of which used the real Date.now() default. Start the
  // injected clock far enough past "now" that this test's first call is guaranteed to be
  // outside whatever interval an earlier test left behind, isolating this test from run order.
  let clock = Date.now() + 10 * 60_000;
  const now = () => clock;

  await buildStaleBaseWarning({ cwd: "/fake/repo/throttle-test-a", runGit, now });
  const fetchCallsAfterFirst = calls.filter((c) => c.args.includes("fetch")).length;
  assert.equal(fetchCallsAfterFirst, 1, "first call within the interval must fetch");

  clock += 1_000; // well inside the default 60s min-interval
  await buildStaleBaseWarning({ cwd: "/fake/repo/throttle-test-a", runGit, now });
  const fetchCallsAfterSecond = calls.filter((c) => c.args.includes("fetch")).length;
  assert.equal(
    fetchCallsAfterSecond,
    1,
    "second call inside the min-interval must NOT fetch again — the count still runs unthrottled",
  );

  // The rev-list count itself is never throttled — both calls must have counted.
  const revListCalls = calls.filter((c) => c.args.includes("rev-list"));
  assert.equal(revListCalls.length, 2, "the count must run on every call regardless of the fetch throttle");
});
