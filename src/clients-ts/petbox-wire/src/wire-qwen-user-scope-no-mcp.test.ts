// Regression cover for the owner decision of 09.09.2026, verbatim «Нет — убрать глобальную
// запись», answering "does qwen launched OUTSIDE any wired project need PetBox tools?".
//
// WHAT WENT WRONG. wire.ts's installGlobalHooks used to write `mcpServers.petbox` into the USER
// scope file `$QWEN_HOME/settings.json`, filling its `X-Api-Key` header with THIS run's project
// env-var name. That file is machine-global while the env var is per-project, so the entry could
// only ever name ONE project and every later `wire` run silently repointed it. Measured on the
// owner's box: it held `${PETBOX_SMOKE_API_KEY}` — the last `wire` had run from a throwaway smoke
// directory — and that variable was set and VALID (`whoami` → project "smoke", with
// tasks:write/memory:write/data:write). A bare `qwen` started anywhere outside a wired root was
// therefore reading and WRITING a foreign project's boards and memory, with no warning anywhere.
//
// ACCEPTED COST (owner's explicit choice, not an oversight): qwen outside a wired directory now
// has no PetBox boards and no memory at all. Inside a wired project nothing changes — the
// WORKSPACE-scope `<project>/.qwen/settings.json` entry writeProjectFiles writes is the one that
// has always actually governed (defect qwen-mcp-json-shadows-workspace-entry), and it resolves
// each project's OWN env var.
//
// WHY A TEST AND NOT A COMMENT. The removal is one deleted assignment inside a 400-line function
// that is edited often; the next person extending installGlobalHooks has every reason to "restore"
// it. These tests fail the moment a petbox MCP entry reappears at user scope, and they are
// BEHAVIORAL — a real `wire` child process against a throwaway $HOME, not a source-text grep — so
// they also catch the entry coming back through some other writer.
//
// Seam: PETBOX_WIRE_TEST_LOOPBACK_BASE_URL, the same http-loopback-only override
// wire-qwen-fragment-first-run.test.ts / wire-full-exit-step11.test.ts use. spawn (async), not
// spawnSync: this process must stay free to answer the child's requests.
//
// Run: node --test src/wire-qwen-user-scope-no-mcp.test.ts

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { makeGitWorkingTree } from "./test-git-tree.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");
const PROJECT = "wire-qwen-user-scope-no-mcp-proj";

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
      res.end(JSON.stringify({ project: PROJECT, workspace: "qwen-user-scope-ws" }));
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

