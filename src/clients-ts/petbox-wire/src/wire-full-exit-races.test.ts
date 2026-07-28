// Behavioral proof for wire-six-remaining-exit-races: the six hard `process.exit()` call sites in
// the FULL `wire` command — the first-run path a newcomer takes — each fired immediately after a
// completed live network round trip, which races Windows' async-handle teardown for whatever
// socket is still closing:
//   Assertion failed: !(handle->flags & UV_HANDLE_CLOSING), file src\win\async.c, line 76
// and the caller observes 127 instead of the code the site named. Same defect already fixed in
// doctor, status and apply.
//
// wire-process-exit-whitelist.test.ts proves STRUCTURALLY that no raw exit remains at these
// sites. This file proves the other half, which structure cannot: that each error scenario really
// does deliver its intended exit code out of a real process — 1 (or 2), never 127, never 0. Both
// halves are needed; neither substitutes for the other (see that file's header on why a
// behavioral test alone cannot prove a timing-dependent race is gone).
//
// The six sites, one test each:
//   1. validateKey — network error          (server closed → fetch throws)   → 1
//   2. validateKey — 401                    (key rejected)                   → 1
//   3. validateKey — project mismatch       (200, different project)         → 1
//   4. ensureTelemetryLog — network error   (--telemetry, server gone)       → 1
//   5. ensureTelemetryLog — !ok             (--telemetry, HTTP 500)          → 1
//   6. main() step 3b — resolveWorkspace    (200 without `workspace`)        → 2
// Plus the top-level main().catch, whose fate the card asked to decide explicitly.
//
// HOW these are reachable at all: the full `wire` path pins its base URL to a constant, so a test
// had no way to stand a fake server in front of it — which is precisely why these six exit points
// went unproven through three rounds of fixing this same bug elsewhere. wire.ts now honors
// PETBOX_WIRE_TEST_LOOPBACK_BASE_URL, but ONLY when it names an http:// loopback address (a real
// host, https, or a DNS name is ignored with a warning: a wiring run hands over an API key and
// must never be redirectable). The same flag disables the one machine-GLOBAL write on the path
// (Windows user-scope env persistence, step 4), so running this suite cannot leave a junk
// PETBOX_*_API_KEY in the developer's own environment. That guard rail is asserted here too.
//
// spawn (async), not spawnSync: this process's event loop must stay free to answer the child's
// requests into the fake server (spawnSync self-deadlocks — see doctor-definition.test.ts).
//
// Run: node --test src/wire-full-exit-races.test.ts

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, realpathSync, rmSync } from "node:fs";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { WIRE_EXIT } from "./wire-exit.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");
const PROJECT = "wire-exit-race-proj";

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

type Fake = { baseUrl: string; close: () => Promise<void> };

