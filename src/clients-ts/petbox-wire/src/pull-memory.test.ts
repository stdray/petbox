// Process-level regression test for the SessionStart hooks' wall-clock behavior.
//
// GAP THIS CLOSES: every other test in this kit imports functions and calls them in-process.
// None of them spawn a hook as a REAL OS process the way Claude Code / Factory Droid actually
// invoke it (stdin piped, argv-less, process exit observed from outside). That gap is exactly
// why a whole class of bug — exit-time crashes, stdout-flush races, and event-loop-drain
// latency — was invisible to `node --test` for as long as it was: nothing here ever measured
// "how long does the OS process actually take, end to end."
//
// This test spawns the REAL pull-memory.ts / droid-pull-memory.ts files as child processes,
// pointed (via an isolated HOME so the real ~/.petbox/projects.json is never touched) at a
// throwaway local HTTP server that answers the ONE endpoint the hook still calls (canon)
// as fast as a loopback socket allows. The definition leg used to be a second fetch here; after
// card wire-stops-fetching-definition it is a local file cascade with no network at all, which
// is itself part of what keeps this wall clock small. Against a server that is NOT slow, the hook's own
// wall-clock overhead must stay small and CONSTANT — if a future change reintroduces an
// uncleared timer or an unreffed-too-late handle, this is the test that catches it (a real
// remote server's occasional slowness is a separate, already-covered concern — see this
// commit's notes on the false "dangling timer" theory that was investigated and disproven for
// that case; this test is deliberately immune to that because the fake server never stalls).
//
// Run: node --test src/pull-memory.test.ts

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync, mkdirSync } from "node:fs";
import http from "node:http";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { HARNESS_INLINE_HARD_LIMIT_BYTES } from "./session-budget.ts";

const HERE = dirname(fileURLToPath(import.meta.url));

// Generous enough to never flake on a loaded CI box, tight enough to fail hard if a
// regression reintroduces anything resembling the old ~8s (timeout-budget) or ~18s
// (lingering-socket) waits this kit's exit path was built to avoid.
const WALL_CLOCK_BUDGET_MS = 2000;

type SpawnResult = { code: number | null; stdout: string; stderr: string; wallMs: number };

function runHook(
  scriptPath: string,
  input: string,
  env: NodeJS.ProcessEnv,
  cwd: string,
): Promise<SpawnResult> {
  return new Promise((resolve, reject) => {
    const t0 = Date.now();
    const child = spawn(process.execPath, [scriptPath], { env, cwd });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (c) => (stdout += c));
    child.stderr.on("data", (c) => (stderr += c));
    child.on("error", reject);
    child.on("close", (code) => {
      resolve({ code, stdout, stderr, wallMs: Date.now() - t0 });
    });
    child.stdin.write(input);
    child.stdin.end();
  });
}

// A throwaway local server that answers the one hook-called endpoint immediately: 200 with an
// empty canon, so no canon block is appended. Content is deliberately uninteresting — this test
// is about wall clock.
function startFastFakeServer(): Promise<{ port: number; close: () => Promise<void> }> {
  return new Promise((resolve) => {
    const server = http.createServer((req, res) => {
      if (req.url?.includes("/memory/") && req.url?.includes("/canon")) {
        res
          .writeHead(200, { "Content-Type": "application/json" })
          .end(JSON.stringify({ project: null, workspace: null }));
        return;
      }
      res.writeHead(404).end();
    });
    server.listen(0, "127.0.0.1", () => {
      const port = (server.address() as { port: number }).port;
      resolve({
        port,
        close: () => new Promise((r) => server.close(() => r())),
      });
    });
  });
}