function runWire(fake: Fake, homeDir: string, workingTree: string): Promise<Run> {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [WIRE_TS, workingTree, PROJECT, "--key", "fake-key-value"], {
      cwd: workingTree,
      env: {
        ...process.env,
        USERPROFILE: homeDir,
        HOME: homeDir,
        HOMEDRIVE: undefined,
        HOMEPATH: undefined,
        // QWEN_HOME is honored by qwen-paths.ts and could be inherited from the developer's own
        // shell; clear it so the assertions below are about the throwaway $HOME, never the real box.
        QWEN_HOME: undefined,
        PETBOX_WIRE_TEST_LOOPBACK_BASE_URL: fake.baseUrl,
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

function readUserQwenSettings(homeDir: string): any {
  return JSON.parse(readFileSync(join(homeDir, ".qwen", "settings.json"), "utf8"));
}

test("a full wire run writes NO petbox MCP entry into the user-scope $QWEN_HOME/settings.json", async () => {
  const fake = await startFakeServer();
  const homeDir = freshDir("petbox-qwen-userscope-home-");
  const workingTree = makeGitWorkingTree(freshDir("petbox-qwen-userscope-proj-"));
  try {
    const run = await runWire(fake, homeDir, workingTree);
    const out = run.stdout + run.stderr;
    assert.equal(run.status, 0, `wire must exit 0 on a clean fresh-machine run. Full output:\n${out}`);

    const settings = readUserQwenSettings(homeDir);
    // The file WAS written by step 8 — otherwise "no mcpServers" would be vacuously true because
    // nothing ran at all. These two keys are what installGlobalHooks legitimately still writes.
    assert.ok(settings.hooks && typeof settings.hooks === "object", `step 8 must still write qwen hooks. Settings:\n${JSON.stringify(settings, null, 2)}`);
    assert.equal(settings.security?.auth?.selectedType, "openai");

    // The decision itself: nothing under mcpServers at user scope. Not an empty map either — the
    // key must be absent, because an empty `mcpServers: {}` is still a kit-authored write into a
    // hand-maintained file.
    assert.ok(
      !("mcpServers" in settings),
      `user-scope qwen settings must carry no mcpServers key at all (owner decision 09.09.2026). Got: ${JSON.stringify(settings.mcpServers)}`,
    );
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(workingTree, { recursive: true, force: true });
  }
});

test("the WORKSPACE-scope .qwen/settings.json petbox entry is still written — the decision removes the global entry only", async () => {
  const fake = await startFakeServer();
  const homeDir = freshDir("petbox-qwen-userscope-home-");
  const workingTree = makeGitWorkingTree(freshDir("petbox-qwen-userscope-proj-"));
  try {
    const run = await runWire(fake, homeDir, workingTree);
    const out = run.stdout + run.stderr;
    assert.equal(run.status, 0, `wire must exit 0. Full output:\n${out}`);

    const ws = JSON.parse(readFileSync(join(workingTree, ".qwen", "settings.json"), "utf8"));
    assert.ok(ws.mcpServers?.petbox, `the workspace-scope petbox entry is the one qwen actually uses inside a wired project. Got:\n${JSON.stringify(ws, null, 2)}`);
    // Unresolved ${VAR} placeholder, per qwen-mcp-entry.ts — a literal key here is the defect that
    // module's own tests pin; asserted again end-to-end so the two scopes cannot be confused.
    assert.match(ws.mcpServers.petbox.headers["X-Api-Key"], /^\$\{PETBOX_.*_API_KEY\}$/);
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(workingTree, { recursive: true, force: true });
  }
});

test("a hand-maintained user-scope settings.json survives a wire run byte-for-byte outside the keys the kit owns", async () => {
  const fake = await startFakeServer();
  const homeDir = freshDir("petbox-qwen-userscope-home-");
  const workingTree = makeGitWorkingTree(freshDir("petbox-qwen-userscope-proj-"));
  try {
    // A miniature of the owner's real file: the layout the kit deliberately stopped writing
    // (modelProviders / agents.modelGrades / model / security.outboundCorrelation) plus an
    // unrelated third-party MCP server of the user's own. All of it must come back untouched.
    const handMade = {
      security: { auth: { selectedType: "qwen-oauth" }, outboundCorrelation: { sessionIdTemplate: "${session_id}" } },
      modelProviders: {
        "ds-deepseek-v4-pro-max": [
          { id: "ds-deepseek-v4-pro-max", samplingParams: { max_tokens: 131072 }, extra_body: { reasoning_effort: "max" }, contextWindowSize: 1048576 },
        ],
      },
      model: { name: "ds-deepseek-v4-pro-max", baseUrl: "https://example.invalid/v1" },
      agents: { modelGrades: { "ds-deepseek-v4-pro-max": "high" } },
      mcpServers: { "not-petbox": { command: "some-other-server", args: ["--stdio"] } },
    };
    mkdirSync(join(homeDir, ".qwen"), { recursive: true });
    writeFileSync(join(homeDir, ".qwen", "settings.json"), JSON.stringify(handMade, null, 2), "utf8");

    const run = await runWire(fake, homeDir, workingTree);
    const out = run.stdout + run.stderr;
    assert.equal(run.status, 0, `wire must exit 0. Full output:\n${out}`);

    const after = readUserQwenSettings(homeDir);
    // No petbox entry snuck into the user's own mcpServers map...
    assert.deepEqual(
      after.mcpServers,
      handMade.mcpServers,
      `the kit must neither add petbox to nor otherwise touch a user-scope mcpServers map. Got:\n${JSON.stringify(after.mcpServers, null, 2)}`,
    );
    // ...and the hand-tuned layout the kit stopped writing is still there, key by key.
    assert.deepEqual(after.modelProviders, handMade.modelProviders);
    assert.deepEqual(after.model, handMade.model);
    assert.deepEqual(after.agents, handMade.agents);
    assert.deepEqual(after.security.outboundCorrelation, handMade.security.outboundCorrelation);
    // The ONE key in this fixture the kit is still entitled to change (qwen-spec.md §7).
    assert.equal(after.security.auth.selectedType, "openai");
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(workingTree, { recursive: true, force: true });
  }
});

test("a stale user-scope mcpServers.petbox left by an older kit is not refreshed or repointed by a new wire run", async () => {
  // Deliberate pin of the NO-PRUNE half of the decision: the kit stops writing, it does not start
  // deleting. A user-scope entry is not the kit's to remove (it may be the owner's own, added by
  // hand for a reason); the one entry an older kit had left on the owner's box was removed once, by
  // hand, with a dated backup. What must NEVER happen again is a wire run re-pointing such an entry
  // at whatever project it happens to be wiring — that repointing IS the defect.
  const fake = await startFakeServer();
  const homeDir = freshDir("petbox-qwen-userscope-home-");
  const workingTree = makeGitWorkingTree(freshDir("petbox-qwen-userscope-proj-"));
  try {
    const stale = {
      mcpServers: {
        petbox: {
          httpUrl: "https://petbox.3po.su/mcp",
          headers: { "X-Api-Key": "${PETBOX_SOME_OTHER_PROJECT_API_KEY}" },
          timeout: 30000,
          trust: true,
          alwaysLoadTools: true,
        },
      },
    };
    mkdirSync(join(homeDir, ".qwen"), { recursive: true });
    writeFileSync(join(homeDir, ".qwen", "settings.json"), JSON.stringify(stale, null, 2), "utf8");

    const run = await runWire(fake, homeDir, workingTree);
    assert.equal(run.status, 0, `wire must exit 0. Full output:\n${run.stdout + run.stderr}`);

    const after = readUserQwenSettings(homeDir);
    assert.deepEqual(
      after.mcpServers,
      stale.mcpServers,
      `a wire run must leave a pre-existing user-scope petbox entry exactly as it found it — never repointed at this run's env var. Got:\n${JSON.stringify(after.mcpServers, null, 2)}`,
    );
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(workingTree, { recursive: true, force: true });
  }
});