function startFakeServer(handler: (req: IncomingMessage, res: ServerResponse) => void): Promise<Fake> {
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

// A port nothing listens on: the fetch fails at connect, so validateKey/ensureTelemetryLog take
// their `catch` branch. Obtained by opening and immediately closing a listener, so the port is
// real and free rather than guessed.
async function deadBaseUrl(): Promise<string> {
  const fake = await startFakeServer((_req, res) => res.end());
  const url = fake.baseUrl;
  await fake.close();
  return url;
}

type Run = { stdout: string; stderr: string; status: number | null };

function runWire(opts: {
  cwd: string;
  homeDir: string;
  baseUrl: string;
  args: readonly string[];
}): Promise<Run> {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [WIRE_TS, ...opts.args], {
      cwd: opts.cwd,
      env: {
        ...process.env,
        USERPROFILE: opts.homeDir,
        HOME: opts.homeDir,
        HOMEDRIVE: undefined,
        HOMEPATH: undefined,
        PETBOX_WIRE_TEST_LOOPBACK_BASE_URL: opts.baseUrl,
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

// Every scenario below wires a throwaway directory with an explicit --key, so nothing depends on
// the developer's own keys.json/registry.
async function wireAgainst(
  baseUrl: string,
  extraArgs: readonly string[] = [],
): Promise<{ run: Run; out: string; homeDir: string; projectDir: string; cleanup: () => void }> {
  const homeDir = freshDir("petbox-wire-race-home-");
  const projectDir = freshDir("petbox-wire-race-proj-");
  const run = await runWire({
    cwd: projectDir,
    homeDir,
    baseUrl,
    args: [projectDir, PROJECT, "--key", "fake-key-value", ...extraArgs],
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

// THE risk this whole change carries, asserted rather than reasoned about: `process.exit(code)`
// INTERRUPTS execution; `process.exitCode = code` does not. Every converted site had to keep
// cutting off whatever the hard exit used to cut off. For the four sites that abort during step 3
// / 3b, "cut off" means step 4 onwards never ran — so NOTHING was persisted: no key store, no
// registry entry, no project config files. If abortRun ever stopped being a control-flow abort,
// a rejected key would start being written to disk, and these assertions are what would catch it.
function assertNothingWasPersisted(homeDir: string, projectDir: string, out: string): void {
  for (const rel of ["keys.json", "projects.json", "wire"]) {
    assert.equal(
      existsSync(join(homeDir, ".petbox", rel)),
      false,
      `aborting during validate/workspace must persist NOTHING — found ~/.petbox/${rel}. ` +
        `If this fails, the abort stopped interrupting execution. Full output:\n${out}`,
    );
  }
  for (const rel of [".mcp.json", ".opencode", ".claude"]) {
    assert.equal(
      existsSync(join(projectDir, rel)),
      false,
      `aborting during validate/workspace must write no project files — found ${rel}. Full output:\n${out}`,
    );
  }
  assert.doesNotMatch(out, /\[4\/10\]/, `step 4 must never have started. Full output:\n${out}`);
}

// Same property for the two telemetry sites, which abort MID-run (step 7b) rather than before any
// persistence: the steps AFTER them must not have run. writeTelemetrySettings is the statement
// immediately following ensureTelemetryLog, and steps 8-11 follow that, so the observable proof is
// that no OTEL_* env was written and the run never reached its terminal message.
function assertTelemetryAbortCutTheRunOff(projectDir: string, out: string): void {
  for (const rel of ["settings.json", "settings.local.json"]) {
    const path = join(projectDir, ".claude", rel);
    if (!existsSync(path)) continue;
    assert.doesNotMatch(
      readFileSync(path, "utf8"),
      /OTEL_/,
      `aborting in ensureTelemetryLog must skip writeTelemetrySettings — found OTEL_* in ${rel}. ` +
        `If this fails, the abort stopped interrupting execution. Full output:\n${out}`,
    );
  }
  assert.doesNotMatch(out, /\[8\/10\]|\[9\/10\]|\[10\/10\]|\[11\/10\]/, `steps after 7b must never have run. Full output:\n${out}`);
}

// Shared assertion: the observable failure mode being fixed is "127 instead of the named code".
function assertExit(run: Run, expected: number, out: string, what: string): void {
  assert.notEqual(run.status, 127, `${what}: must never surface as 127 (the libuv-race symptom). Full output:\n${out}`);
  assert.equal(run.status, expected, `${what}: expected exit ${expected}. Full output:\n${out}`);
}

// ---- site 1: validateKey, network error ------------------------------------

test("site 1 — validateKey network error: full `wire` exits 1 (not 127, not 0)", async () => {
  const dead = await deadBaseUrl();
  const { run, out, homeDir, projectDir, cleanup } = await wireAgainst(dead);
  try {
    assertExit(run, WIRE_EXIT.hard, out, "validateKey network error");
    assert.match(out, /\[3\/10\] validate: could not reach/, `Full output:\n${out}`);
    assertNothingWasPersisted(homeDir, projectDir, out);
  } finally {
    cleanup();
  }
});

// ---- site 2: validateKey, 401 ----------------------------------------------

test("site 2 — validateKey 401: full `wire` exits 1 (not 127, not 0)", async () => {
  const fake = await startFakeServer((_req, res) => {
    res.writeHead(401, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ error: "unauthorized" }));
  });
  const { run, out, homeDir, projectDir, cleanup } = await wireAgainst(fake.baseUrl);
  try {
    assertExit(run, WIRE_EXIT.hard, out, "validateKey 401");
    assert.match(out, /server rejected the API key \(401\)/, `Full output:\n${out}`);
    // A rejected key must never reach the disk — the reason validateKey aborts BEFORE step 4.
    assertNothingWasPersisted(homeDir, projectDir, out);
  } finally {
    await fake.close();
    cleanup();
  }
});

// ---- site 3: validateKey, project mismatch ---------------------------------

test("site 3 — validateKey project mismatch: full `wire` exits 1 (not 127, not 0)", async () => {
  const fake = await startFakeServer((_req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ project: "some-other-project", workspace: "ws" }));
  });
  const { run, out, homeDir, projectDir, cleanup } = await wireAgainst(fake.baseUrl);
  try {
    assertExit(run, WIRE_EXIT.hard, out, "validateKey project mismatch");
    assert.match(out, /key belongs to project 'some-other-project'/, `Full output:\n${out}`);
    assertNothingWasPersisted(homeDir, projectDir, out);
  } finally {
    await fake.close();
    cleanup();
  }
});

// ---- site 6: main() step 3b, resolveWorkspace ------------------------------
//
// Placed next to the validateKey sites because it is the same failure window: it fires on the
// statement AFTER `await validateKey(...)` returns. Its `process.exit(ws.exitCode)` READ like a
// child process's status being forwarded — the reason it was mis-triaged as safe twice — but
// `exitCode` is a locally computed WIRE_EXIT.usage from resolveWorkspace (wire-identity.ts).
// The code it must deliver is therefore 2, and proving that also proves it is not a forward.

test("site 6 — resolveWorkspace after a completed validateKey: full `wire` exits 2 (usage), not 127", async () => {
  const fake = await startFakeServer((_req, res) => {
    // 200 with the RIGHT project but no `workspace` field — an older server. resolveWorkspace then
    // has nothing to resolve and no --workspace flag was passed.
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ project: PROJECT }));
  });
  const { run, out, homeDir, projectDir, cleanup } = await wireAgainst(fake.baseUrl);
  try {
    assertExit(run, WIRE_EXIT.usage, out, "resolveWorkspace usage failure");
    assert.match(out, /--workspace is required/, `Full output:\n${out}`);
    // Site 6 is the one converted with a plain early `return` rather than abortRun, so this is
    // where a missing `return` would show up as the run marching on into step 4.
    assertNothingWasPersisted(homeDir, projectDir, out);
    // It really did complete step 3's live round trip first — i.e. this is the racy window, not
    // an early bail before any network activity.
    assert.match(out, /\[3\/10\] validate: OK/, `expected step 3 to have completed. Full output:\n${out}`);
  } finally {
    await fake.close();
    cleanup();
  }
});

