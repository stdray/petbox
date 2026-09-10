// Regression coverage for commit a5618dbe (fix(wire): surface UnresolvedEnvRefError to the
// owner instead of swallowing it in hook best-effort catches).
//
// GAP THIS CLOSES: registry.ts's resolveProject() throws UnresolvedEnvRefError when a
// keys.json entry is an unresolved $VAR/${VAR} reference (card
// keys-json-supports-env-var-references, decision 2). a5618dbe taught the 9 global hooks that
// call resolveProject() to special-case that one exception instead of folding it into their
// pre-existing blanket best-effort `catch {}` — but nothing exercised the HOOKS themselves:
// `grep UnresolvedEnvRefError src/` turns up 14 files, and the only two *.test.ts among them
// (registry.test.ts, posix-env.test.ts) test registry.ts/posix-env.ts, never a hook. So the
// owner-visible behavior a5618dbe shipped — "⚠ <message>" landing in the SAME channel as the
// hook's normal banner, plus stderr, for SessionStart-shaped hooks; stderr only for
// Stop/SessionEnd-shaped hooks; every OTHER exception still silent/best-effort — had zero test
// coverage. This file drives each hook as a REAL OS process (the idiom pull-memory.test.ts and
// broken-layer-loudness.test.ts already use), the same way Claude Code / Codex / Droid / Qwen
// actually invoke them, because "which channel" and "exit 0" are process-level claims an
// in-process unit test cannot make. opencode-plugin.ts is covered separately
// (hooks-unresolved-env-ref-opencode.test.ts) since it is loaded in-process, not spawned.
//
// Run: node --test src/hooks-unresolved-env-ref.test.ts

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

const HERE = import.meta.dirname;

type SpawnResult = { code: number | null; stdout: string; stderr: string };

function runHook(script: string, input: string, env: NodeJS.ProcessEnv, cwd: string): Promise<SpawnResult> {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [join(HERE, script)], { env, cwd });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (c) => (stdout += c));
    child.stderr.on("data", (c) => (stderr += c));
    child.on("error", reject);
    child.on("close", (code) => resolve({ code, stdout, stderr }));
    child.stdin.write(input);
    child.stdin.end();
  });
}

// A registry + keys.json pair that reproduces the EXACT scenario decision 1/2 create: the
// project is wired (an entry in projects.json), the env var itself is NOT set in this process,
// and keys.json holds a reference to a SECOND var that is *also* not set — so
// readKeyStore()/resolveProject() throws UnresolvedEnvRefError rather than returning "" or null.
function setUpUnresolvedRefRegistry(): {
  home: string;
  projectDir: string;
  envVar: string;
  refVar: string;
  project: string;
} {
  const home = mkdtempSync(join(tmpdir(), "petbox-envref-home-"));
  const projectDir = join(home, "fake-project");
  mkdirSync(projectDir, { recursive: true });
  mkdirSync(join(home, ".petbox"), { recursive: true });
  const envVar = "FAKE_UNRESOLVED_ENVREF_TEST_KEY";
  const refVar = "FAKE_UNRESOLVED_ENVREF_TEST_TARGET"; // deliberately never set anywhere
  const project = "fake-unresolved-project";
  writeFileSync(
    join(home, ".petbox", "projects.json"),
    JSON.stringify({ entries: [{ prefix: projectDir, project, envVar, baseUrl: "http://127.0.0.1:1" }] }),
  );
  writeFileSync(join(home, ".petbox", "keys.json"), JSON.stringify({ [envVar]: `$${refVar}` }));
  return { home, projectDir, envVar, refVar, project };
}

// Env for a spawned hook: isolated HOME, and — critically — WITHOUT envVar/refVar, so the
// reference genuinely fails to resolve (mirrors setUpUnresolvedRefRegistry's contract; also
// strips them if they happen to leak in from the outer shell).
function envWithoutRefVars(home: string, envVar: string, refVar: string): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = { ...process.env, HOME: home, USERPROFILE: home };
  delete env[envVar];
  delete env[refVar];
  return env;
}