// A fake server whose canon response is deliberately oversized (well past the measured
// harness inline budget — see session-budget.ts) so the hook's drop-and-log path (the fix for
// startup-banner-truncated-86-percent) is exercised end to end as a REAL process, not just via
// the in-process unit tests in session-budget.test.ts.
function startFatCanonFakeServer(): Promise<{ port: number; close: () => Promise<void> }> {
  return new Promise((resolve) => {
    const fatBody = "canon-line ".repeat(5000); // ~55KB — far past any reasonable budget
    const server = http.createServer((req, res) => {
      if (req.url?.includes("/memory/") && req.url?.includes("/canon")) {
        res
          .writeHead(200, { "Content-Type": "application/json" })
          .end(JSON.stringify({ project: { body: fatBody, updatedAt: null, version: 1 }, workspace: null }));
        return;
      }
      res.writeHead(404).end();
    });
    server.listen(0, "127.0.0.1", () => {
      const port = (server.address() as { port: number }).port;
      resolve({
        port,
        close: () => new Promise((r) => server.close(() => r())),
      });
    });
  });
}

// A fake server whose canon response is sized to REPRODUCE the real `$system` measurements
// (bug: owner-only-skills-block-silently-dropped-in-real-project), not a round number: whole
// canon 4058B, project-leg-only 2773B (a ~1285B workspace leg) — the exact bytes measured against
// the live server and its offline cache. Combined with a real materialized owner-only skill
// (~1281B unshrunk) and the real compact-mode protocol, this is the scenario that broke: the
// block appended unconditionally after an already-budget-fitting protocol+canon banner exceeded
// the harness's 10 000B hard limit on every session start.
function startRealSystemScaleCanonFakeServer(): Promise<{ port: number; close: () => Promise<void> }> {
  return new Promise((resolve) => {
    const projectBody = "X".repeat(2598);
    const workspaceBody = "Y".repeat(1268);
    const server = http.createServer((req, res) => {
      if (req.url?.includes("/memory/") && req.url?.includes("/canon")) {
        res.writeHead(200, { "Content-Type": "application/json" }).end(
          JSON.stringify({
            project: { body: projectBody, updatedAt: null, version: 1 },
            workspace: { body: workspaceBody, updatedAt: null, version: 1 },
          }),
        );
        return;
      }
      res.writeHead(404).end();
    });
    server.listen(0, "127.0.0.1", () => {
      const port = (server.address() as { port: number }).port;
      resolve({ port, close: () => new Promise((r) => server.close(() => r())) });
    });
  });
}

function setUpIsolatedRegistry(baseUrl: string): { home: string; projectDir: string } {
  const home = mkdtempSync(join(tmpdir(), "petbox-hook-proc-"));
  const projectDir = join(home, "fake-project");
  mkdirSync(projectDir, { recursive: true });
  mkdirSync(join(home, ".petbox"), { recursive: true });
  writeFileSync(
    join(home, ".petbox", "projects.json"),
    JSON.stringify({
      entries: [{ prefix: projectDir, project: "fake-project", envVar: "FAKE_HOOK_TEST_KEY", baseUrl }],
    }),
  );
  return { home, projectDir };
}

test("pull-memory.ts as a real process: wall clock stays well under budget against a fast server", async () => {
  const { close, port } = await startFastFakeServer();
  const { home, projectDir } = setUpIsolatedRegistry(`http://127.0.0.1:${port}`);
  try {
    const env: NodeJS.ProcessEnv = {
      ...process.env,
      HOME: home,
      USERPROFILE: home, // os.homedir() reads USERPROFILE on win32, HOME on POSIX
      FAKE_HOOK_TEST_KEY: "fake-key-for-test",
    };
    const input = JSON.stringify({
      session_id: "test",
      cwd: projectDir,
      hook_event_name: "SessionStart",
      source: "startup",
    });

    const result = await runHook(join(HERE, "pull-memory.ts"), input, env, projectDir);

    assert.equal(result.code, 0, `expected exit 0, got ${result.code}. stderr: ${result.stderr}`);
    assert.equal(result.stderr, "", "hook must never write to stderr on the happy path");
    assert.ok(result.stdout.length > 0, "hook must print the banner");
    assert.ok(
      result.wallMs < WALL_CLOCK_BUDGET_MS,
      `wall clock ${result.wallMs}ms exceeded the ${WALL_CLOCK_BUDGET_MS}ms budget against a server that never stalls — ` +
        `this is the regression this test exists to catch (an uncleared timer / late-unreffed handle holding the loop open)`,
    );
  } finally {
    await close();
    rmSync(home, { recursive: true, force: true });
  }
});