// ---- sites 4 & 5: ensureTelemetryLog (--telemetry opt-in) ------------------
//
// These sit at step 7b, so the run must first get all the way through validate/persist/kit-copy/
// registry/project-files. The fake server answers validate, then fails the log-ensure call.

test("site 4 — ensureTelemetryLog network error: full `wire --telemetry` exits 1 (not 127, not 0)", async () => {
  // validate succeeds against a live server; the telemetry POST goes to a dead port. Two servers
  // are not possible on one base URL, so instead: the same server answers validate and then
  // destroys the socket for the logs call, which surfaces to fetch() as a network failure.
  const fake = await startFakeServer((req, res) => {
    const url = req.url ?? "";
    if (url.includes("/api/auth/validate")) {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ project: PROJECT, workspace: "race-ws" }));
      return;
    }
    if (url.includes("/logs")) {
      req.destroy();
      return;
    }
    res.writeHead(404).end("{}");
  });
  const { run, out, projectDir, cleanup } = await wireAgainst(fake.baseUrl, ["--telemetry"]);
  try {
    assertExit(run, WIRE_EXIT.hard, out, "ensureTelemetryLog network error");
    assert.match(out, /\[telemetry\] could not reach/, `Full output:\n${out}`);
    assertTelemetryAbortCutTheRunOff(projectDir, out);
  } finally {
    await fake.close();
    cleanup();
  }
});

