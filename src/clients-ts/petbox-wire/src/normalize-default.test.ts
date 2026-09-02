// End-to-end + unit tests for the ONE-DEFAULT normalization (card:
// normalize-all-environments-to-default).
//
// What is actually at risk here, and therefore what these tests are for: the command under test
// WRITES and DELETES files across eight project directories, seven of which belong to other
// people, and it renders into the owner's own harness profiles where unrelated files already
// live. Every test below is a safety property, not a formatting check:
//
//   1. `--roles=user` renders the roles ONCE into the three harness profiles and sweeps the
//      project copies — marker-gated, so an unmarked file at the same path survives byte for byte.
//   2. The foreign files already sitting in `~/.factory/droids/` (real ones on the owner's
//      machine: worker.md, scrutiny-feature-reviewer.md, user-testing-flow-validator.md) are
//      untouched. This is the proof that the command is safe for OTHER consumers, not just here.
//   3. `--adopt` overwrites an unmarked file at exactly the path it names and nothing else; a
//      second unmarked file in the same run is still refused and the run still exits 1; a
//      `petbox: manual` declaration outranks it; a named path apply never sees is an error, not a
//      silent success.
//   4. The `--dry-run` summary is computed from the SAME ledger the per-file lines are rendered
//      from — asserted by counting "would write" lines in the real output and comparing.
//   5. The `.gitignore` policy block is spliced in without disturbing a single other line.
//   6. Two identical runs in a row: the second changes nothing and exits 0.
//
// --offline throughout: no network, no fake server. That also means the skills step is
// intentionally skipped, so role behavior is exercised in isolation.
//
// Run: node --test src/normalize-default.test.ts

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  realpathSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { createAdoptSet, pathKey } from "./adopt-paths.ts";
import { formatAction, summarize, type ApplyAction } from "./apply-ledger.ts";
import { GITIGNORE_BEGIN, GITIGNORE_END, spliceGitignoreBlock } from "./gitignore-block.ts";
import { managedGitignoreEntries, projectRoleFiles } from "./managed-paths.ts";
import { PETBOX_MANUAL_LINE, PETBOX_MARKER_LINE } from "./origin-marker.ts";
import { loadWireConfig, userAgentFilesDir } from "./role-scope.ts";
import { WIRE_EXIT } from "./wire-exit.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