const SESSION_ID = "test-session";

// ---- SessionStart family: pull-memory + codex/droid/qwen ports --------------------------------
// Contract (a5618dbe): "⚠ <message>" on the SAME channel the hook's normal banner uses, PLUS
// stderr. pull-memory.ts's channel is raw stdout; the other three wrap it in the
// `{ hookSpecificOutput: { hookEventName: "SessionStart", additionalContext } }` JSON envelope.
const SESSION_START_HOOKS = [
  { script: "pull-memory.ts", channel: "raw" as const },
  { script: "codex-pull-memory.ts", channel: "json" as const },
  { script: "droid-pull-memory.ts", channel: "json" as const },
  { script: "qwen-pull-memory.ts", channel: "json" as const },
];

function extractInSessionChannel(script: string, channel: "raw" | "json", stdout: string): string {
  if (channel === "raw") return stdout;
  assert.ok(stdout.trim().length > 0, `${script}: expected a JSON envelope on stdout, got nothing`);
  const parsed = JSON.parse(stdout) as { hookSpecificOutput?: { additionalContext?: string } };
  const ctx = parsed.hookSpecificOutput?.additionalContext;
  assert.equal(typeof ctx, "string", `${script}: hookSpecificOutput.additionalContext must be a string. stdout: ${stdout}`);
  return ctx as string;
}

for (const { script, channel } of SESSION_START_HOOKS) {
  test(`${script}: an unresolved keys.json $VAR reference is surfaced in-session (naming the var + project) AND on stderr, exit 0`, async () => {
    const { home, projectDir, envVar, refVar, project } = setUpUnresolvedRefRegistry();
    try {
      const env = envWithoutRefVars(home, envVar, refVar);
      const input = JSON.stringify({ session_id: SESSION_ID, cwd: projectDir, hook_event_name: "SessionStart", source: "startup" });

      const result = await runHook(script, input, env, projectDir);

      assert.equal(result.code, 0, `a SessionStart hook must never fail the session on this error. stderr:\n${result.stderr}`);

      const inSession = extractInSessionChannel(script, channel, result.stdout);
      assert.ok(inSession.startsWith("⚠") || inSession.includes("⚠"), `${script}: the in-session note must carry the ⚠ marker. Got: ${inSession}`);
      // The whole point of the fix: name the VARIABLE and the PROJECT, not a generic warning.
      assert.ok(inSession.includes(envVar), `${script}: in-session note must name the env var "${envVar}". Got: ${inSession}`);
      assert.ok(inSession.includes(refVar), `${script}: in-session note must name the referenced var "${refVar}". Got: ${inSession}`);
      assert.ok(inSession.includes(project), `${script}: in-session note must name the project "${project}". Got: ${inSession}`);

      assert.ok(result.stderr.length > 0, `${script}: must ALSO mirror the note to stderr, per the fix's contract`);
      assert.ok(result.stderr.includes(envVar), `${script}: stderr must name the env var. Got: ${result.stderr}`);
      assert.ok(result.stderr.includes(project), `${script}: stderr must name the project. Got: ${result.stderr}`);
    } finally {
      rmSync(home, { recursive: true, force: true });
    }
  });
}