test("site 5 — ensureTelemetryLog HTTP 500: full `wire --telemetry` exits 1 (not 127, not 0)", async () => {
  const fake = await startFakeServer((req, res) => {
    const url = req.url ?? "";
    if (url.includes("/api/auth/validate")) {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ project: PROJECT, workspace: "race-ws" }));
      return;
    }
    if (url.includes("/logs")) {
      res.writeHead(500, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ error: "internal" }));
      return;
    }
    res.writeHead(404).end("{}");
  });
  const { run, out, projectDir, cleanup } = await wireAgainst(fake.baseUrl, ["--telemetry"]);
  try {
    assertExit(run, WIRE_EXIT.hard, out, "ensureTelemetryLog !ok");
    assert.match(out, /\[telemetry\] failed to ensure log/, `Full output:\n${out}`);
    assert.match(out, /HTTP 500/, `must name the actual status, not a generic network claim. Full output:\n${out}`);
    assertTelemetryAbortCutTheRunOff(projectDir, out);
  } finally {
    await fake.close();
    cleanup();
  }
});

// ---- the loopback seam's own guard rails -----------------------------------
//
// The seam is production code, so its restriction is part of the contract and is tested like one.

test("the test seam is loopback-only: a non-loopback base URL is IGNORED, loudly", async () => {
  const homeDir = freshDir("petbox-wire-race-home-");
  const projectDir = freshDir("petbox-wire-race-proj-");
  try {
    // https + a real hostname. If the seam honored it, wire would try to reach that host; instead
    // it must warn and fall back to the built-in default. `--help` keeps the run from doing
    // anything at all beyond arg parsing, so nothing is sent anywhere by this test.
    const run = await runWire({
      cwd: projectDir,
      homeDir,
      baseUrl: "https://attacker.example.com",
      args: ["--help"],
    });
    const out = run.stdout + run.stderr;
    assert.match(
      out,
      /is not an http:\/\/ loopback address — ignored/,
      `a non-loopback override must be refused out loud. Full output:\n${out}`,
    );
    assert.equal(run.status, WIRE_EXIT.ok, `--help still exits 0. Full output:\n${out}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("the loopback sandbox skips the machine-GLOBAL user-scope env write (so the suite cannot pollute the developer's environment)", async () => {
  const fake = await startFakeServer((req, res) => {
    const url = req.url ?? "";
    if (url.includes("/api/auth/validate")) {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ project: PROJECT, workspace: "race-ws" }));
      return;
    }
    if (url.includes("/logs")) {
      res.writeHead(500, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ error: "internal" }));
      return;
    }
    res.writeHead(404).end("{}");
  });
  const { out, cleanup } = await wireAgainst(fake.baseUrl, ["--telemetry"]);
  try {
    assert.match(
      out,
      /\[4\/10\] loopback sandbox — SKIPPED user-scope env persistence/,
      `step 4 must not touch machine-global state under the sandbox. Full output:\n${out}`,
    );
    assert.doesNotMatch(
      out,
      /\[4\/10\] persisted .* to user-scope env/,
      `the real user-scope persistence must not have run. Full output:\n${out}`,
    );
  } finally {
    await fake.close();
    cleanup();
  }
});