// Pins the real Claude Code SessionStart payload shape, captured live (2026-08-28, CC on
// Windows via `claude -p`): {session_id, transcript_path, cwd, hook_event_name, source}. There
// was suspicion the hook reads the wrong key (a "matcher" field was floated as the real name,
// confused with the unrelated hook-registration matcher-pattern table in the docs) — if that
// were true, `HookInput.source` would silently stay `undefined` on every real session and the
// code's `let source = "startup"` default would mask it completely: no error, no log, just a
// permanently-skipped resume/compact nudge. Asserting on the nudge text (which only appears
// when `source` parses to "resume"/"compact" — see protocol.ts) turns that silent divergence
// into a hard test failure instead of an invisible one.
test("pull-memory.ts as a real process: a real-shaped payload with source:\"resume\" parses `source` and appends the resume nudge", async () => {
  const { close, port } = await startFastFakeServer();
  const { home, projectDir } = setUpIsolatedRegistry(`http://127.0.0.1:${port}`);
  try {
    const env: NodeJS.ProcessEnv = {
      ...process.env,
      HOME: home,
      USERPROFILE: home,
      FAKE_HOOK_TEST_KEY: "fake-key-for-test",
    };
    // Mirrors the exact field set captured from a live harness invocation, not just the
    // fields this hook happens to read.
    const input = JSON.stringify({
      session_id: "6087d175-023a-49af-8410-fb6778e3bd82",
      transcript_path: "C:\\Users\\stdray\\.claude\\projects\\fake\\6087d175.jsonl",
      cwd: projectDir,
      hook_event_name: "SessionStart",
      source: "resume",
    });

    const result = await runHook(join(HERE, "pull-memory.ts"), input, env, projectDir);

    assert.equal(result.code, 0, `expected exit 0, got ${result.code}. stderr: ${result.stderr}`);
    assert.match(
      result.stdout,
      /Session resume — also recall recent session\/decision memories/,
      `source:"resume" must produce the resume nudge; if this fails, the hook is not reading ` +
        `the real payload's \`source\` field — got stdout: ${JSON.stringify(result.stdout.slice(0, 500))}`,
    );
  } finally {
    await close();
    rmSync(home, { recursive: true, force: true });
  }
});

test("pull-memory.ts as a real process: an oversized canon is dropped, logged loudly (stderr + wire.log), and stdout never risks the harness's own truncation", async () => {
  const { close, port } = await startFatCanonFakeServer();
  const { home, projectDir } = setUpIsolatedRegistry(`http://127.0.0.1:${port}`);
  try {
    const env: NodeJS.ProcessEnv = {
      ...process.env,
      HOME: home,
      USERPROFILE: home,
      FAKE_HOOK_TEST_KEY: "fake-key-for-test",
    };
    const input = JSON.stringify({
      session_id: "test",
      cwd: projectDir,
      hook_event_name: "SessionStart",
      source: "startup",
    });

    const result = await runHook(join(HERE, "pull-memory.ts"), input, env, projectDir);

    assert.equal(result.code, 0, `hook must still exit 0 on a budget overage (best-effort contract), got ${result.code}`);
    assert.ok(result.stdout.length > 0, "the mandatory protocol block must still ship");
    assert.ok(
      Buffer.byteLength(result.stdout, "utf8") <= HARNESS_INLINE_HARD_LIMIT_BYTES,
      `stdout is ${Buffer.byteLength(result.stdout, "utf8")}B — at/over the harness's own ` +
        `${HARNESS_INLINE_HARD_LIMIT_BYTES}B hard limit, meaning the harness would itself have ` +
        `truncated it (the exact bug this test exists to catch)`,
    );
    assert.ok(!result.stdout.includes("canon-line "), "the oversized canon must NOT have been inlined");
    assert.match(
      result.stderr,
      /exceeded budget/,
      `an over-budget banner must complain loudly to stderr; got: ${JSON.stringify(result.stderr)}`,
    );

    const logPath = join(home, ".petbox", "wire.log");
    const logContent = readFileSync(logPath, "utf8");
    assert.match(
      logContent,
      /exceeded budget/,
      "the overage must also leave a durable trace in ~/.petbox/wire.log, not just stderr",
    );
  } finally {
    await close();
    rmSync(home, { recursive: true, force: true });
  }
});

