// Regression tests for the second half of probe-collapses-http-errors-into-network: `apply`
// (performApply, wire.ts) used to print its bare "done — all known harnesses accepted every
// role." trailing line even when the skills refresh step was silently skipped because the
// workspace probe failed — the SAME class of defect already fixed once for self-smoke
// (selfsmoke-failure-prints-done): a partial run reads as a complete one because the LAST line a
// human sees never says otherwise.
//
// UPDATED by wire-exit-incomplete-is-invisible-to-automation. The original card deliberately
// froze the exit code and fixed only the TEXT; the owner then lifted that boundary, because a
// truthful message no automation reads is still invisible ("частичный apply неотличим от полного
// по коду возврата"). So an UNINTENTIONAL skip now also exits WIRE_EXIT.incomplete (4) — a
// BREAKING change to a published contract, asserted here rather than discovered in someone's CI.
//
// Unchanged, and re-asserted below because it is what keeps code 4 worth listening to:
// `--offline` and "directory not registered" are INTENTIONAL skips (the user asked for them) and
// still exit 0 with the full "done —" line. Only a probe FAILURE is unintentional.
//
// wire.ts runs main() at module top level (see its own file header), so `apply` can only be
// exercised as a real subprocess — same spawn-based technique as apply-exit-race-libuv.test.ts /
// doctor-skill-drift.test.ts. spawn (async), not spawnSync, for the online case so this process's
// event loop stays free to answer the fake server (see those files' comments on the self-deadlock
// spawnSync causes here).
//
// Run: node --test src/apply-skills-skip.test.ts

import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { WIRE_EXIT } from "./wire-exit.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

// Minimal shape-valid single-role definition — no requiredCapabilities, so every harness passes
// the truthfulness gate trivially and the ONLY thing under test is the skills-skip bookkeeping,
// never an unrelated policy block.
const DEF_RECORD = {
  key: "default",
  version: 1,
  definition: {
    name: "apply-skills-skip-test-def",
    roles: [{ slug: "worker", tier: "worker", requiredCapabilities: [] }],
  },
};

// A definition whose second role requires a capability opencode does NOT declare
// (dynamic_model_at_spawn — see harness-capabilities.ts), so the truthfulness gate blocks that
// harness while claude-code/droid still write. Used by the priority test below.
const DEF_RECORD_TRUTHFULNESS_BLOCK = {
  key: "default",
  version: 1,
  definition: {
    name: "apply-skills-skip-truthfulness-def",
    roles: [
      { slug: "worker", tier: "worker", requiredCapabilities: [] },
      { slug: "orchestrator", tier: "orchestrator", requiredCapabilities: ["dynamic_model_at_spawn"] },
    ],
  },
};

function startFakeServer(
  validateHandler: (req: import("node:http").IncomingMessage, res: import("node:http").ServerResponse) => void,
  defRecord: unknown = DEF_RECORD,
): Promise<{
  baseUrl: string;
  close: () => Promise<void>;
}> {
  return new Promise((resolve) => {
    const server = createServer((req, res) => {
      const url = req.url ?? "";
      if (url.includes("/agent-defs/")) {
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify(defRecord));
        return;
      }
      if (url.includes("/api/auth/validate")) {
        validateHandler(req, res);
        return;
      }
      res.writeHead(404, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ error: "not found" }));
    });
    server.listen(0, "127.0.0.1", () => {
      const { port } = server.address() as AddressInfo;
      resolve({
        baseUrl: `http://127.0.0.1:${port}`,
        close: () => new Promise((r) => server.close(() => r())),
      });
    });
  });
}

function writeOnlineRegistry(homeDir: string, projectDir: string, project: string, baseUrl: string): void {
  const petboxDir = join(homeDir, ".petbox");
  mkdirSync(petboxDir, { recursive: true });
  const envVar = "PETBOX_APPLY_SKILLS_SKIP_TEST_API_KEY";
  writeFileSync(
    join(petboxDir, "projects.json"),
    JSON.stringify({ entries: [{ prefix: projectDir, project, envVar, baseUrl }] }, null, 2),
    "utf8",
  );
  writeFileSync(join(petboxDir, "keys.json"), JSON.stringify({ [envVar]: "fake-key-value" }, null, 2), "utf8");
}

