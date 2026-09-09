// Behavioral proof for task wire-fragment-first-run-empty-grades: the FIRST-EVER `wire` run on a
// brand-new machine must print a qwen config fragment whose `agents.modelGrades` (and `model.name`
// — task qwen-model-name-into-fragment) are already filled in, not the empty `{}` a fresh
// roles.json used to produce.
//
// THE BUG. Step 8 (installGlobalHooks, which prints the qwen fragment via
// qwen-config-fragment.ts) used to run BEFORE step 11's seedDefaultRoleBindingsIfMissing — the
// call that creates roles.json's default role→model bindings on a fresh machine. So the very
// first `wire` run's own step 8 read an empty/missing roles.json and printed
// `"agents": {"modelGrades": {}}`. A second run printed the correct fragment, but by then the
// human who was ever going to SEE the empty one (this fragment is meant to be pasted exactly
// once, per observations/wire-qwen-fragment-first-run-empty-grades) already had.
//
// THE FIX (wire.ts): the role-binding seed call now runs as its own step 7c, strictly before step
// 8's installGlobalHooks. This test proves the observable effect end-to-end: a REAL wire run
// against a completely fresh $HOME (no ~/.petbox/roles.json at all) prints a non-empty fragment on
// its very first invocation, that a second run (roles.json now pre-seeded) is idempotent with the
// first, and that a hand-made rebind between the two runs is never clobbered.
//
// Seam: PETBOX_WIRE_TEST_LOOPBACK_BASE_URL, the same http-loopback-only override
// wire-full-exit-step11.test.ts/wire-full-exit-races.test.ts use. spawn (async), not spawnSync:
// this process must stay free to answer the child's requests.
//
// Run: node --test src/wire-qwen-fragment-first-run.test.ts

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { makeGitWorkingTree } from "./test-git-tree.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");
const PROJECT = "wire-qwen-fragment-first-run-proj";

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

type Fake = { baseUrl: string; close: () => Promise<void> };

// Minimal fake — every scenario here needs self-smoke to succeed; nothing tests failure paths.
function startFakeServer(): Promise<Fake> {
  const handler = (req: IncomingMessage, res: ServerResponse): void => {
    const url = req.url ?? "";
    if (url.includes("/api/auth/validate")) {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ project: PROJECT, workspace: "qwen-fragment-ws" }));
      return;
    }
    if (url.includes("wire-smoke")) {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ sessionId: "s1", version: 1, messageCount: 1 }));
      return;
    }
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