// The owner-only-skills block (work: user-invocable-skills-invisible-to-model) is wired through
// this same real-process path, not just exercised in-process against the pure function — the
// exact gap opencode-plugin-system-transform.test.ts's header describes for the sibling index
// ("shipped green tests and a feature that never reached the model").
function materializeOwnerOnlySkill(projectDir: string): void {
  const skillDir = join(projectDir, ".claude", "skills", "petbox-factory-run");
  mkdirSync(skillDir, { recursive: true });
  writeFileSync(
    join(skillDir, "SKILL.md"),
    [
      "---",
      "name: petbox-factory-run",
      "description: Fan tasks out to workers. Use for an unattended multi-task pass.",
      "disable-model-invocation: true",
      "---",
      "",
      "# Factory run",
      "",
    ].join("\n"),
    "utf8",
  );
}

test("pull-memory.ts as a real process: a materialized owner-only skill reaches stdout, with the Claude-Code fact", async () => {
  const { close, port } = await startFastFakeServer();
  const { home, projectDir } = setUpIsolatedRegistry(`http://127.0.0.1:${port}`);
  materializeOwnerOnlySkill(projectDir);
  try {
    const env: NodeJS.ProcessEnv = {
      ...process.env,
      HOME: home,
      USERPROFILE: home,
      FAKE_HOOK_TEST_KEY: "fake-key-for-test",
    };
    const input = JSON.stringify({ session_id: "test", cwd: projectDir, hook_event_name: "SessionStart", source: "startup" });

    const result = await runHook(join(HERE, "pull-memory.ts"), input, env, projectDir);

    assert.equal(result.code, 0, `expected exit 0, got ${result.code}. stderr: ${result.stderr}`);
    assert.ok(result.stdout.includes("`petbox-factory-run`"), `owner-only block must reach stdout:\n${result.stdout}`);
    assert.ok(
      result.stdout.includes("removes these from your own listing entirely"),
      "Claude Code must get the hidden-entirely fact",
    );
    assert.ok(!result.stdout.includes("does not recognize the key"), "must not carry opencode's fact");
  } finally {
    await close();
    rmSync(home, { recursive: true, force: true });
  }
});

