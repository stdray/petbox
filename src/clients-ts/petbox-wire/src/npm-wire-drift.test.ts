// Unit tests for npm-wire-drift.ts (task kit-version-lands-everywhere-and-sweeps item 3): the
// `npm-wire` tag gate must stop being a silent link in main -> npm -> ~/.petbox/wire -> project.
//
// Run: node --test src/npm-wire-drift.test.ts

import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { checkNpmWireDrift, formatNpmWireDrift, type NpmWireDriftResult } from "./npm-wire-drift.ts";

function freshDir(): string {
  return realpathSync(mkdtempSync(join(tmpdir(), "petbox-npm-drift-")));
}

function hasGit(): boolean {
  try {
    execFileSync("git", ["--version"], { stdio: "ignore" });
    return true;
  } catch {
    return false;
  }
}

function git(args: string[], cwd: string): string {
  return execFileSync("git", args, { cwd, encoding: "utf8" }).trim();
}

/** A repo with a `main` branch carrying `commitCount` commits. Returns every commit sha, oldest
 * first (index 0 is the FIRST commit main ever had — a stand-in for "the commit npm published"). */
function makeRepoWithMainHistory(commitCount: number): { dir: string; shas: string[] } {
  const dir = freshDir();
  git(["init", "-q", "-b", "main"], dir);
  git(["config", "user.email", "test@example.com"], dir);
  git(["config", "user.name", "Test"], dir);
  const shas: string[] = [];
  for (let i = 0; i < commitCount; i++) {
    writeFileSync(join(dir, "f.txt"), `commit ${i}\n`, "utf8");
    git(["add", "."], dir);
    git(["commit", "-q", "-m", `commit ${i}`], dir);
    shas.push(git(["rev-parse", "HEAD"], dir));
  }
  return { dir, shas };
}

function fakeFetch(response: { version: string; gitHead: string } | "network-error" | "not-ok"): typeof fetch {
  return (async () => {
    if (response === "network-error") throw new Error("simulated network failure");
    if (response === "not-ok") return new Response("nope", { status: 500 });
    return new Response(JSON.stringify(response), { status: 200, headers: { "Content-Type": "application/json" } });
  }) as typeof fetch;
}

test("checkNpmWireDrift: not a git checkout at all -> skipped, never throws", async () => {
  const dir = freshDir();
  try {
    const result = await checkNpmWireDrift(dir, { fetchImpl: fakeFetch("network-error") });
    assert.equal(result.status, "skipped");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("checkNpmWireDrift: git repo but no local 'main' ref -> skipped", async (t) => {
  if (!hasGit()) {
    t.skip("git not on PATH");
    return;
  }
  const dir = freshDir();
  try {
    git(["init", "-q", "-b", "not-main"], dir);
    git(["config", "user.email", "test@example.com"], dir);
    git(["config", "user.name", "Test"], dir);
    writeFileSync(join(dir, "f.txt"), "x\n", "utf8");
    git(["add", "."], dir);
    git(["commit", "-q", "-m", "init"], dir);
    const result = await checkNpmWireDrift(dir, { fetchImpl: fakeFetch("network-error") });
    assert.equal(result.status, "skipped");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("checkNpmWireDrift: npm registry unreachable -> skipped, never throws", async (t) => {
  if (!hasGit()) {
    t.skip("git not on PATH");
    return;
  }
  const { dir } = makeRepoWithMainHistory(1);
  try {
    const result = await checkNpmWireDrift(dir, { fetchImpl: fakeFetch("network-error") });
    assert.equal(result.status, "skipped");
    const result2 = await checkNpmWireDrift(dir, { fetchImpl: fakeFetch("not-ok") });
    assert.equal(result2.status, "skipped");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("checkNpmWireDrift: npm gitHead === local main head -> in-sync", async (t) => {
  if (!hasGit()) {
    t.skip("git not on PATH");
    return;
  }
  const { dir, shas } = makeRepoWithMainHistory(1);
  try {
    const result = await checkNpmWireDrift(dir, {
      fetchImpl: fakeFetch({ version: "0.1.0-ci.1", gitHead: shas[0]! }),
    });
    assert.equal(result.status, "in-sync");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("checkNpmWireDrift: npm gitHead is an ANCESTOR of local main -> ahead, with the exact commit count", async (t) => {
  if (!hasGit()) {
    t.skip("git not on PATH");
    return;
  }
  const { dir, shas } = makeRepoWithMainHistory(4);
  try {
    // npm published commit index 0; main has since gained 3 more commits (indices 1..3).
    const result = await checkNpmWireDrift(dir, {
      fetchImpl: fakeFetch({ version: "0.1.0-ci.100", gitHead: shas[0]! }),
    });
    assert.equal(result.status, "ahead");
    if (result.status === "ahead") {
      assert.equal(result.aheadBy, 3);
      assert.equal(result.publishedGitHead, shas[0]);
      assert.equal(result.localMainHead, shas[3]);
      assert.equal(result.publishedVersion, "0.1.0-ci.100");
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("checkNpmWireDrift: npm gitHead is NOT an ancestor of local main (unrelated sha) -> diverged", async (t) => {
  if (!hasGit()) {
    t.skip("git not on PATH");
    return;
  }
  const { dir } = makeRepoWithMainHistory(1);
  try {
    // A well-formed but entirely unrelated 40-hex sha (never existed in this repo's history).
    const foreignSha = "a".repeat(40);
    const result = await checkNpmWireDrift(dir, {
      fetchImpl: fakeFetch({ version: "0.1.0-ci.1", gitHead: foreignSha }),
    });
    assert.equal(result.status, "diverged");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("formatNpmWireDrift: names every status distinctly, never silent about a real drift", () => {
  const cases: NpmWireDriftResult[] = [
    { status: "skipped", reason: "not a git checkout" },
    { status: "in-sync", gitHead: "a".repeat(40) },
    {
      status: "ahead",
      publishedGitHead: "b".repeat(40),
      publishedVersion: "0.1.0-ci.5",
      localMainHead: "c".repeat(40),
      aheadBy: 7,
    },
    {
      status: "diverged",
      publishedGitHead: "d".repeat(40),
      publishedVersion: "0.1.0-ci.5",
      localMainHead: "e".repeat(40),
    },
  ];
  const lines = cases.map(formatNpmWireDrift);
  assert.match(lines[0]!, /skipped/);
  assert.match(lines[1]!, /in sync/);
  assert.match(lines[2]!, /STALE/);
  assert.match(lines[2]!, /7/);
  assert.match(lines[3]!, /not an ancestor/);
  // Every line is distinct — no two statuses collapse into the same wording.
  assert.equal(new Set(lines).size, lines.length);
});