// ---- Regression guard: this must NOT become "warn on anything" --------------------------------
// A test that only exercises the happy-path warning would not notice a reversion to a blanket
// `catch {}` that swallows UnresolvedEnvRefError too (the exact bug a5618dbe fixed) NOR an
// over-correction that starts warning on ORDINARY conditions (e.g. "project simply not
// registered"). This pins the untouched, pre-existing silent case: an unregistered cwd must stay
// a true no-op — no stdout, no stderr, exit 0 — exactly as before a5618dbe.
for (const { script } of SESSION_START_HOOKS) {
  test(`${script}: an ORDINARY unregistered project stays a silent no-op (unaffected by the UnresolvedEnvRefError special-case)`, async () => {
    const home = mkdtempSync(join(tmpdir(), "petbox-envref-noreg-home-"));
    const projectDir = join(home, "unregistered-project");
    mkdirSync(projectDir, { recursive: true });
    try {
      // No ~/.petbox/projects.json at all — the ordinary "never wired" case (Class A).
      const env = { ...process.env, HOME: home, USERPROFILE: home };
      const input = JSON.stringify({ session_id: SESSION_ID, cwd: projectDir, hook_event_name: "SessionStart", source: "startup" });

      const result = await runHook(script, input, env, projectDir);

      assert.equal(result.code, 0, `stderr:\n${result.stderr}`);
      assert.equal(result.stdout, "", `${script}: an unregistered project must produce NO output, not a warning`);
      assert.equal(result.stderr, "", `${script}: an unregistered project must stay silent on stderr too`);
    } finally {
      rmSync(home, { recursive: true, force: true });
    }
  });
}

// ---- Stop/SessionEnd family: push-session + codex/droid/qwen ports ----------------------------
// Contract (a5618dbe): these hooks have no in-session channel left by the time they run (the
// session already ended) — stderr only, still exit 0, still best-effort for everything else.
const STOP_HOOKS = ["push-session.ts", "codex-push-session.ts", "droid-push-session.ts", "qwen-push-session.ts"];

for (const script of STOP_HOOKS) {
  test(`${script}: an unresolved keys.json $VAR reference is surfaced on stderr ONLY (naming the var + project), exit 0`, async () => {
    const { home, projectDir, envVar, refVar, project } = setUpUnresolvedRefRegistry();
    try {
      const env = envWithoutRefVars(home, envVar, refVar);
      const input = JSON.stringify({ session_id: SESSION_ID, transcript_path: join(home, "missing.jsonl"), cwd: projectDir });

      const result = await runHook(script, input, env, projectDir);

      assert.equal(result.code, 0, `a Stop hook must never fail the session on this error. stderr:\n${result.stderr}`);
      // No in-session channel exists for this family — stdout must stay empty.
      assert.equal(result.stdout, "", `${script}: a Stop hook has no in-session channel; stdout must stay empty. Got: ${result.stdout}`);
      assert.ok(result.stderr.length > 0, `${script}: must surface the note on stderr`);
      assert.ok(result.stderr.includes(envVar), `${script}: stderr must name the env var "${envVar}". Got: ${result.stderr}`);
      assert.ok(result.stderr.includes(refVar), `${script}: stderr must name the referenced var "${refVar}". Got: ${result.stderr}`);
      assert.ok(result.stderr.includes(project), `${script}: stderr must name the project "${project}". Got: ${result.stderr}`);
    } finally {
      rmSync(home, { recursive: true, force: true });
    }
  });

  test(`${script}: an ORDINARY unregistered project stays a silent no-op (unaffected by the UnresolvedEnvRefError special-case)`, async () => {
    const home = mkdtempSync(join(tmpdir(), "petbox-envref-noreg-home-"));
    const projectDir = join(home, "unregistered-project");
    mkdirSync(projectDir, { recursive: true });
    try {
      const env = { ...process.env, HOME: home, USERPROFILE: home };
      const input = JSON.stringify({ session_id: SESSION_ID, transcript_path: join(home, "missing.jsonl"), cwd: projectDir });

      const result = await runHook(script, input, env, projectDir);

      assert.equal(result.code, 0, `stderr:\n${result.stderr}`);
      assert.equal(result.stdout, "", `${script}: an unregistered project must produce NO stdout`);
      assert.equal(result.stderr, "", `${script}: an unregistered project must stay silent on stderr too`);
    } finally {
      rmSync(home, { recursive: true, force: true });
    }
  });
}
