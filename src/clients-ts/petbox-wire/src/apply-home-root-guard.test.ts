// End-to-end regression tests for the two guards on apply's ROOT (card:
// wire-apply-guard-registered-dir). Both exist because `resolveApplyRoot` falls back to cwd when
// cwd is not inside a git working tree, and NOTHING downstream treated that fallback as the
// non-answer it is.
//
// GUARD 1 — the collision. `apply --roles=user` from the home directory, on the owner's machine,
// 2026-09-06:
//
//   apply [roles:user]: summary — writes=0 unchanged=15 removals=0
//   apply: root=C:\Users\stdray (via cwd)
//   apply: would remove C:\Users\stdray\.claude\agents\petbox-*.md — project copy   (x5)
//   apply: would remove C:\Users\stdray\.factory\droids\petbox-*.md — project copy  (x5)
//   apply: summary — removals=10 refused=0   exit=0
//
// One command rendered 15 role files into the harness profiles and then deleted 10 of them as
// "project copies", silently, exit 0. root was HOME, so `<root>/.claude/agents` and
// `<root>/.factory/droids` WERE the profiles. Re-running apply from a project recreated them and
// the next run from home killed them again — a stable, invisible flip-flop.
//
// GUARD 2 — the scatter. Under roleScope=project the same cwd fallback means "render 5 roles x 3
// harness layouts into whatever directory this shell was sitting in". That is the friend's-machine
// case (no ~/.petbox/wire.json, so the scope defaults to project): 15 files land somewhere nothing
// maintains, one of the three trees (`~/.opencode/agent`) is not even a path any harness reads.
//
// What these tests deliberately do NOT do is touch the real profile. Every run below gets its own
// temp HOME via env (USERPROFILE/HOME, with HOMEDRIVE/HOMEPATH cleared so Windows' homedir() can
// only read what we set) — the same technique the rest of the suite uses. The runs are REAL, not
// --dry-run: the defect is that files written earlier in the command are deleted later in the same
// command, and a preview cannot demonstrate a file surviving.
//
// wire.ts runs main() at module top level, so apply can only be exercised as a subprocess.
//
// Run: node --test src/apply-home-root-guard.test.ts

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readdirSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { HARNESS_IDS } from "./harness-capabilities.ts";
import { makeGitWorkingTree } from "./test-git-tree.ts";
import { userAgentFilesDir } from "./role-scope.ts";
import { WIRE_EXIT } from "./wire-exit.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");

const ROLE_BASENAMES = [
  "petbox-orchestrator.md",
  "petbox-worker.md",
  "petbox-worker-highstakes.md",
  "petbox-reserve.md",
  "petbox-explore.md",
];

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

function runWire(args: string[], homeDir: string, cwd: string): { out: string; status: number | null } {
  const res = spawnSync(process.execPath, [WIRE_TS, ...args], {
    cwd,
    encoding: "utf8",
    env: {
      ...process.env,
      USERPROFILE: homeDir,
      HOME: homeDir,
      HOMEDRIVE: undefined,
      HOMEPATH: undefined,
    },
  });
  return { out: (res.stdout ?? "") + (res.stderr ?? ""), status: res.status };
}

/** Every role file this harness's USER profile should hold after a `--roles=user` run. */
function profileFiles(homeDir: string, harness: (typeof HARNESS_IDS)[number]): string[] {
  const dir = join(homeDir, userAgentFilesDir(harness));
  return ROLE_BASENAMES.map((b) => join(dir, b));
}

function assertProfileIntact(homeDir: string, out: string): void {
  for (const harness of HARNESS_IDS) {
    for (const file of profileFiles(homeDir, harness)) {
      assert.equal(
        existsSync(file),
        true,
        `${harness}: ${file} was rendered by this very command and must still exist after it. ` +
          `Full output:\n${out}`,
      );
    }
  }
}

// ---- GUARD 1: root=HOME under roleScope=user ---------------------------------------------------

