// Behavioral proof for full-wire-exit-ignores-step-11: the FULL `wire` command's own exit code
// must reflect step 11 (seed role bindings + apply), the step that compiles the per-harness agent
// artifacts.
//
// THE BUG. Step 11 was deliberately non-aborting — the key is validated and every other file is
// written by the time it runs, so a compile hiccup must not throw that away
// (fresh-wire-roster-unusable). But "does not abort" had been implemented as "does not count":
// `performApply`'s code was printed and DROPPED. A run whose agent artifacts were never written
// exited 0, which made doc/agent-wiring.md §2b's "`0` — every requested step ran" true for `apply`
// and FALSE for `wire`, and re-opened the exact bug step 11 exists to close — a script wiring a
// fleet could not tell a fully wired machine from one with no agent roster at all.
//
// THE TWO HALVES, and why both are asserted here:
//   1. the code now propagates (this is the fix), AND
//   2. the run still is not interrupted (this is the decision the fix must NOT break).
// Half 2 is the important one. It is easy to "fix" half 1 by exiting at step 11, which would
// silently undo fresh-wire-roster-unusable; the assertions below therefore check that everything
// step 11 does after its first failure still happened, and that the run reached its own end.
//
// Step 10's self-smoke is the precedent this fix copies (`process.exitCode = 1`, keep going), so
// the two can now fail in the SAME run — and the reported code must be the STRONGEST by the
// declared priority (1 > 3 > 4 > 0, wire-exit.ts's strongestExitCode), never "whoever assigned
// last". Step 11 assigns last, so a bare assignment would report 4 over step 10's 1.
//
// Seam: PETBOX_WIRE_TEST_LOOPBACK_BASE_URL, the same http-loopback-only override
// wire-full-exit-races.test.ts uses (see its header for why the full-wire path is otherwise
// untestable, and for the guard rails on that seam). spawn (async), not spawnSync: this process
// must stay free to answer the child's requests.
//
// Run: node --test src/wire-full-exit-step11.test.ts

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { WIRE_EXIT } from "./wire-exit.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");
const PROJECT = "wire-step11-exit-proj";

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

type Fake = { baseUrl: string; close: () => Promise<void> };

// The one server every scenario uses. Two knobs, each aimed at one failure the run must report:
//   smokeOk        — step 10's self-smoke round trip (a bad body ⇒ exit 1 from that step)
//   failProbeAfter — how many /api/auth/validate calls succeed before the rest return 500. Step 3
//                    takes the first one; apply's workspace probe (which gates the skills refresh)
//                    takes the second, and its failure is an UNINTENDED skip ⇒ step 11 exits 4.
function startFakeServer(opts: {
  readonly smokeOk: boolean;
  readonly failProbeAfter?: number;
}): Promise<Fake> {
  let validateCalls = 0;
  const handler = (req: IncomingMessage, res: ServerResponse): void => {
    const url = req.url ?? "";
    if (url.includes("/api/auth/validate")) {
      validateCalls++;
      if (opts.failProbeAfter !== undefined && validateCalls > opts.failProbeAfter) {
        res.writeHead(500, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ error: "probe unavailable" }));
        return;
      }
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ project: PROJECT, workspace: "step11-ws" }));
      return;
    }
    if (url.includes("wire-smoke")) {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(
        opts.smokeOk
          ? JSON.stringify({ sessionId: "s1", version: 1, messageCount: 1 })
          : "not a json body at all",
      );
      return;
    }
    // Everything else (the agent-defs fetch) 404s: resolveApplyDefinition falls back to the
    // built-in default, which is the fresh-machine path this step is about.
    res.writeHead(404, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ error: "not found" }));
  };
  return new Promise((resolve) => {
    const server = createServer(handler);
    server.listen(0, "127.0.0.1", () => {
      const { port } = server.address() as AddressInfo;
      resolve({
        baseUrl: `http://127.0.0.1:${port}`,
        close: () => new Promise((r) => server.close(() => r())),
      });
    });
  });
}

type Run = { stdout: string; stderr: string; status: number | null };

type Wired = {
  readonly run: Run;
  readonly out: string;
  readonly homeDir: string;
  readonly projectDir: string;
};