function runApplyOnline(cwd: string, homeDir: string): Promise<{ stdout: string; stderr: string; status: number | null }> {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [WIRE_TS, "apply"], {
      cwd,
      env: {
        ...process.env,
        USERPROFILE: homeDir,
        HOME: homeDir,
        HOMEDRIVE: undefined,
        HOMEPATH: undefined,
      },
    });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (d) => (stdout += d.toString("utf8")));
    child.stderr.on("data", (d) => (stderr += d.toString("utf8")));
    child.on("error", reject);
    child.on("close", (status) => resolve({ stdout, stderr, status }));
  });
}

function runApplyOffline(cwd: string, homeDir: string): { stdout: string; stderr: string; status: number | null } {
  const res = spawnSync(process.execPath, [WIRE_TS, "apply", "--offline"], {
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
  return { stdout: res.stdout ?? "", stderr: res.stderr ?? "", status: res.status };
}

test("apply (online, workspace probe hits HTTP 500): UNINTENTIONAL skills skip — exits 4 (incomplete), names the skip, and summary carries it", async () => {
  const homeDir = freshDir("petbox-apply-skip-home-");
  const projectDir = freshDir("petbox-apply-skip-proj-");
  const fake = await startFakeServer((_req, res) => {
    res.writeHead(500, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ error: "internal" }));
  });
  try {
    writeOnlineRegistry(homeDir, projectDir, "apply-skip-500-proj", fake.baseUrl);

    const { stdout, stderr, status } = await runApplyOnline(projectDir, homeDir);
    const out = stdout + stderr;

    // THE breaking change: this used to be 0, which is why a CI step could not see a partial run.
    assert.equal(
      status,
      WIRE_EXIT.incomplete,
      `an unintentional skip must now be visible in the exit code, not only in stdout. Full output:\n${out}`,
    );
    assert.notEqual(status, 127, `must never surface as 127 (the libuv-race symptom). Full output:\n${out}`);

    // The old lie: this exact bare line must NOT be the trailing message when the skip was
    // unintentional.
    assert.doesNotMatch(
      out,
      /apply: done — all known harnesses accepted every role\.\s*$/m,
      `must not claim unqualified full success when skills were silently skipped. Full output:\n${out}`,
    );
    assert.match(out, /INCOMPLETE/, `trailing line must name the run as incomplete. Full output:\n${out}`);
    assert.match(out, /skills were NOT refreshed/i, `must name WHAT was skipped. Full output:\n${out}`);
    assert.match(out, /HTTP 500/, `must name WHY (the actual status code, not a generic network claim). Full output:\n${out}`);
    assert.doesNotMatch(out, /could not reach/i, `an HTTP 500 must never be described as unreachable. Full output:\n${out}`);

    // Structured summary must carry the fact machine-readably.
    assert.match(
      out,
      /"skillsSkipped":\{"intentional":false,"reason":"[^"]*HTTP 500[^"]*"\}/,
      `summary must carry an unintentional skip with the HTTP-500 reason. Full output:\n${out}`,
    );
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("apply --offline: skills skip is INTENTIONAL — unchanged behavior, still a full 'done' line, summary marks intentional:true", () => {
  const homeDir = freshDir("petbox-apply-skip-home-");
  const projectDir = freshDir("petbox-apply-skip-proj-");
  try {
    const { stdout, stderr, status } = runApplyOffline(projectDir, homeDir);
    const out = stdout + stderr;

    assert.equal(status, WIRE_EXIT.ok, `Full output:\n${out}`);
    // --offline is a deliberate, user-requested skip — the pre-existing full-success line must
    // still print unchanged.
    assert.match(
      out,
      /apply: done — all known harnesses accepted every role\./,
      `--offline must NOT be treated as an unintentional skip. Full output:\n${out}`,
    );
    assert.doesNotMatch(out, /INCOMPLETE/, `--offline is intentional, never reported as incomplete. Full output:\n${out}`);
    // Unchanged behavior: a fully successful run (no truthfulness block, no clobber, and now no
    // UNINTENDED skip) never printed the structured summary JSON before this fix either — only
    // the failure/incomplete branches do. --offline is the intentional-skip carve-out, so this
    // path must stay exactly as quiet as it always was; asserting silence here is the regression
    // guard against this fix's `unintendedSkillsSkip` branch over-firing on an intentional skip.
    assert.doesNotMatch(out, /"skillsSkipped"/, `an intentional --offline skip must not flip the run onto the incomplete/summary-printing path. Full output:\n${out}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

// ---- priority when several conditions hold at once -------------------------
//
// wire-exit.test.ts pins the ORDER in the pure classifier; these two prove the wiring end-to-end,
// which is what the card asked for ("реши явно, что важнее, и ЗАКРЕПИ ТЕСТОМ", not "leave it to
// the order of the if-statements"). Both scenarios have an unintentional skills skip AND a
// stronger condition; the stronger one must own the exit code, and the skip must still be
// reported inside the summary rather than disappearing.

test("PRIORITY: clobber refusal (1) outranks incomplete (4) — and the skip is still named in summary", async () => {
  const homeDir = freshDir("petbox-apply-prio-home-");
  const projectDir = freshDir("petbox-apply-prio-proj-");
  const fake = await startFakeServer((_req, res) => {
    res.writeHead(500, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ error: "internal" }));
  });
  try {
    writeOnlineRegistry(homeDir, projectDir, "apply-prio-clobber-proj", fake.baseUrl);

    // A real, non-PetBox file exactly where the sole role's claude-code artifact must go →
    // writeArtifact refuses → clobberBlocked. The 500 above independently causes the
    // unintentional skills skip, so BOTH conditions hold on this one run.
    mkdirSync(join(projectDir, ".claude", "agents"), { recursive: true });
    writeFileSync(
      join(projectDir, ".claude", "agents", "petbox-worker.md"),
      "# not a petbox file\nsome real content the owner wrote by hand\n",
      "utf8",
    );

    const { stdout, stderr, status } = await runApplyOnline(projectDir, homeDir);
    const out = stdout + stderr;

    assert.equal(
      status,
      WIRE_EXIT.hard,
      `a refused write must outrank an incomplete run (expected ${WIRE_EXIT.hard}, not ` +
        `${WIRE_EXIT.incomplete}). Full output:\n${out}`,
    );
    assert.match(out, /hard failure/, `Full output:\n${out}`);
    // The weaker fact must not vanish just because it lost the exit code.
    assert.match(
      out,
      /"skillsSkipped":\{"intentional":false,/,
      `the losing condition must still be reported in the summary. Full output:\n${out}`,
    );
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("PRIORITY: truthfulness block (3) outranks incomplete (4) — and the skip is still named in summary", async () => {
  const homeDir = freshDir("petbox-apply-prio-home-");
  const projectDir = freshDir("petbox-apply-prio-proj-");
  const fake = await startFakeServer(
    (_req, res) => {
      res.writeHead(500, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ error: "internal" }));
    },
    DEF_RECORD_TRUTHFULNESS_BLOCK,
  );
  try {
    writeOnlineRegistry(homeDir, projectDir, "apply-prio-truth-proj", fake.baseUrl);

    const { stdout, stderr, status } = await runApplyOnline(projectDir, homeDir);
    const out = stdout + stderr;

    assert.equal(
      status,
      WIRE_EXIT.truthfulness,
      `a policy block must outrank an incomplete run (expected ${WIRE_EXIT.truthfulness}, not ` +
        `${WIRE_EXIT.incomplete}). Full output:\n${out}`,
    );
    assert.match(out, /truthfulness partial/, `Full output:\n${out}`);
    assert.match(
      out,
      /"skillsSkipped":\{"intentional":false,/,
      `the losing condition must still be reported in the summary. Full output:\n${out}`,
    );
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});
