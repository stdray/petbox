// Live-run coverage for card keys-json-supports-env-var-references, the two pieces that need a
// real `wire` process (not just direct calls into registry.ts / posix-env.ts, which
// registry.test.ts and posix-env.test.ts already cover):
//
//   decision 1 — bootstrap writes a $VAR reference into ~/.petbox/keys.json itself, by default,
//   whenever the key it resolved came straight from a live env var (not only as a manual option).
//
//   decision 2 — an unresolved reference fails LOUDLY, on start, before the run's first network
//   call — proven here by NEVER starting a fake server for that scenario at all: if wire.ts tried
//   to reach the network anyway, the test would hang/error against a closed port instead of
//   passing, which is a stronger guarantee than asserting "0 requests received".
//
// Same loopback-sandbox seam as wire-full-exit-step11.test.ts / wire-full-exit-races.test.ts
// (PETBOX_WIRE_TEST_LOOPBACK_BASE_URL) — see those files for why the full-wire path is otherwise
// untestable and for the guard rails on that seam.
//
// Run: node --test src/wire-bootstrap-keys-json-ref.test.ts

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { deriveEnvVar } from "./wire-identity.ts";
import { makeGitWorkingTree } from "./test-git-tree.ts";
import { WIRE_EXIT } from "./wire-exit.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");
const PROJECT = "keys-ref-bootstrap-proj";

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

type Run = { stdout: string; stderr: string; status: number | null };

function runWire(argv: string[], env: NodeJS.ProcessEnv): Promise<Run> {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [WIRE_TS, ...argv], { env });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (d) => (stdout += d.toString("utf8")));
    child.stderr.on("data", (d) => (stderr += d.toString("utf8")));
    child.on("error", reject);
    child.on("close", (status) => resolve({ stdout, stderr, status }));
  });
}

test("decision 1: a full wire run with the key already live in the environment writes a $VAR REFERENCE to keys.json, not a copy of the secret; the request the server actually receives carries the REAL key, never literal '${VAR}' text", async () => {
  const homeDir = freshDir("petbox-keysref-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-keysref-proj-"));
  const envVar = deriveEnvVar(PROJECT);
  const realKey = "live-bootstrap-secret-abc123";
  let receivedApiKeyHeaders: string[] = [];

  const server = createServer((req: IncomingMessage, res: ServerResponse) => {
    const url = req.url ?? "";
    if (url.includes("/api/auth/validate")) {
      receivedApiKeyHeaders.push(String(req.headers["x-api-key"] ?? ""));
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ project: PROJECT, workspace: "keysref-ws" }));
      return;
    }
    // Everything past step 4 (skills probe, self-smoke, etc.) 404s — irrelevant to this test,
    // which only needs the run to survive long enough to execute step 4's writeKeyToStore.
    res.writeHead(404, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ error: "not found" }));
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", () => resolve()));
  const { port } = server.address() as AddressInfo;

  try {
    const run = await runWire([projectDir, PROJECT], {
      ...process.env,
      USERPROFILE: homeDir,
      HOME: homeDir,
      HOMEDRIVE: undefined,
      HOMEPATH: undefined,
      PETBOX_WIRE_TEST_LOOPBACK_BASE_URL: `http://127.0.0.1:${port}`,
      [envVar]: realKey,
    });
    const out = run.stdout + run.stderr;

    // The server must have seen the REAL key — never a literal, unexpanded "${VAR}" reaching a
    // request header (the qwen-code #11499 shape this card exists to rule out).
    assert.ok(receivedApiKeyHeaders.length > 0, `validate was never called. Full output:\n${out}`);
    assert.ok(
      receivedApiKeyHeaders.every((h) => h === realKey),
      `every X-Api-Key header must carry the real key, never a placeholder. Got: ${JSON.stringify(receivedApiKeyHeaders)}`,
    );

    // keys.json must hold a REFERENCE, not the secret — decision 1: bootstrap does this itself,
    // by default, whenever the key came from a live env var.
    const keysJson = JSON.parse(readFileSync(join(homeDir, ".petbox", "keys.json"), "utf8"));
    assert.equal(keysJson[envVar], "${" + envVar + "}", `Full output:\n${out}`);
    assert.notEqual(keysJson[envVar], realKey, "the file must never carry a copy of the secret");

    assert.match(out, /as a \$VAR reference/i, `step 4's log line should say it wrote a reference. Full output:\n${out}`);
  } finally {
    await new Promise<void>((resolve) => server.close(() => resolve()));
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("decision 2: a project already wired with a REFERENCE, re-run with that env var NOT inherited, fails LOUDLY before any network call — no server is even started, so a request getting through would hang/error rather than pass", async () => {
  const homeDir = freshDir("petbox-keysref-unresolved-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-keysref-unresolved-proj-"));
  const envVar = deriveEnvVar(PROJECT);

  mkdirSync(join(homeDir, ".petbox"), { recursive: true });
  writeFileSync(
    join(homeDir, ".petbox", "projects.json"),
    JSON.stringify({ entries: [{ prefix: projectDir, project: PROJECT, envVar }] }),
    "utf8",
  );
  writeFileSync(join(homeDir, ".petbox", "keys.json"), JSON.stringify({ [envVar]: "${" + envVar + "}" }), "utf8");

  const cleanEnv = { ...process.env };
  delete cleanEnv[envVar]; // the exact scenario: not inherited by this process (a harness that
  // doesn't pass the whole environment through to a hook/MCP child)
  cleanEnv["USERPROFILE"] = homeDir;
  cleanEnv["HOME"] = homeDir;
  delete cleanEnv["HOMEDRIVE"];
  delete cleanEnv["HOMEPATH"];
  // Deliberately NO PETBOX_WIRE_TEST_LOOPBACK_BASE_URL and no fake server: this run must never
  // reach step 3's network call at all.

  const run = await runWire([projectDir, PROJECT], cleanEnv);
  const out = run.stdout + run.stderr;

  try {
    assert.equal(run.status, WIRE_EXIT.usage, `expected the usage exit code (bad configuration, not a hard crash). Full output:\n${out}`);
    assert.match(out, new RegExp(envVar), `the envVar name must be in the diagnostic. Full output:\n${out}`);
    assert.match(out, new RegExp(PROJECT), `the project name must be in the diagnostic. Full output:\n${out}`);
    assert.doesNotMatch(out, /401/, `must never look like a provider rejection. Full output:\n${out}`);
    assert.doesNotMatch(out, /at TestContext|at Module\._compile|node:internal/, `must be a clean operator-facing message, not a raw stack trace. Full output:\n${out}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});
