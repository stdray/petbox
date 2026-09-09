// End-to-end proof for task wire-codex-config-print-fragment (owner decision 09.09.2026,
// «и назад тоже. все унифицировать»): a REAL `wire` run, against a sandboxed $HOME, no longer
// writes codex's provider/model/catalog config — it PRINTS it — while everything the kit still
// owns (hooks, hook trust, MCP, role files) keeps landing.
//
// Acceptance criteria proved here (numbered on the card):
//   #1 — after `wire` on a temp $CODEX_HOME, `model_providers.*`, `model_provider`, `model`,
//        `http_headers` and `model_catalog_json` are byte-for-byte what they were before, and the
//        kit-owned `petbox-model-catalog.json` is no longer written at all.
//   #2 — hooks, the MCP entry and the codex role files are still there: no regression.
//   #4 — a rebinding via roles.json (what `model set --agent codex` writes) changes the PRINTED
//        text.
//   #5 — a role bound to a slug missing from the live catalog WARNS, naming apply_patch and the
//        window, and the whole fragment goes to stderr, loudly.
//
// Seam and harness shape are lifted verbatim from wire-qwen-fragment-first-run.test.ts — same
// loopback fake server, same sandboxed HOME/USERPROFILE, spawn (async) so this process stays free
// to answer the child.
//
// Run: node --test src/wire-codex-fragment.test.ts

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readdirSync,
  readFileSync,
  realpathSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { makeGitWorkingTree } from "./test-git-tree.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");
const PROJECT = "wire-codex-fragment-proj";

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

type Fake = { baseUrl: string; close: () => Promise<void> };