test("apply --roles=user from HOME: removals=0, the profile survives, and the output SAYS why the sweep was skipped", () => {
  // homeDir is a plain directory, never a repository — that is the whole precondition: no git
  // working tree, so resolveApplyRoot falls back to cwd and root becomes HOME itself.
  const homeDir = freshDir("petbox-home-guard-home-");
  try {
    const { out, status } = runWire(["apply", "--roles=user"], homeDir, homeDir);

    assert.equal(status, WIRE_EXIT.ok, `expected a clean run. Full output:\n${out}`);
    assert.match(out, /root=.*\(via cwd\)/, `the fallback must still be reported honestly. Full output:\n${out}`);

    // THE regression: not one profile file may be removed. Asserted on the ledger's own summary
    // (the count) AND on the disk (the files), because those two disagreeing is precisely the
    // class of defect this card is about.
    assert.match(
      out,
      /apply: summary[^\n]*removals=0/,
      `the project pass must remove nothing when its "project" is the profile. Full output:\n${out}`,
    );
    assert.doesNotMatch(
      out,
      /(would remove|removed)[^\n]*project copy/,
      `no profile file may be classified as a project copy. Full output:\n${out}`,
    );
    assertProfileIntact(homeDir, out);

    // The skip must be VISIBLE. A guard that silently does nothing is indistinguishable from the
    // bug not existing, and the next person to read this output has to be able to see the
    // decision that was made on their behalf.
    assert.match(
      out,
      /sweep SKIPPED for claude-code — the project role directory is the user profile itself/,
      `Full output:\n${out}`,
    );
    assert.match(
      out,
      /sweep SKIPPED for droid — the project role directory is the user profile itself/,
      `Full output:\n${out}`,
    );
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
  }
});

test("PER HARNESS: claude-code and droid are skipped, opencode is NOT — and opencode's real project leftovers are still swept", () => {
  // The guard must be derived from the two directory functions, per harness, not from "does root
  // look like HOME". opencode survived the live incident only because `.opencode/agent` (project)
  // and `.config/opencode/agents` (user) differ by one character. If the guard were a blanket
  // "root is HOME -> skip everything", opencode's genuine leftovers would stop being cleaned and
  // nobody would notice for months.
  const homeDir = freshDir("petbox-home-guard-perharness-");
  try {
    // A real, marked leftover in opencode's PROJECT layout under HOME — the one thing at root=HOME
    // that IS a stale project copy and should still go.
    const opencodeProjectDir = join(homeDir, ".opencode", "agent");
    mkdirSync(opencodeProjectDir, { recursive: true });
    const stale = join(opencodeProjectDir, "petbox-worker.md");
    writeFileSync(stale, "---\nname: petbox-worker\npetbox: managed\n---\n\nstale project copy\n", "utf8");

    const { out, status } = runWire(["apply", "--roles=user"], homeDir, homeDir);
    assert.equal(status, WIRE_EXIT.ok, `Full output:\n${out}`);

    assert.match(out, /sweep SKIPPED for claude-code/, `Full output:\n${out}`);
    assert.match(out, /sweep SKIPPED for droid/, `Full output:\n${out}`);
    assert.doesNotMatch(
      out,
      /sweep SKIPPED for opencode/,
      `opencode does not collide, so its sweep must still run — the guard is per-harness, not ` +
        `per-run. Full output:\n${out}`,
    );

    assert.equal(existsSync(stale), false, `the genuine opencode leftover must still be swept. Full output:\n${out}`);
    assertProfileIntact(homeDir, out);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
  }
});