// Wire a throwaway directory against `fake`, with a throwaway HOME so nothing touches the
// developer's own keys.json/registry/roles.json. `prepare` seeds the machine state a scenario
// needs BEFORE the run (a foreign artifact file, a hostile roles.json).
async function wireAgainst(
  fake: Fake,
  prepare: (dirs: { homeDir: string; projectDir: string }) => void = () => {},
): Promise<Wired & { cleanup: () => void }> {
  const homeDir = freshDir("petbox-step11-home-");
  const projectDir = freshDir("petbox-step11-proj-");
  prepare({ homeDir, projectDir });
  const run = await new Promise<Run>((resolve, reject) => {
    const child = spawn(
      process.execPath,
      [WIRE_TS, projectDir, PROJECT, "--key", "fake-key-value"],
      {
        cwd: projectDir,
        env: {
          ...process.env,
          USERPROFILE: homeDir,
          HOME: homeDir,
          HOMEDRIVE: undefined,
          HOMEPATH: undefined,
          PETBOX_WIRE_TEST_LOOPBACK_BASE_URL: fake.baseUrl,
        },
      },
    );
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (d) => (stdout += d.toString("utf8")));
    child.stderr.on("data", (d) => (stderr += d.toString("utf8")));
    child.on("error", reject);
    child.on("close", (status) => resolve({ stdout, stderr, status }));
  });
  return {
    run,
    out: run.stdout + run.stderr,
    homeDir,
    projectDir,
    cleanup: () => {
      rmSync(homeDir, { recursive: true, force: true });
      rmSync(projectDir, { recursive: true, force: true });
    },
  };
}

// A real file of someone else's, sitting exactly where apply wants to write one role's artifact.
// writeArtifact refuses to clobber it (no `petbox: managed` marker) ⇒ step 11 exits 1 (hard).
function plantForeignArtifact(projectDir: string): void {
  const dir = join(projectDir, ".claude", "agents");
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, "petbox-worker.md"), "# my own agent file, not PetBox's\n", "utf8");
}

// A binding claude-code cannot resolve (its model space is a CLOSED enum). The truthfulness gate
// blocks that role ⇒ step 11 exits 3.
function plantUnresolvableBinding(homeDir: string): void {
  mkdirSync(join(homeDir, ".petbox"), { recursive: true });
  writeFileSync(
    join(homeDir, ".petbox", "roles.json"),
    JSON.stringify({
      activeProfile: "default",
      profiles: {
        default: {
          agents: {
            "claude-code": {
              roles: {
                orchestrator: { model: "opus" },
                worker: { model: "gpt-4o-mini" }, // not in claude-code's closed model space
                "worker-highstakes": { model: "opus" },
                explore: { model: "haiku" },
                reserve: { model: "fable" },
              },
            },
          },
        },
      },
    }),
    "utf8",
  );
}

function assertExit(run: Run, expected: number, out: string, what: string): void {
  assert.notEqual(run.status, 127, `${what}: must never surface as 127 (the libuv-race symptom). Full output:\n${out}`);
  assert.equal(run.status, expected, `${what}: expected exit ${expected}. Full output:\n${out}`);
}

// Step 11 ran to completion and the run reached its OWN end. This is the guard on the decision the
// fix must not break: propagating the code must never turn into aborting at step 11.
function assertRunWasNotInterrupted(w: Wired): void {
  // `result roleScope=` — the old `result written=N` counter was RENAMED, not quietly repaired:
  // it counted ROLE writes only while every reader took it for "files" (card:
  // normalize-all-environments-to-default item 4). File counts now live on the `summary` line.
  assert.match(w.out, /\[11\/10\]: result roleScope=/, `step 11 must reach its own result line. Full output:\n${w.out}`);
  assert.match(
    w.out,
    /wire: (step 11|self-smoke FAILED)|^done\./m,
    `main() must reach its terminal message block after step 11. Full output:\n${w.out}`,
  );
  // Steps 4-8 (the work step 11 must never throw away) really did land on disk.
  for (const rel of ["keys.json", "projects.json"]) {
    assert.ok(
      existsSync(join(w.homeDir, ".petbox", rel)),
      `~/.petbox/${rel} must exist — step 11 must not discard earlier steps. Full output:\n${w.out}`,
    );
  }
  assert.ok(
    existsSync(join(w.projectDir, ".mcp.json")),
    `the project MCP config must exist. Full output:\n${w.out}`,
  );
}