function runWire(fake: Fake, homeDir: string, projectDir: string): Promise<Run> {
  return new Promise((resolve, reject) => {
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
}

// String-aware brace matching — the fragment's own text contains literal `{`/`}` INSIDE string
// values (`"${session_id}"`), so a naive `text.slice(text.indexOf("{"))` + JSON.parse (fine for the
// unit tests in qwen-config-fragment.test.ts, which have no trailing prose) is not enough once real
// `wire` stdout has more log lines after the fragment.
function extractFirstJsonObject(text: string, fromIndex: number): any {
  const start = text.indexOf("{", fromIndex);
  assert.ok(start !== -1, `no '{' found from index ${fromIndex}. Text:\n${text}`);
  let depth = 0;
  let inString = false;
  let escaped = false;
  for (let i = start; i < text.length; i++) {
    const ch = text[i];
    if (inString) {
      if (escaped) escaped = false;
      else if (ch === "\\") escaped = true;
      else if (ch === '"') inString = false;
      continue;
    }
    if (ch === '"') {
      inString = true;
      continue;
    }
    if (ch === "{") depth++;
    else if (ch === "}") {
      depth--;
      if (depth === 0) return JSON.parse(text.slice(start, i + 1));
    }
  }
  throw new Error(`unterminated JSON object starting at ${start}`);
}

function qwenFragmentFrom(out: string): any {
  const marker = out.indexOf("Paste these top-level keys into $QWEN_HOME/settings.json");
  assert.ok(marker !== -1, `expected the qwen fragment marker in wire's output. Full output:\n${out}`);
  return extractFirstJsonObject(out, marker);
}

test("first-ever wire run on a fresh machine prints a qwen fragment with non-empty agents.modelGrades and model.name (not the old empty {})", async () => {
  const fake = await startFakeServer();
  const homeDir = freshDir("petbox-qwen-frag-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-qwen-frag-proj-"));
  try {
    const run = await runWire(fake, homeDir, projectDir);
    const out = run.stdout + run.stderr;
    assert.equal(run.status, 0, `wire must exit 0 on a clean fresh-machine run. Full output:\n${out}`);
    const fragment = qwenFragmentFrom(out);
    assert.ok(
      Object.keys(fragment.agents.modelGrades).length > 0,
      `agents.modelGrades must be seeded before the fragment is printed, not empty. Fragment:\n${JSON.stringify(fragment, null, 2)}`,
    );
    assert.equal(typeof fragment.model?.name, "string");
    assert.ok(fragment.model.name.length > 0);
    // Order matters for this task specifically: step 7c (seed) must have run before step 8
    // (install+print) in the actual log stream, not just "roles.json happens to exist by now".
    const seedIdx = out.indexOf("[7c/10] roles:");
    const fragmentIdx = out.indexOf("Paste these top-level keys");
    assert.ok(seedIdx !== -1, `expected a [7c/10] roles: seed log line. Full output:\n${out}`);
    assert.ok(seedIdx < fragmentIdx, `seed step must log BEFORE the fragment is printed. Full output:\n${out}`);
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("a second wire run on the same machine reprints the identical fragment (idempotent — no re-seed-on-top-of-existing)", async () => {
  const fake = await startFakeServer();
  const homeDir = freshDir("petbox-qwen-frag-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-qwen-frag-proj-"));
  try {
    const run1 = await runWire(fake, homeDir, projectDir);
    assert.equal(run1.status, 0, `first run must exit 0. Full output:\n${run1.stdout + run1.stderr}`);
    const fragment1 = qwenFragmentFrom(run1.stdout + run1.stderr);

    const run2 = await runWire(fake, homeDir, projectDir);
    assert.equal(run2.status, 0, `second run must exit 0. Full output:\n${run2.stdout + run2.stderr}`);
    const out2 = run2.stdout + run2.stderr;
    const fragment2 = qwenFragmentFrom(out2);

    assert.deepEqual(fragment2, fragment1, `second run's fragment must match the first byte-for-byte (idempotency). Second output:\n${out2}`);
    // The second run must report roles.json as already existing, not re-create it from scratch.
    assert.match(out2, /\[7c\/10\] roles:.*already exist/, `second run must not re-seed from a blank slate. Full output:\n${out2}`);
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("a hand rebind between two wire runs is never clobbered by the step 7c seed (seeding is additive only)", async () => {
  const fake = await startFakeServer();
  const homeDir = freshDir("petbox-qwen-frag-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-qwen-frag-proj-"));
  try {
    const run1 = await runWire(fake, homeDir, projectDir);
    assert.equal(run1.status, 0, `first run must exit 0. Full output:\n${run1.stdout + run1.stderr}`);
    const fragment1 = qwenFragmentFrom(run1.stdout + run1.stderr);
    assert.equal(fragment1.model.name, "ds-deepseek-v4-pro"); // the kit's own default seed

    // Hand-rebind the qwen orchestrator role to a DIFFERENT registered id, exactly the way
    // `petbox-wire model set orchestrator ... --agent qwen` would.
    const rolesPath = join(homeDir, ".petbox", "roles.json");
    const roles = JSON.parse(readFileSync(rolesPath, "utf8"));
    roles.profiles.default.agents.qwen.roles.orchestrator.model = "openai:go-qwen3.8-max";
    writeFileSync(rolesPath, JSON.stringify(roles, null, 2), "utf8");

    const run2 = await runWire(fake, homeDir, projectDir);
    assert.equal(run2.status, 0, `second run must exit 0. Full output:\n${run2.stdout + run2.stderr}`);
    const fragment2 = qwenFragmentFrom(run2.stdout + run2.stderr);
    assert.equal(
      fragment2.model.name,
      "go-qwen3.8-max",
      `step 7c's seed must not have reverted the hand rebind. Fragment:\n${JSON.stringify(fragment2, null, 2)}`,
    );
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});