// THE regression this hook exists to close (bug: owner-only-skills-block-silently-dropped-in-
// real-project): the block's own unit tests were green against a small canon fixture, and the
// feature never reached the agent in the one real project it was for. This drives the REAL hook
// process, against a REAL-SCALE canon (measured $system bytes, not round numbers — see
// startRealSystemScaleCanonFakeServer's comment), in "compact" source mode (the longer protocol
// variant) — the same class of end-to-end check opencode-plugin-system-transform.test.ts's header
// describes for the sibling salience index, applied here to the ladder fix in session-budget.ts.
test("pull-memory.ts as a real process: at REAL $system scale (4KB canon, compact mode), the owner-only-skills block still reaches stdout — the workspace canon leg sheds instead", async () => {
  const { close, port } = await startRealSystemScaleCanonFakeServer();
  const { home, projectDir } = setUpIsolatedRegistry(`http://127.0.0.1:${port}`);
  materializeOwnerOnlySkill(projectDir);
  try {
    const env: NodeJS.ProcessEnv = {
      ...process.env,
      HOME: home,
      USERPROFILE: home,
      FAKE_HOOK_TEST_KEY: "fake-key-for-test",
    };
    const input = JSON.stringify({ session_id: "test", cwd: projectDir, hook_event_name: "SessionStart", source: "compact" });

    const result = await runHook(join(HERE, "pull-memory.ts"), input, env, projectDir);

    assert.equal(result.code, 0, `expected exit 0, got ${result.code}. stderr: ${result.stderr}`);
    assert.ok(
      result.stdout.includes("`petbox-factory-run`"),
      `THE regression: the owner-only-skills block must survive at real $system scale:\n${result.stdout.length} bytes`,
    );
    assert.ok(result.stdout.includes("### Project ("), "the canon project leg must still be present");
    assert.ok(!result.stdout.includes("### Workspace"), "the canon WORKSPACE leg — not the block — must be what paid for the room");
    assert.ok(
      Buffer.byteLength(result.stdout, "utf8") <= HARNESS_INLINE_HARD_LIMIT_BYTES,
      `stdout is ${Buffer.byteLength(result.stdout, "utf8")}B — at/over the harness's own ${HARNESS_INLINE_HARD_LIMIT_BYTES}B hard limit`,
    );
  } finally {
    await close();
    rmSync(home, { recursive: true, force: true });
  }
});

test("droid-pull-memory.ts as a real process: a materialized owner-only skill reaches the context, with the Claude-Code/Droid fact", async () => {
  const { close, port } = await startFastFakeServer();
  const { home, projectDir } = setUpIsolatedRegistry(`http://127.0.0.1:${port}`);
  materializeOwnerOnlySkill(projectDir);
  try {
    const env: NodeJS.ProcessEnv = {
      ...process.env,
      HOME: home,
      USERPROFILE: home,
      FAKE_HOOK_TEST_KEY: "fake-key-for-test",
    };
    const input = JSON.stringify({ session_id: "test", cwd: projectDir, hook_event_name: "SessionStart", source: "startup" });

    const result = await runHook(join(HERE, "droid-pull-memory.ts"), input, env, projectDir);

    assert.equal(result.code, 0, `expected exit 0, got ${result.code}. stderr: ${result.stderr}`);
    const out = JSON.parse(result.stdout);
    const context: string = out.hookSpecificOutput.additionalContext;
    assert.ok(context.includes("`petbox-factory-run`"), `owner-only block must reach the context:\n${context}`);
    assert.ok(context.includes("removes these from your own listing entirely"), "Droid must get the hidden-entirely fact");
  } finally {
    await close();
    rmSync(home, { recursive: true, force: true });
  }
});

test("droid-pull-memory.ts as a real process: wall clock stays well under budget against a fast server", async () => {
  const { close, port } = await startFastFakeServer();
  const { home, projectDir } = setUpIsolatedRegistry(`http://127.0.0.1:${port}`);
  try {
    const env: NodeJS.ProcessEnv = {
      ...process.env,
      HOME: home,
      USERPROFILE: home,
      FAKE_HOOK_TEST_KEY: "fake-key-for-test",
    };
    const input = JSON.stringify({
      session_id: "test",
      cwd: projectDir,
      hook_event_name: "SessionStart",
      source: "startup",
    });

    const result = await runHook(join(HERE, "droid-pull-memory.ts"), input, env, projectDir);

    assert.equal(result.code, 0, `expected exit 0, got ${result.code}. stderr: ${result.stderr}`);
    assert.equal(result.stderr, "", "hook must never write to stderr on the happy path");
    assert.ok(result.stdout.length > 0, "hook must print the banner");
    assert.ok(
      result.wallMs < WALL_CLOCK_BUDGET_MS,
      `wall clock ${result.wallMs}ms exceeded the ${WALL_CLOCK_BUDGET_MS}ms budget against a server that never stalls`,
    );
  } finally {
    await close();
    rmSync(home, { recursive: true, force: true });
  }
});