// ---- the clean baseline: nothing may become noisier ------------------------

test("clean full `wire`: step 11 succeeds, the run exits 0 and still says done.", async () => {
  const fake = await startFakeServer({ smokeOk: true });
  const w = await wireAgainst(fake);
  try {
    assertExit(w.run, WIRE_EXIT.ok, w.out, "clean full wire");
    assert.match(w.out, /\[11\/10\]: done — all known harnesses accepted every role\./, `Full output:\n${w.out}`);
    assert.match(w.run.stdout, /^done\. NOTE:/m, `a clean run must still sign off with done. Full output:\n${w.out}`);
    assertRunWasNotInterrupted(w);
  } finally {
    await fake.close();
    w.cleanup();
  }
});

// ---- the defect itself: each non-zero step-11 code reaches the caller ------

test("step 11 INCOMPLETE (4): full `wire` exits 4, not 0", async () => {
  // Step 3's validate succeeds; apply's workspace probe (validate call #2) gets HTTP 500, so the
  // skills refresh is skipped for a reason the user never asked for — WIRE_EXIT.incomplete.
  const fake = await startFakeServer({ smokeOk: true, failProbeAfter: 1 });
  const w = await wireAgainst(fake);
  try {
    assertExit(w.run, WIRE_EXIT.incomplete, w.out, "step 11 incomplete");
    assert.match(w.out, /\[11\/10\]: done, but INCOMPLETE/, `Full output:\n${w.out}`);
    assert.match(w.out, /\[11\/10\] next: petbox-wire apply/, `Full output:\n${w.out}`);
    // The agent artifacts themselves were still written — "incomplete" here is about the skills,
    // and the run must not overstate the damage any more than it understated it before.
    assert.ok(existsSync(join(w.projectDir, ".claude", "agents", "petbox-worker.md")));
    assertRunWasNotInterrupted(w);
  } finally {
    await fake.close();
    w.cleanup();
  }
});

test("step 11 TRUTHFULNESS (3): full `wire` exits 3, not 0", async () => {
  const fake = await startFakeServer({ smokeOk: true });
  const w = await wireAgainst(fake, ({ homeDir }) => plantUnresolvableBinding(homeDir));
  try {
    assertExit(w.run, WIRE_EXIT.truthfulness, w.out, "step 11 truthfulness block");
    assert.match(w.out, /\[11\/10\]: truthfulness partial/, `Full output:\n${w.out}`);
    assertRunWasNotInterrupted(w);
  } finally {
    await fake.close();
    w.cleanup();
  }
});

test("step 11 HARD (1): a refused clobbering write makes full `wire` exit 1, not 0", async () => {
  // The loudest case of the bug: apply REFUSED to overwrite a real file, said so, and the run
  // still reported complete success to whatever script invoked it.
  const fake = await startFakeServer({ smokeOk: true });
  const w = await wireAgainst(fake, ({ projectDir }) => plantForeignArtifact(projectDir));
  try {
    assertExit(w.run, WIRE_EXIT.hard, w.out, "step 11 clobber refusal");
    assert.match(w.out, /\[11\/10\]: REFUSED to overwrite/, `Full output:\n${w.out}`);
    assertRunWasNotInterrupted(w);
  } finally {
    await fake.close();
    w.cleanup();
  }
});

// ---- both non-aborting steps fail at once: the STRONGEST code wins ---------