test("CASE: HOME and root spelled in DIFFERENT cases name one directory — the guard still fires, end to end", () => {
  // On Windows `C:\Users\X` and `C:\users\x` are one directory, so the two sides of the guard's
  // comparison can name the same place while differing as strings. Under the `===` this guard
  // replaces, the sweep would then run and eat the profile on a machine that already "had the fix".
  //
  // WHICH SIDE gets mangled is not a free choice, and this was measured rather than assumed:
  // passing a lowercased path as the child's `cwd` does NOT reach the code as lowercase — Windows
  // canonicalizes the current directory, so `process.cwd()` (and therefore root) comes back in the
  // filesystem's own casing and no trap is set at all. The HOME side has no such normalization:
  // homedir() returns USERPROFILE verbatim, so a lowercased USERPROFILE really does make
  // userAgentFilesRoot lowercase while root stays canonical. That is the trap, and the assertion
  // below on the two spellings in the output is what keeps this test from silently going vacuous
  // if that ever changes.
  //
  // Skipped where the filesystem really is case-sensitive: there the two spellings are genuinely
  // two directories and there would be nothing to detect. The pure comparison is covered on every
  // platform by role-dir-collision.test.ts.
  if (process.platform !== "win32" && process.platform !== "darwin") return;

  const homeDir = freshDir("petbox-home-guard-CASE-");
  try {
    const mangled = homeDir.toLowerCase();
    assert.notEqual(mangled, homeDir, "fixture must actually differ in case, or this proves nothing");
    assert.equal(existsSync(mangled), true, "the case-mangled spelling must reach the same directory");

    // HOME lowercased, cwd in the filesystem's own casing: the comparison's two sides now disagree
    // as STRINGS while naming one directory.
    const { out, status } = runWire(["apply", "--roles=user"], mangled, homeDir);

    assert.equal(status, WIRE_EXIT.ok, `Full output:\n${out}`);
    // The trap is really set: the profile dir is printed lowercase, root is printed canonical.
    assert.match(
      out,
      new RegExp(`user-scope agent dir = ${mangled.replace(/[\\^$.*+?()[\]{}|]/g, "\\$&")}`, "i"),
      `Full output:\n${out}`,
    );
    assert.ok(
      out.includes(`user-scope agent dir = ${join(mangled, userAgentFilesDir("claude-code"))}`) &&
        out.includes(`root=${homeDir} `),
      `the two sides must be spelled DIFFERENTLY in this run, or the test proves nothing about ` +
        `case. Full output:\n${out}`,
    );

    assert.match(out, /sweep SKIPPED for claude-code/, `Full output:\n${out}`);
    assert.match(out, /sweep SKIPPED for droid/, `Full output:\n${out}`);
    assert.match(out, /apply: summary[^\n]*removals=0/, `Full output:\n${out}`);
    assertProfileIntact(mangled, out);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
  }
});

// ---- GUARD 2: root from the cwd fallback under roleScope=project -------------------------------

test("apply (project scope) from a NON-GIT directory: REFUSED, exit 1, nothing written, and the message says what to do instead", () => {
  const homeDir = freshDir("petbox-scatter-home-");
  const looseDir = freshDir("petbox-scatter-loose-"); // deliberately NOT a repository
  try {
    const { out, status } = runWire(["apply"], homeDir, looseDir);

    assert.equal(status, WIRE_EXIT.hard, `a refusal must be visible in the exit code. Full output:\n${out}`);
    assert.match(out, /REFUSED/, `Full output:\n${out}`);
    assert.match(out, /is not a git working tree/, `must name the actual reason. Full output:\n${out}`);
    // Both ways out, by name — a refusal that does not say what to do instead just moves the
    // problem into the operator's head.
    assert.match(out, /--roles=user/, `must offer the machine-scope route. Full output:\n${out}`);
    assert.match(out, /from inside its checkout/, `must offer the project route. Full output:\n${out}`);

    // NOTHING scattered: not one of the three harness layouts may appear.
    for (const rel of [".claude", ".opencode", ".factory"]) {
      assert.equal(
        existsSync(join(looseDir, rel)),
        false,
        `${rel} must not be created in a directory that is not a project. Full output:\n${out}`,
      );
    }
    assert.deepEqual(
      readdirSync(looseDir),
      [],
      `the directory must be byte-for-byte as it was found. Full output:\n${out}`,
    );
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(looseDir, { recursive: true, force: true });
  }
});

