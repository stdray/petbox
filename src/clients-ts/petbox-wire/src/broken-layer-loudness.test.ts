// Spec broken-layer-fails-loudly, end to end through REAL processes.
//
// The spec says one thing — "an unparseable or inconsistent layer MUST stop the build of agent
// artifacts, naming the file and the place of the error; substituting a previously-successful
// result is NOT permitted" — and the owner drew the boundary that makes it implementable in a
// kit whose SessionStart hooks must never crash a session:
//
//   apply / doctor are a BUILD.  A broken layer is a hard refusal: exit WIRE_EXIT.hard, the
//                               absolute path and the parser's message on stderr, and NOT ONE
//                               artifact written or changed — the same contract the dangling-
//                               target gate already had ("Nothing was written").
//   SessionStart is a RENDER.   The process still exits 0 (a hook that crashes is worse than one
//                               that degrades), but the banner LEADS with a marker naming the
//                               broken file, the protocol under it comes from the kit base, and
//                               ~/.petbox/wire.log gets a trace. The loudness is in stdout, not
//                               in the exit code.
//
// definition-source.test.ts covers both at unit level. This file spawns the real CLI and the real
// hook, because "nothing was written" and "the process still exited 0" are process-level claims
// that an in-process test cannot actually make.
//
// Run: node --test src/broken-layer-loudness.test.ts

import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { agentFilesDir } from "./apply-artifacts.ts";
import { HARNESS_IDS } from "./harness-capabilities.ts";
import { PETBOX_MARKER_LINE } from "./origin-marker.ts";
import { WIRE_EXIT } from "./wire-exit.ts";

const HERE = import.meta.dirname;
const WIRE_TS = join(HERE, "wire.ts");

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

/** A project layer whose `layer.json` is fine but whose role document is not JSON at all. */
function writeBrokenProjectLayer(root: string): string {
  const dir = join(root, ".petbox", "agents");
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, "layer.json"), JSON.stringify({ name: "project", mode: "overlay" }), "utf8");
  const broken = join(dir, "petbox-worker.json");
  writeFileSync(broken, '{ "slug": "worker", "tier": ', "utf8");
  return broken;
}