test("self-smoke (1) AND step 11 (4) both fail: full `wire` exits 1 — priority decides, not assignment order", async () => {
  // Step 10 sets 1 and keeps going; step 11 then finishes with 4. Step 11 assigns LAST, so a bare
  // `process.exitCode = applyResult.code` reports 4 and downgrades a hard failure to a retryable
  // one. strongestExitCode (1 > 3 > 4 > 0) is what makes the answer a decision instead of a race.
  const fake = await startFakeServer({ smokeOk: false, failProbeAfter: 1 });
  const w = await wireAgainst(fake);
  try {
    assertExit(w.run, WIRE_EXIT.hard, w.out, "self-smoke + step 11 both failed");
    assert.notEqual(w.run.status, WIRE_EXIT.incomplete, `4 here would mean step 11 overwrote step 10's 1. Full output:\n${w.out}`);
    // Neither failure may be swallowed by the other in the report, either.
    assert.match(w.out, /\[10\/10\] self-smoke: server did not return a numeric version/, `Full output:\n${w.out}`);
    assert.match(w.out, /\[11\/10\]: done, but INCOMPLETE/, `Full output:\n${w.out}`);
    assert.match(w.run.stderr, /wire: self-smoke FAILED/, `Full output:\n${w.out}`);
    assert.match(w.run.stderr, /wire: step 11 .* FAILED with exit 4/, `Full output:\n${w.out}`);
    assertRunWasNotInterrupted(w);
  } finally {
    await fake.close();
    w.cleanup();
  }
});

// ---- the decision the fix must NOT break ----------------------------------

test("a FAILING step 11 still does not abort the run: apply keeps writing past the failure and wire finishes", async () => {
  // fresh-wire-roster-unusable's decision, asserted rather than trusted. The refused role is
  // claude-code's `worker`; everything apply does AFTERWARDS must still have happened:
  //   - the remaining claude-code roles,
  //   - the other two harnesses (opencode, droid) — later iterations of the same loop,
  //   - the skills refresh, which runs after ALL harnesses,
  //   - and main()'s terminal message block after that.
  const fake = await startFakeServer({ smokeOk: true });
  const w = await wireAgainst(fake, ({ projectDir }) => plantForeignArtifact(projectDir));
  try {
    assertExit(w.run, WIRE_EXIT.hard, w.out, "step 11 clobber refusal");

    const stillWritten = [
      join(".claude", "agents", "petbox-orchestrator.md"),
      join(".opencode", "agent", "petbox-worker.md"),
      join(".factory", "droids", "petbox-worker.md"),
      join(".claude", "skills", "petbox", "SKILL.md"),
      join(".factory", "skills", "petbox", "SKILL.md"),
    ];
    for (const rel of stillWritten) {
      assert.ok(
        existsSync(join(w.projectDir, rel)),
        `step 11 must not abort on the first refusal — ${rel} is missing. If this fails, the exit-code ` +
          `fix turned a non-aborting step into an aborting one (fresh-wire-roster-unusable). Full output:\n${w.out}`,
      );
    }
    // …and apply reports exactly that shape: the harness holding the refused file is PARTIAL (it
    // wrote its other roles), the two that followed it in the loop are fully ok. The written-file
    // COUNT is deliberately not pinned — it tracks the default roster's size, which is none of
    // this test's business.
    assert.match(
      w.out,
      /\[11\/10\]: result roleScope=project ok=\[opencode,droid\] partial=\[claude-code\]/,
      `apply must report the partial write it actually performed. Full output:\n${w.out}`,
    );
    assertRunWasNotInterrupted(w);
  } finally {
    await fake.close();
    w.cleanup();
  }
});

// ---- the final message may not read like success --------------------------

test("a non-zero step 11 suppresses 'done.' — the last line is the failure (selfsmoke-failure-prints-done, same rule)", async () => {
  const fake = await startFakeServer({ smokeOk: true, failProbeAfter: 1 });
  const w = await wireAgainst(fake);
  try {
    assertExit(w.run, WIRE_EXIT.incomplete, w.out, "step 11 incomplete");
    assert.doesNotMatch(
      w.run.stdout,
      /^done\./m,
      `a run exiting non-zero must never sign off with "done.". Full output:\n${w.out}`,
    );
    const stderrLines = w.run.stderr.split("\n").filter((l) => l.trim().length > 0);
    const last = stderrLines[stderrLines.length - 1];
    assert.ok(last, `expected a terminal stderr line. Full output:\n${w.out}`);
    assert.match(last, /wire: step 11 .* FAILED with exit 4/, `the LAST line must be the failure. Full output:\n${w.out}`);
  } finally {
    await fake.close();
    w.cleanup();
  }
});