function writeRegistry(homeDir: string, entries: Array<{ prefix: string; project: string; envVar: string }>): void {
  mkdirSync(join(homeDir, ".petbox"), { recursive: true });
  writeFileSync(join(homeDir, ".petbox", "projects.json"), JSON.stringify({ entries }, null, 2), "utf8");
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

/** Plant a project-scope role artifact of OURS (marker present) — a sweep candidate. */
function plantOwnedRole(projectDir: string, rel: string[], name: string): string {
  const dir = join(projectDir, ...rel);
  mkdirSync(dir, { recursive: true });
  const p = join(dir, name);
  writeFileSync(p, `---\nname: ${name}\n${PETBOX_MARKER_LINE}\n---\nold generation\n`, "utf8");
  return p;
}

/** Plant a file in OUR namespace that is NOT ours (no marker) — must never be deleted. */
function plantForeign(dir: string, name: string, body: string): { path: string; bytes: Buffer } {
  mkdirSync(dir, { recursive: true });
  const p = join(dir, name);
  writeFileSync(p, body, "utf8");
  return { path: p, bytes: readFileSync(p) };
}

const USER_ROLE_DIRS = [".claude/agents", ".config/opencode/agents", ".factory/droids"] as const;

function userRoleFileCount(homeDir: string): number {
  let n = 0;
  for (const rel of USER_ROLE_DIRS) {
    const dir = join(homeDir, ...rel.split("/"));
    if (!existsSync(dir)) continue;
    n += readdirSync(dir).filter((f) => /^petbox-[a-z0-9_-]+\.md$/.test(f)).length;
  }
  return n;
}

// ---------------------------------------------------------------------------------------------
// 1 + 2: roles move to the user profile; project copies are swept; foreign files survive.
// ---------------------------------------------------------------------------------------------

test("apply --roles=user: renders roles ONCE into the three harness profiles and sweeps the project's own copies", () => {
  const homeDir = freshDir("petbox-norm-home-");
  const proj = freshDir("petbox-norm-proj-");
  try {
    writeRegistry(homeDir, [{ prefix: proj, project: "norm-a", envVar: "PETBOX_NORM_A_API_KEY" }]);
    // The project starts in the OLD shape: role copies in all three per-project layouts.
    const owned = [
      plantOwnedRole(proj, [".claude", "agents"], "petbox-worker.md"),
      plantOwnedRole(proj, [".opencode", "agent"], "petbox-worker.md"),
      plantOwnedRole(proj, [".factory", "droids"], "petbox-worker.md"),
    ];

    const run = runWire(["apply", "--offline", "--roles=user"], homeDir, proj);
    assert.equal(run.status, WIRE_EXIT.ok, `expected exit 0; output:\n${run.out}`);

    // 15 = 5 declared roles x 3 harness profiles. The whole point of the card: not 90.
    assert.equal(userRoleFileCount(homeDir), 15, `expected 15 user-scope role files; output:\n${run.out}`);
    // opencode's user directory is the PLURAL `agents`; the singular is opencode legacy and must
    // never be the render target.
    assert.equal(existsSync(join(homeDir, ".config", "opencode", "agents")), true);
    assert.equal(existsSync(join(homeDir, ".config", "opencode", "agent")), false);
    assert.equal(userAgentFilesDir("opencode"), ".config/opencode/agents");

    // Every project copy is gone, and opencode's now-empty legacy directory with it.
    for (const p of owned) assert.equal(existsSync(p), false, `${p} survived the sweep; output:\n${run.out}`);
    assert.deepEqual(projectRoleFiles(proj), [], `output:\n${run.out}`);
    assert.equal(existsSync(join(proj, ".opencode", "agent")), false, "legacy .opencode/agent should be gone");
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(proj, { recursive: true, force: true });
  }
});

test("apply --roles=user: foreign files in the harness profile and in the project are left byte-for-byte alone", () => {
  const homeDir = freshDir("petbox-norm-foreign-home-");
  const proj = freshDir("petbox-norm-foreign-proj-");
  try {
    writeRegistry(homeDir, [{ prefix: proj, project: "norm-b", envVar: "PETBOX_NORM_B_API_KEY" }]);

    // The three real files on the owner's machine, reproduced: someone else's droids sitting in
    // ~/.factory/droids, carrying no PetBox marker. The command MUST leave them exactly as is —
    // this is the property that makes it safe to ship to other consumers.
    const droidsDir = join(homeDir, ".factory", "droids");
    const strangers = [
      plantForeign(droidsDir, "worker.md", "---\nname: worker\n---\nsomebody else's droid\n"),
      plantForeign(droidsDir, "scrutiny-feature-reviewer.md", "# not ours\n"),
      plantForeign(droidsDir, "user-testing-flow-validator.md", "# also not ours\n"),
    ];
    // …and an unmarked file inside the project, in OUR namespace, at a path the sweep visits.
    const projectStranger = plantForeign(
      join(proj, ".claude", "agents"),
      "petbox-worker.md",
      "---\nname: petbox-worker\n---\nhand-written, no marker\n",
    );

    const run = runWire(["apply", "--offline", "--roles=user"], homeDir, proj);

    for (const s of strangers) {
      assert.equal(existsSync(s.path), true, `${s.path} was deleted`);
      assert.deepEqual(readFileSync(s.path), s.bytes, `${s.path} was modified`);
    }
    assert.equal(existsSync(projectStranger.path), true, "an unmarked project file was deleted");
    assert.deepEqual(readFileSync(projectStranger.path), projectStranger.bytes, "an unmarked project file was modified");
    assert.match(run.out, /left .*petbox-worker\.md in place/);
    // …and it does NOT count as a leftover copy of ours. It cannot: apply will never delete it, so
    // counting it would report a project as permanently off-policy with no command that fixes it.
    assert.deepEqual(projectRoleFiles(proj), [], "a foreign file in our namespace must not count as ours");
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(proj, { recursive: true, force: true });
  }
});

test("apply --roles=user: the policy is remembered, so a later PLAIN apply does not re-render project copies", () => {
  const homeDir = freshDir("petbox-norm-policy-home-");
  const proj = freshDir("petbox-norm-policy-proj-");
  try {
    writeRegistry(homeDir, [{ prefix: proj, project: "norm-c", envVar: "PETBOX_NORM_C_API_KEY" }]);

    const first = runWire(["apply", "--offline", "--roles=user"], homeDir, proj);
    assert.equal(first.status, WIRE_EXIT.ok, `output:\n${first.out}`);
    assert.equal(loadWireConfig(homeDir).roleScope, "user");

    // No flag this time. Without the persisted policy this would write 15 role files back into
    // the project — the exact regression the card's item 1 is guarding against.
    const second = runWire(["apply", "--offline"], homeDir, proj);
    assert.equal(second.status, WIRE_EXIT.ok, `output:\n${second.out}`);
    assert.match(second.out, /roles → user scope \(from config\)/);
    assert.deepEqual(projectRoleFiles(proj), [], `a plain apply re-created project role copies:\n${second.out}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(proj, { recursive: true, force: true });
  }
});

test("apply --roles=user --dry-run: writes nothing at all, and does NOT persist the policy", () => {
  const homeDir = freshDir("petbox-norm-dry-home-");
  const proj = freshDir("petbox-norm-dry-proj-");
  try {
    writeRegistry(homeDir, [{ prefix: proj, project: "norm-d", envVar: "PETBOX_NORM_D_API_KEY" }]);
    const owned = plantOwnedRole(proj, [".claude", "agents"], "petbox-worker.md");

    const run = runWire(["apply", "--offline", "--roles=user", "--dry-run"], homeDir, proj);
    assert.equal(run.status, WIRE_EXIT.ok, `output:\n${run.out}`);
    assert.equal(userRoleFileCount(homeDir), 0, "a dry run must not create any user-scope file");
    assert.equal(existsSync(owned), true, "a dry run must not delete a project role copy");
    assert.equal(existsSync(join(homeDir, ".petbox", "wire.json")), false, "a dry run must not persist the policy");
    assert.match(run.out, /NOT persisted/);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(proj, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------------------------
// 6: idempotency — the card's own acceptance check.
// ---------------------------------------------------------------------------------------------

test("apply --all --roles=user twice: the second run changes nothing, every role line says unchanged, exit 0", () => {
  const homeDir = freshDir("petbox-norm-idem-home-");
  const projA = freshDir("petbox-norm-idem-a-");
  const projB = freshDir("petbox-norm-idem-b-");
  try {
    writeRegistry(homeDir, [
      { prefix: projA, project: "idem-a", envVar: "PETBOX_IDEM_A_API_KEY" },
      { prefix: projB, project: "idem-b", envVar: "PETBOX_IDEM_B_API_KEY" },
    ]);
    plantOwnedRole(projA, [".claude", "agents"], "petbox-worker.md");

    const first = runWire(["apply", "--all", "--offline", "--roles=user"], homeDir, projA);
    assert.equal(first.status, WIRE_EXIT.ok, `output:\n${first.out}`);

    const second = runWire(["apply", "--all", "--offline", "--roles=user"], homeDir, projA);
    assert.equal(second.status, WIRE_EXIT.ok, `output:\n${second.out}`);
    // Nothing written, nothing removed, anywhere — the roles pass and both project passes.
    const writeLines = second.out.split("\n").filter((l) => / would write | would remove |: wrote |: removed /.test(l));
    assert.deepEqual(writeLines, [], `second run was not a no-op:\n${writeLines.join("\n")}`);
    assert.match(second.out, /\[roles:user\]: summary \(applied\) — writes=0 \(roles=0 skills=0\) unchanged=15/);
    assert.equal(userRoleFileCount(homeDir), 15);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projA, { recursive: true, force: true });
    rmSync(projB, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------------------------
// 4: the dry-run summary is the summary of the run that would execute.
// ---------------------------------------------------------------------------------------------

test("apply --dry-run: the number of 'would write' LINES equals the summary's own writes count", () => {
  const homeDir = freshDir("petbox-norm-count-home-");
  const proj = freshDir("petbox-norm-count-proj-");
  try {
    writeRegistry(homeDir, [{ prefix: proj, project: "count-a", envVar: "PETBOX_COUNT_A_API_KEY" }]);

    const run = runWire(["apply", "--offline", "--dry-run"], homeDir, proj);
    assert.equal(run.status, WIRE_EXIT.ok, `output:\n${run.out}`);

    const lines = run.out.split("\n");
    const wouldWrite = lines.filter((l) => l.includes(" would write ")).length;
    const summaryLine = lines.find((l) => l.includes("summary (dry run"));
    assert.ok(summaryLine, `no summary line in output:\n${run.out}`);
    const m = /writes=(\d+) /.exec(summaryLine);
    assert.ok(m, `summary line has no writes= count: ${summaryLine}`);
    assert.ok(wouldWrite > 0, `expected at least one 'would write' line:\n${run.out}`);
    assert.equal(
      Number(m[1]),
      wouldWrite,
      `the summary disagrees with the lines above it — that is the bug this card names ` +
        `(apply-all-summary-undercounts-writes). summary=${m[1]}, lines=${wouldWrite}\n${run.out}`,
    );
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(proj, { recursive: true, force: true });
  }
});

test("ledger: the summary and the rendered lines cannot disagree — 'would write' is exclusive to a write action", () => {
  const actions: ApplyAction[] = [
    { kind: "write", subject: "role", path: "/a" },
    { kind: "write", subject: "skill", path: "/b" },
    { kind: "unchanged", subject: "role", path: "/c" },
    { kind: "remove", subject: "orphan", path: "/d", note: "gone from the definition" },
    { kind: "refuse", subject: "skill", path: "/e" },
    { kind: "kept", subject: "role", path: "/f" },
    { kind: "manual", subject: "skill", path: "/g" },
  ];
  const rendered = actions.map((a) => formatAction("x", a, true).text);
  const s = summarize(actions);
  assert.equal(s.filesWritten, 2);
  assert.equal(s.roleFilesWritten, 1);
  assert.equal(s.skillFilesWritten, 1);
  assert.equal(rendered.filter((l) => l.includes("would write")).length, s.filesWritten);
  assert.equal(rendered.filter((l) => l.includes("would remove")).length, s.removed);
  assert.equal(s.refused, 1);
  assert.deepEqual(s.refusedPaths, ["/e"]);
});

// ---------------------------------------------------------------------------------------------
// 3: --adopt is per-path and nothing else.
// ---------------------------------------------------------------------------------------------

test("--adopt: an unmarked file at the NAMED path is overwritten; a second one is still refused and the run still exits 1", () => {
  const homeDir = freshDir("petbox-norm-adopt-home-");
  const proj = freshDir("petbox-norm-adopt-proj-");
  try {
    writeRegistry(homeDir, [{ prefix: proj, project: "adopt-a", envVar: "PETBOX_ADOPT_A_API_KEY" }]);
    const named = plantForeign(
      join(proj, ".claude", "agents"),
      "petbox-worker.md",
      "---\nname: petbox-worker\n---\nold PetBox render, pre-marker\n",
    );
    const notNamed = plantForeign(
      join(proj, ".claude", "agents"),
      "petbox-explore.md",
      "---\nname: petbox-explore\n---\nalso unmarked\n",
    );

    // Without --adopt both are refused and the run is a hard failure.
    const bare = runWire(["apply", "--offline"], homeDir, proj);
    assert.equal(bare.status, WIRE_EXIT.hard, `output:\n${bare.out}`);
    assert.deepEqual(readFileSync(named.path), named.bytes);
    assert.deepEqual(readFileSync(notNamed.path), notNamed.bytes);

    // With --adopt on exactly one path: that one is written, the other is STILL refused, and the
    // run still exits 1. There is no bulk mode and no --force.
    const adopted = runWire(["apply", "--offline", "--adopt", named.path], homeDir, proj);
    assert.equal(adopted.status, WIRE_EXIT.hard, `the un-named refusal must still fail the run:\n${adopted.out}`);
    assert.match(adopted.out, /ADOPTED/);
    assert.notDeepEqual(readFileSync(named.path), named.bytes, "the named path should have been overwritten");
    assert.match(readFileSync(named.path, "utf8"), new RegExp(PETBOX_MARKER_LINE));
    assert.deepEqual(readFileSync(notNamed.path), notNamed.bytes, "an un-named unmarked file was touched");
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(proj, { recursive: true, force: true });
  }
});

test("--adopt: a `petbox: manual` declaration outranks it — the project's own path is never adopted", () => {
  const homeDir = freshDir("petbox-norm-manual-home-");
  const proj = freshDir("petbox-norm-manual-proj-");
  try {
    writeRegistry(homeDir, [{ prefix: proj, project: "adopt-b", envVar: "PETBOX_ADOPT_B_API_KEY" }]);
    const declared = plantForeign(
      join(proj, ".claude", "agents"),
      "petbox-worker.md",
      `---\nname: petbox-worker\n${PETBOX_MANUAL_LINE}\n---\nthe project owns this path\n`,
    );

    const run = runWire(["apply", "--offline", "--adopt", declared.path], homeDir, proj);
    assert.deepEqual(readFileSync(declared.path), declared.bytes, "a `petbox: manual` file was overwritten by --adopt");
    assert.equal(run.status, WIRE_EXIT.hard, `output:\n${run.out}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(proj, { recursive: true, force: true });
  }
});

test("--adopt: a path apply never considered is reported and fails the run, never a silent exit 0", () => {
  const homeDir = freshDir("petbox-norm-adopt-miss-home-");
  const proj = freshDir("petbox-norm-adopt-miss-proj-");
  try {
    writeRegistry(homeDir, [{ prefix: proj, project: "adopt-c", envVar: "PETBOX_ADOPT_C_API_KEY" }]);
    const bogus = join(proj, ".claude", "agents", "petbox-nope-typo.md");

    const run = runWire(["apply", "--offline", "--adopt", bogus], homeDir, proj);
    assert.equal(run.status, WIRE_EXIT.hard, `a typo'd --adopt must not pass silently:\n${run.out}`);
    assert.match(run.out, /never considered/);
    assert.match(run.out, /petbox-nope-typo\.md/);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(proj, { recursive: true, force: true });
  }
});

test("--adopt: a relative path is refused up front (it would resolve against the wrong directory under --all)", () => {
  const homeDir = freshDir("petbox-norm-adopt-rel-home-");
  const proj = freshDir("petbox-norm-adopt-rel-proj-");
  try {
    writeRegistry(homeDir, [{ prefix: proj, project: "adopt-d", envVar: "PETBOX_ADOPT_D_API_KEY" }]);
    const run = runWire(["apply", "--offline", "--adopt", ".claude/agents/petbox-worker.md"], homeDir, proj);
    assert.equal(run.status, WIRE_EXIT.usage, `output:\n${run.out}`);
    assert.match(run.out, /ABSOLUTE path/);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(proj, { recursive: true, force: true });
  }
});

test("adopt set: matching is normalized (slash direction, and case on win32 only); `consider` is what marks a path used", () => {
  const set = createAdoptSet(["C:\\x\\y\\Z.md"], "win32");
  assert.equal(set.has("C:/x/y/z.md"), true, "win32 comparison must fold case and slashes");
  assert.deepEqual(set.unmatched(), ["C:\\x\\y\\Z.md"], "`has` alone must not mark a path as used");
  set.consider("C:/x/y/z.md");
  assert.deepEqual(set.unmatched(), [], "`consider` marks it used, so a re-run stays idempotent");

  const posix = createAdoptSet(["/x/Z.md"], "linux");
  assert.equal(posix.has("/x/z.md"), false, "on POSIX two paths differing in case are two files");
  // Trailing separators are trimmed, and the key is a fixed point. Asserted through pathKey's own
  // output rather than against a literal: `resolve` is bound to the HOST platform (on Windows it
  // turns "/a/b" into "D:/a/b"), so a literal expectation here would test the runner, not the code.
  const key = pathKey(join("a", "b") + "/", "linux");
  assert.equal(key.endsWith("/"), false, "a trailing separator must not survive into the key");
  assert.equal(pathKey(key, "linux"), key, "pathKey must be a fixed point");
});

// ---------------------------------------------------------------------------------------------
// 5: the single git policy.
// ---------------------------------------------------------------------------------------------

test("gitignore entries: SKILL paths only — no role globs are ever written into a project's repo", () => {
  const entries = managedGitignoreEntries();
  // Runs with no git binary and no temp project, so the decision stays pinned even on a machine
  // where the end-to-end .gitignore test below bails out.
  assert.ok(entries.length > 0);
  assert.ok(entries.every((e) => e.startsWith(".claude/skills/") || e.startsWith(".factory/skills/")), entries.join(","));
  assert.equal(
    entries.some((e) => e.includes("/agents") || e.includes("/agent/") || e.includes("/droids")),
    false,
    `role paths must not appear — after the normalization a project holds none, so these would be ` +
      `ignore rules for known-empty paths in someone else's repository: ${entries.join(",")}`,
  );
  assert.deepEqual([...entries].sort(), entries, "the block must be sorted, or it churns between applies");
});

test("gitignore block: spliced into an existing file without disturbing any other line, and idempotent", () => {
  const entries = ["a/", "b/"];
  const original = "node_modules/\n*.log\n";
  const once = spliceGitignoreBlock(original, entries);
  assert.match(once, /^node_modules\/\n\*\.log\n/, "existing lines must survive at the top, byte for byte");
  assert.ok(once.includes(GITIGNORE_BEGIN) && once.includes(GITIGNORE_END));
  assert.ok(once.includes("\na/\n") && once.includes("\nb/\n"));
  assert.equal(spliceGitignoreBlock(once, entries), once, "a second splice must be a no-op");

  // A changed entry set replaces ONLY the block.
  const changed = spliceGitignoreBlock(once, ["c/"]);
  assert.match(changed, /^node_modules\/\n\*\.log\n/);
  assert.equal(changed.includes("\na/\n"), false, "the old block's entries must be gone");
  assert.ok(changed.includes("\nc/\n"));

  // No file at all: the block IS the file.
  const fresh = spliceGitignoreBlock(null, entries);
  assert.ok(fresh.startsWith(GITIGNORE_BEGIN));
  assert.ok(fresh.endsWith(GITIGNORE_END + "\n"));
});

test("gitignore policy: apply writes the managed block into the project's .gitignore, once", () => {
  const homeDir = freshDir("petbox-norm-gi-home-");
  const proj = freshDir("petbox-norm-gi-proj-");
  try {
    writeRegistry(homeDir, [{ prefix: proj, project: "gi-a", envVar: "PETBOX_GI_A_API_KEY" }]);
    // apply only writes a .gitignore where a git worktree actually is (root resolved `via git`).
    const init = spawnSync("git", ["init", "-q"], { cwd: proj, encoding: "utf8" });
    if (init.status !== 0) return; // no git on this machine — the policy has nothing to apply to
    writeFileSync(join(proj, ".gitignore"), "# mine\nbuild/\n", "utf8");

    const first = runWire(["apply", "--offline", "--roles=user"], homeDir, proj);
    assert.equal(first.status, WIRE_EXIT.ok, `output:\n${first.out}`);
    const gi = readFileSync(join(proj, ".gitignore"), "utf8");
    assert.match(gi, /^# mine\nbuild\/\n/, "the project's own lines must survive untouched");
    for (const e of managedGitignoreEntries()) assert.ok(gi.includes(`\n${e}\n`), `missing ignore entry ${e}`);
    // The block covers SKILLS only (owner decision 2026-09-02). Role globs were in here and came
    // back out: after the normalization a project holds no role artifacts at all, so those rules
    // would be ignore lines for known-empty paths written into seven other people's repositories.
    // Asserted against the rendered file, not just the entry list — iterating the function's own
    // output would pass whatever the function happened to return, which is exactly what let the
    // role globs sit here unnoticed.
    for (const roleDir of [".claude/agents", ".opencode/agent", ".factory/droids"]) {
      assert.equal(gi.includes(roleDir), false, `.gitignore must carry no role path, found ${roleDir}:\n${gi}`);
    }
    assert.ok(gi.includes("\n.claude/skills/petbox/\n") && gi.includes("\n.factory/skills/petbox/\n"));

    const second = runWire(["apply", "--offline", "--roles=user"], homeDir, proj);
    assert.equal(second.status, WIRE_EXIT.ok, `output:\n${second.out}`);
    assert.equal(readFileSync(join(proj, ".gitignore"), "utf8"), gi, "a second apply must not touch .gitignore");
    assert.match(second.out, /\.gitignore unchanged/);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(proj, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------------------------
// 3 (reporting side): status --all counts roles and git state.
// ---------------------------------------------------------------------------------------------

test("status --all: reports project role copies and the git state of managed paths, and stays read-only", () => {
  const homeDir = freshDir("petbox-norm-status-home-");
  const proj = freshDir("petbox-norm-status-proj-");
  try {
    writeRegistry(homeDir, [{ prefix: proj, project: "st-a", envVar: "PETBOX_ST_A_API_KEY" }]);
    mkdirSync(join(homeDir, ".petbox"), { recursive: true });
    writeFileSync(join(homeDir, ".petbox", "wire.json"), JSON.stringify({ roleScope: "user" }) + "\n", "utf8");
    const owned = plantOwnedRole(proj, [".claude", "agents"], "petbox-worker.md");

    const run = runWire(["status", "--all", "--offline"], homeDir, proj);
    assert.equal(run.status, WIRE_EXIT.ok, `status must always exit 0; output:\n${run.out}`);
    assert.match(run.out, /roles → user scope/);
    assert.match(run.out, /project role file\(s\)/);
    assert.match(run.out, /STALE/, "a project still holding role copies under the user policy is stale");
    assert.equal(existsSync(owned), true, "status must never delete anything");
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(proj, { recursive: true, force: true });
  }
});