test("apply: a broken layer is a HARD refusal — exit 1, the absolute path and the parser's message on stderr, and NOT ONE artifact written", () => {
  const homeDir = freshDir("petbox-broken-home-");
  const projectDir = freshDir("petbox-broken-proj-");
  try {
    const broken = writeBrokenProjectLayer(projectDir);
    const res = spawnSync(process.execPath, [WIRE_TS, "apply"], {
      cwd: projectDir,
      encoding: "utf8",
      env: { ...process.env, USERPROFILE: homeDir, HOME: homeDir, HOMEDRIVE: undefined, HOMEPATH: undefined },
    });
    const stderr = res.stderr ?? "";
    const out = (res.stdout ?? "") + stderr;

    assert.equal(res.status, WIRE_EXIT.hard, `Full output:\n${out}`);
    assert.ok(stderr.includes(broken), `stderr must name the broken file by ABSOLUTE path:\n${stderr}`);
    assert.match(stderr, /is not valid JSON/, `stderr must carry the parser's own message:\n${stderr}`);
    // Nothing was written — not the role artifacts, not the harness directories themselves.
    for (const h of HARNESS_IDS) {
      assert.ok(
        !existsSync(join(projectDir, agentFilesDir(h))),
        `${h}: apply touched disk despite refusing. Full output:\n${out}`,
      );
    }
    // …and no previously-successful result was substituted for the unreadable one (D15).
    assert.doesNotMatch(out, /LKG|last known good/i, `Full output:\n${out}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("apply: a broken layer discovered on a SECOND run leaves the artifacts from the FIRST run byte-for-byte untouched", () => {
  // "Nothing was written" has to mean nothing CHANGED, not merely nothing was created — the
  // realistic shape is an operator editing a layer under a project that already has artifacts.
  const homeDir = freshDir("petbox-broken-home-");
  const projectDir = freshDir("petbox-broken-proj-");
  try {
    const env = { ...process.env, USERPROFILE: homeDir, HOME: homeDir, HOMEDRIVE: undefined, HOMEPATH: undefined };
    const first = spawnSync(process.execPath, [WIRE_TS, "apply"], { cwd: projectDir, encoding: "utf8", env });
    assert.equal(first.status, WIRE_EXIT.ok, `setup:\n${(first.stdout ?? "") + (first.stderr ?? "")}`);
    const artifact = join(projectDir, agentFilesDir("claude-code"), "petbox-worker.md");
    const before = readFileSync(artifact, "utf8");
    assert.ok(before.includes(PETBOX_MARKER_LINE), "setup: the artifact must be one of ours");

    writeBrokenProjectLayer(projectDir);
    const second = spawnSync(process.execPath, [WIRE_TS, "apply"], { cwd: projectDir, encoding: "utf8", env });
    assert.equal(second.status, WIRE_EXIT.hard, `${(second.stdout ?? "") + (second.stderr ?? "")}`);
    assert.equal(readFileSync(artifact, "utf8"), before, "a refused run modified an existing artifact");
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

// ---- SessionStart: loud in stdout, never in the exit code -------------------------------------

function runHook(script: string, cwd: string, homeDir: string): Promise<{ code: number | null; stdout: string; stderr: string }> {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [join(HERE, script)], {
      cwd,
      env: { ...process.env, HOME: homeDir, USERPROFILE: homeDir, FAKE_BROKEN_LAYER_KEY: "k" },
    });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (c) => (stdout += c));
    child.stderr.on("data", (c) => (stderr += c));
    child.on("error", reject);
    child.on("close", (code) => resolve({ code, stdout, stderr }));
    child.stdin.write(
      JSON.stringify({ session_id: "t", cwd, hook_event_name: "SessionStart", source: "startup" }),
    );
    child.stdin.end();
  });
}

/** A registered project pointing at a dead port: the canon fetch degrades to nothing, which is
 * orthogonal to what is under test and keeps the banner small. */
function registerProject(homeDir: string, projectDir: string): void {
  mkdirSync(join(homeDir, ".petbox"), { recursive: true });
  writeFileSync(
    join(homeDir, ".petbox", "projects.json"),
    JSON.stringify({
      entries: [
        { prefix: projectDir, project: "broken-layer-proj", envVar: "FAKE_BROKEN_LAYER_KEY", baseUrl: "http://127.0.0.1:1" },
      ],
    }),
    "utf8",
  );
}

for (const script of ["pull-memory.ts", "droid-pull-memory.ts"] as const) {
  test(`${script}: a broken layer keeps the session alive (exit 0) but the banner LEADS with the broken file's path`, async () => {
    const homeDir = freshDir("petbox-broken-hook-home-");
    const projectDir = join(homeDir, "proj");
    mkdirSync(projectDir, { recursive: true });
    try {
      registerProject(homeDir, projectDir);
      const broken = writeBrokenProjectLayer(projectDir);

      const res = await runHook(script, projectDir, homeDir);

      assert.equal(res.code, 0, `a SessionStart hook must never fail the session. stderr:\n${res.stderr}`);
      assert.ok(res.stdout.length > 0, "the protocol must still ship — a silent hook is the worse failure");
      // droid's SessionStart contract is the structured JSON envelope, Claude Code's is raw
      // stdout — unwrap so the SAME assertions apply to the text the agent actually receives.
      const banner =
        script === "droid-pull-memory.ts"
          ? (JSON.parse(res.stdout) as { hookSpecificOutput: { additionalContext: string } })
              .hookSpecificOutput.additionalContext
          : res.stdout;
      assert.ok(
        banner.includes(broken),
        `the banner must name the broken file by absolute path. banner:\n${banner.slice(0, 1200)}`,
      );
      assert.match(banner, /BROKEN/, `banner:\n${banner.slice(0, 1200)}`);
      // The marker leads: it must survive any tail truncation of everything after it.
      const markerIndex = banner.indexOf(broken);
      const protocolIndex = banner.indexOf("PetBox memory active");
      assert.ok(protocolIndex >= 0, `the protocol block must be present. banner:\n${banner.slice(0, 1200)}`);
      assert.ok(
        markerIndex < protocolIndex,
        "the broken-layer marker must come BEFORE the protocol block, not trail it",
      );

      const log = readFileSync(join(homeDir, ".petbox", "wire.log"), "utf8");
      assert.ok(log.includes(broken), `the breakage must leave a durable wire.log trace:\n${log}`);
    } finally {
      rmSync(homeDir, { recursive: true, force: true });
    }
  });
}