function startFakeServer(): Promise<Fake> {
  const handler = (req: IncomingMessage, res: ServerResponse): void => {
    const url = req.url ?? "";
    if (url.includes("/api/auth/validate")) {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ project: PROJECT, workspace: "codex-fragment-ws" }));
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
    const child = spawn(process.execPath, [WIRE_TS, projectDir, PROJECT, "--key", "fake-key-value"], {
      cwd: projectDir,
      env: {
        ...process.env,
        USERPROFILE: homeDir,
        HOME: homeDir,
        HOMEDRIVE: undefined,
        HOMEPATH: undefined,
        // Explicitly NOT set: $CODEX_HOME then resolves to <sandboxed home>/.codex, which is what
        // makes this a temp CODEX_HOME without a second seam.
        CODEX_HOME: undefined,
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

/** A hand-configured $CODEX_HOME/config.toml carrying DELIBERATELY non-kit values in every key the
 * kit used to overwrite — so "the kit left it alone" is provable by identity, not by the values
 * happening to coincide with what the kit would have written. */
const HAND_WRITTEN_KEYS = [
  `model_provider = "my-own-provider"`,
  `model = "my-own-model"`,
  `model_catalog_json = 'C:\\somewhere\\my-catalog.json'`,
  ``,
  `[model_providers.my-own-provider]`,
  `name = "Hand Written"`,
  `base_url = "https://example.invalid/v1"`,
  `env_key = "MY_OWN_KEY"`,
  `wire_api = "responses"`,
  `http_headers = { "x-opencode-session" = "hand-written-session-value" }`,
  ``,
];

function seedCodexHome(homeDir: string): string {
  const codexHome = join(homeDir, ".codex");
  mkdirSync(codexHome, { recursive: true });
  const configPath = join(codexHome, "config.toml");
  writeFileSync(configPath, HAND_WRITTEN_KEYS.join("\n"), "utf8");
  return codexHome;
}

test("acceptance #1: wire leaves every provider/model/header/catalog key in $CODEX_HOME/config.toml byte-for-byte, and writes no model catalog at all", async () => {
  const fake = await startFakeServer();
  const homeDir = freshDir("petbox-codex-frag-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-codex-frag-proj-"));
  try {
    const codexHome = seedCodexHome(homeDir);
    const configPath = join(codexHome, "config.toml");
    const before = readFileSync(configPath, "utf8");

    const run = await runWire(fake, homeDir, projectDir);
    const out = run.stdout + run.stderr;
    assert.equal(run.status, 0, `wire must exit 0. Full output:\n${out}`);

    const after = readFileSync(configPath, "utf8");
    // Every hand-written line survives verbatim, in order, as one contiguous run of text.
    assert.ok(
      after.startsWith(before),
      `the kit rewrote hand-written codex config keys.\n--- before ---\n${before}\n--- after ---\n${after}`,
    );
    // And what it DID add is only its own two artifacts.
    const added = after.slice(before.length);
    for (const line of added.split("\n")) {
      const t = line.trim();
      if (t === "") continue;
      assert.ok(
        /^\[(hooks\.state\.|projects\.)/.test(t) || /^(enabled|trusted_hash|trust_level) = /.test(t),
        `wire added a line to config.toml that is neither hook trust nor project trust: ${line}\n` +
          `--- added ---\n${added}`,
      );
    }
    // The catalog file the kit used to own is not written any more — it is printed instead.
    assert.equal(
      existsSync(join(codexHome, "petbox-model-catalog.json")),
      false,
      "petbox-model-catalog.json must no longer be written by the kit",
    );
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("acceptance #2: hooks, hook trust, the MCP entry and the codex role files all still land — no regression from the removal", async () => {
  const fake = await startFakeServer();
  const homeDir = freshDir("petbox-codex-frag-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-codex-frag-proj-"));
  try {
    const run = await runWire(fake, homeDir, projectDir);
    const out = run.stdout + run.stderr;
    assert.equal(run.status, 0, `wire must exit 0. Full output:\n${out}`);

    const codexHome = join(homeDir, ".codex");
    const hooks = JSON.parse(readFileSync(join(codexHome, "hooks.json"), "utf8"));
    for (const event of ["SessionStart", "SessionEnd", "PreToolUse"]) {
      assert.ok(Array.isArray(hooks.hooks?.[event]) && hooks.hooks[event].length > 0, `codex hook ${event} missing`);
    }
    const userConfig = readFileSync(join(codexHome, "config.toml"), "utf8");
    assert.match(userConfig, /\[hooks\.state\./, "hook trust entries missing from $CODEX_HOME/config.toml");
    assert.match(userConfig, /trust_level = "trusted"/, "project trust missing from $CODEX_HOME/config.toml");

    const projectConfig = readFileSync(join(projectDir, ".codex", "config.toml"), "utf8");
    assert.match(projectConfig, /\[mcp_servers\.petbox\]/, "project-scope MCP entry missing");

    // Role files. role-scope.ts renders codex roles into `<scope root>/.codex/agents`, and WHICH
    // root is the `roleScope` policy's business (project today, user after the flip that is in
    // flight in a sibling task) — this test asserts the files exist, deliberately not which
    // policy is in force.
    const roleDirs = [join(projectDir, ".codex", "agents"), join(homeDir, ".codex", "agents")];
    const found = roleDirs.filter((d) => existsSync(d) && readdirSync(d).some((f) => f.startsWith("petbox-")));
    assert.ok(found.length > 0, `no codex role files under ${roleDirs.join(" or ")}. Full output:\n${out}`);
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("acceptance #5: on a machine with no codex catalog the fragment is printed LOUDLY on stderr, naming apply_patch and the fallback window", async () => {
  const fake = await startFakeServer();
  const homeDir = freshDir("petbox-codex-frag-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-codex-frag-proj-"));
  try {
    const run = await runWire(fake, homeDir, projectDir);
    assert.equal(run.status, 0, `wire must exit 0. Full output:\n${run.stdout + run.stderr}`);
    // stderr, not the ordinary log stream — this is the "громкая печать" requirement.
    assert.match(run.stderr, /=== CODEX CONFIG FRAGMENT/);
    assert.match(run.stderr, /diverges from the kit's roster/);
    assert.match(run.stderr, /apply_patch/);
    assert.match(run.stderr, /272000/);
    // And it is a real, working catalog: freeform apply_patch and the measured window.
    assert.match(run.stderr, /"apply_patch_tool_type": "freeform"/);
    assert.match(run.stderr, /"context_window": 1048576/);
    assert.ok(!run.stderr.includes("128000"), "the retired kit-chosen 128000 must not be printed");
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("acceptance #4: a codex rebinding in roles.json changes the fragment the next wire run prints", async () => {
  const fake = await startFakeServer();
  const homeDir = freshDir("petbox-codex-frag-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-codex-frag-proj-"));
  try {
    const run1 = await runWire(fake, homeDir, projectDir);
    assert.equal(run1.status, 0, `first run must exit 0. Full output:\n${run1.stdout + run1.stderr}`);
    assert.match(run1.stderr, /model = "deepseek-v4-pro"/, "the kit's own seed should drive the first print");

    // Exactly what `petbox-wire model set orchestrator glm-5.3-flash --agent codex` persists.
    const rolesPath = join(homeDir, ".petbox", "roles.json");
    const roles = JSON.parse(readFileSync(rolesPath, "utf8"));
    roles.profiles.default.agents.codex.roles.orchestrator.model = "glm-5.3-flash";
    writeFileSync(rolesPath, JSON.stringify(roles, null, 2), "utf8");

    const run2 = await runWire(fake, homeDir, projectDir);
    assert.equal(run2.status, 0, `second run must exit 0. Full output:\n${run2.stdout + run2.stderr}`);
    assert.match(run2.stderr, /model = "glm-5.3-flash"/);
    assert.match(run2.stderr, /"slug": "glm-5\.3-flash"/);
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});