test("BOUNDARY: a FRESH CLONE (a git tree, not in the registry) still applies exactly as before — exit 0, skills skipped with the wire hint", () => {
  // The documented, intentional behaviour this guard must not touch (wire.ts's skills branch,
  // apply-skills-skip.test.ts): a friend clones the repo and runs apply before registering it.
  // root = the clone's top, roles render, skills are skipped with a hint, exit 0. `via` is "git"
  // here, so the refusal above never sees this case — which is exactly why `via` is the axis and
  // registry membership is not.
  const homeDir = freshDir("petbox-freshclone-home-");
  const cloneDir = makeGitWorkingTree(freshDir("petbox-freshclone-proj-"));
  try {
    const { out, status } = runWire(["apply"], homeDir, cloneDir);

    assert.equal(status, WIRE_EXIT.ok, `a fresh clone must keep working. Full output:\n${out}`);
    assert.doesNotMatch(out, /REFUSED/, `the scatter guard must not fire on a real checkout. Full output:\n${out}`);
    assert.match(out, /root=.*\(via git\)/, `root must come from git, not the cwd fallback. Full output:\n${out}`);
    assert.match(
      out,
      /skills — skipped \([^)]*is not a registered project; run `wire` here first\)/,
      `the documented unregistered-clone message must be unchanged. Full output:\n${out}`,
    );
    // The roles really did render into the clone — the guard refuses non-projects, not projects.
    for (const b of ROLE_BASENAMES) {
      assert.equal(existsSync(join(cloneDir, ".claude", "agents", b)), true, `Full output:\n${out}`);
    }
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(cloneDir, { recursive: true, force: true });
  }
});

test("BOUNDARY: a SUBDIRECTORY of a checkout is not a fallback — root climbs to the toplevel and applies there", () => {
  // The other half of "via=git is the right axis": running apply from `src/` inside a checkout is
  // an ordinary thing to do, and git answers with the toplevel. If the guard had keyed on
  // "does cwd itself contain .git" it would refuse here.
  const homeDir = freshDir("petbox-subdir-home-");
  const cloneDir = makeGitWorkingTree(freshDir("petbox-subdir-proj-"));
  try {
    const deep = join(cloneDir, "src", "nested");
    mkdirSync(deep, { recursive: true });

    const { out, status } = runWire(["apply"], homeDir, deep);

    assert.equal(status, WIRE_EXIT.ok, `Full output:\n${out}`);
    assert.doesNotMatch(out, /REFUSED/, `Full output:\n${out}`);
    assert.equal(
      existsSync(join(cloneDir, ".claude", "agents", "petbox-worker.md")),
      true,
      `artifacts belong at the toplevel, not in the subdirectory apply was run from. Full output:\n${out}`,
    );
    assert.equal(existsSync(join(deep, ".claude")), false, `Full output:\n${out}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(cloneDir, { recursive: true, force: true });
  }
});

test("BOUNDARY: --roles=user from a non-git directory is NOT refused — it needs no project at all", () => {
  // The refusal is about rendering PROJECT artifacts into a non-project. The user scope renders
  // into the harness profiles and is the very route the refusal recommends, so refusing it too
  // would leave the operator with no way out of the message they were just handed.
  const homeDir = freshDir("petbox-userscope-home-");
  const looseDir = freshDir("petbox-userscope-loose-");
  try {
    const { out, status } = runWire(["apply", "--roles=user"], homeDir, looseDir);

    assert.equal(status, WIRE_EXIT.ok, `Full output:\n${out}`);
    assert.doesNotMatch(out, /REFUSED/, `Full output:\n${out}`);
    assertProfileIntact(homeDir, out);
    // looseDir is not HOME, so nothing there collides and no skip line is printed for it.
    assert.doesNotMatch(out, /sweep SKIPPED/, `Full output:\n${out}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(looseDir, { recursive: true, force: true });
  }
});
