// Behavioral proof for bug wire-mcp-wipes-foreign-servers (observation
// wire-mcp-json-and-opencode-json-wipe-foreign-servers): the full `wire` command used to
// REGENERATE `.mcp.json` (claude-code) and `.opencode/opencode.json` (opencode) WHOLE on every
// run — silently destroying any foreign MCP server, and any unrelated top-level setting (e.g.
// opencode's `theme`), that a person had added themselves. The three other MCP-config write sites
// (`.factory/mcp.json`, `.codex/config.toml`, `.qwen/settings.json`) already merged instead of
// clobbering; the fix reuses that same primitive (mergeMcpServer in wire.ts, and a matching
// findBlock-based check for codex's TOML block) for the two broken files, and adds a name-conflict
// warning at all five sites — a pre-existing entry named `petbox` with different content is now
// announced (file path included) before it is overwritten, rather than replaced in silence.
//
// Seam: PETBOX_WIRE_TEST_LOOPBACK_BASE_URL, same as wire-full-exit-step11.test.ts (see its header
// for why the full-wire path is otherwise untestable). spawn (async), not spawnSync.
//
// Run: node --test src/wire-project-mcp-merge.test.ts

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
const PROJECT = "wire-mcp-merge-proj";

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

type Fake = { baseUrl: string; close: () => Promise<void> };

function startFakeServer(): Promise<Fake> {
  const handler = (req: IncomingMessage, res: ServerResponse): void => {
    const url = req.url ?? "";
    if (url.includes("/api/auth/validate")) {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ project: PROJECT, workspace: "mcp-merge-ws" }));
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

async function runWireOnce(
  fake: Fake,
  homeDir: string,
  projectDir: string,
): Promise<Run> {
  return new Promise<Run>((resolve, reject) => {
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

function readJsonFile(path: string): any {
  return JSON.parse(readFileSync(path, "utf8"));
}

// Seed every one of the five MCP-config write sites with a foreign entry PLUS an unrelated
// top-level setting (where the format allows one), so a single run proves all five at once.
function seedForeign(projectDir: string): void {
  mkdirSync(join(projectDir, ".opencode"), { recursive: true });
  mkdirSync(join(projectDir, ".factory"), { recursive: true });
  mkdirSync(join(projectDir, ".codex"), { recursive: true });
  mkdirSync(join(projectDir, ".qwen"), { recursive: true });

  writeFileSync(
    join(projectDir, ".mcp.json"),
    JSON.stringify(
      { mcpServers: { myOwnServer: { type: "stdio", command: "my-tool" } } },
      null,
      2,
    ) + "\n",
    "utf8",
  );

  writeFileSync(
    join(projectDir, ".opencode", "opencode.json"),
    JSON.stringify(
      {
        theme: "my-custom-theme",
        mcp: { myOcServer: { type: "local", command: ["my-oc-tool"] } },
      },
      null,
      2,
    ) + "\n",
    "utf8",
  );

  writeFileSync(
    join(projectDir, ".factory", "mcp.json"),
    JSON.stringify({ mcpServers: { teamServer: { type: "http", url: "https://team.example/mcp" } } }, null, 2) + "\n",
    "utf8",
  );

  writeFileSync(
    join(projectDir, ".codex", "config.toml"),
    'sandbox_mode = "workspace-write"\n\n[mcp_servers.teamServer]\nurl = "https://team.example/mcp"\n',
    "utf8",
  );

  writeFileSync(
    join(projectDir, ".qwen", "settings.json"),
    JSON.stringify(
      { someOtherSetting: true, mcpServers: { myQwenServer: { httpUrl: "https://qwen.example/mcp" } } },
      null,
      2,
    ) + "\n",
    "utf8",
  );
}

test("full `wire` merges .mcp.json and opencode.json instead of clobbering them, without breaking the three already-working merge sites", async () => {
  const fake = await startFakeServer();
  const homeDir = freshDir("petbox-mcp-merge-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-mcp-merge-proj-"));
  seedForeign(projectDir);

  try {
    const run1 = await runWireOnce(fake, homeDir, projectDir);
    const out1 = run1.stdout + run1.stderr;
    assert.equal(run1.status, 0, `first wire run must succeed. Full output:\n${out1}`);

    // ---- acceptance 1: foreign content in the two previously-clobbered files survives ----
    const mcpJson1 = readJsonFile(join(projectDir, ".mcp.json"));
    assert.deepEqual(
      mcpJson1.mcpServers.myOwnServer,
      { type: "stdio", command: "my-tool" },
      `.mcp.json: foreign server myOwnServer must survive a full wire. Full output:\n${out1}`,
    );

    const oc1 = readJsonFile(join(projectDir, ".opencode", "opencode.json"));
    assert.equal(
      oc1.theme,
      "my-custom-theme",
      `opencode.json: foreign top-level setting 'theme' must survive a full wire. Full output:\n${out1}`,
    );
    assert.deepEqual(
      oc1.mcp.myOcServer,
      { type: "local", command: ["my-oc-tool"] },
      `opencode.json: foreign server myOcServer must survive a full wire. Full output:\n${out1}`,
    );

    // ---- acceptance 2: our own entry is written correctly ----
    assert.equal(mcpJson1.mcpServers.petbox.type, "http");
    assert.match(mcpJson1.mcpServers.petbox.url, /\/mcp$/);
    assert.equal(oc1.mcp.petbox.type, "remote");
    assert.match(oc1.mcp.petbox.url, /\/mcp$/);

    // ---- acceptance 4 (regression): the three already-working merge sites still merge ----
    const factory1 = readJsonFile(join(projectDir, ".factory", "mcp.json"));
    assert.deepEqual(
      factory1.mcpServers.teamServer,
      { type: "http", url: "https://team.example/mcp" },
      `.factory/mcp.json: foreign teamServer must still survive (regression). Full output:\n${out1}`,
    );
    assert.ok(factory1.mcpServers.petbox, `.factory/mcp.json: petbox entry must still be written. Full output:\n${out1}`);

    const codexToml1 = readFileSync(join(projectDir, ".codex", "config.toml"), "utf8");
    assert.match(
      codexToml1,
      /\[mcp_servers\.teamServer\]/,
      `.codex/config.toml: foreign [mcp_servers.teamServer] must still survive (regression). Full output:\n${out1}`,
    );
    assert.match(codexToml1, /sandbox_mode = "workspace-write"/, `.codex/config.toml: root scalar must survive. Full output:\n${out1}`);
    assert.match(codexToml1, /\[mcp_servers\.petbox\]/, `.codex/config.toml: petbox block must still be written. Full output:\n${out1}`);

    const qwen1 = readJsonFile(join(projectDir, ".qwen", "settings.json"));
    assert.equal(qwen1.someOtherSetting, true, `.qwen/settings.json: foreign setting must survive (regression). Full output:\n${out1}`);
    assert.deepEqual(
      qwen1.mcpServers.myQwenServer,
      { httpUrl: "https://qwen.example/mcp" },
      `.qwen/settings.json: foreign server must survive (regression). Full output:\n${out1}`,
    );
    assert.ok(qwen1.mcpServers.petbox, `.qwen/settings.json: petbox entry must still be written. Full output:\n${out1}`);

    // ---- acceptance 2b + 3-negative: a byte-identical re-run is idempotent and quiet ----
    const before = {
      mcp: readFileSync(join(projectDir, ".mcp.json"), "utf8"),
      oc: readFileSync(join(projectDir, ".opencode", "opencode.json"), "utf8"),
      factory: readFileSync(join(projectDir, ".factory", "mcp.json"), "utf8"),
      codex: readFileSync(join(projectDir, ".codex", "config.toml"), "utf8"),
      qwen: readFileSync(join(projectDir, ".qwen", "settings.json"), "utf8"),
    };
    const run2 = await runWireOnce(fake, homeDir, projectDir);
    const out2 = run2.stdout + run2.stderr;
    assert.equal(run2.status, 0, `second (idempotent) wire run must succeed. Full output:\n${out2}`);
    const after = {
      mcp: readFileSync(join(projectDir, ".mcp.json"), "utf8"),
      oc: readFileSync(join(projectDir, ".opencode", "opencode.json"), "utf8"),
      factory: readFileSync(join(projectDir, ".factory", "mcp.json"), "utf8"),
      codex: readFileSync(join(projectDir, ".codex", "config.toml"), "utf8"),
      qwen: readFileSync(join(projectDir, ".qwen", "settings.json"), "utf8"),
    };
    assert.equal(after.mcp, before.mcp, `.mcp.json must be byte-identical on a repeat run. Full output:\n${out2}`);
    assert.equal(after.oc, before.oc, `opencode.json must be byte-identical on a repeat run. Full output:\n${out2}`);
    assert.equal(after.factory, before.factory, `.factory/mcp.json must be byte-identical on a repeat run (regression). Full output:\n${out2}`);
    assert.equal(after.codex, before.codex, `.codex/config.toml must be byte-identical on a repeat run (regression). Full output:\n${out2}`);
    assert.equal(after.qwen, before.qwen, `.qwen/settings.json must be byte-identical on a repeat run (regression). Full output:\n${out2}`);
    assert.doesNotMatch(
      out2,
      /WARNING:.*already had a/,
      `an idempotent re-run must print no name-conflict warning. Full output:\n${out2}`,
    );
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("a foreign 'petbox'-named entry in any of the five configs warns, naming the file, before being overwritten", async () => {
  const fake = await startFakeServer();
  const homeDir = freshDir("petbox-mcp-conflict-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-mcp-conflict-proj-"));

  mkdirSync(join(projectDir, ".opencode"), { recursive: true });
  mkdirSync(join(projectDir, ".factory"), { recursive: true });
  mkdirSync(join(projectDir, ".codex"), { recursive: true });
  mkdirSync(join(projectDir, ".qwen"), { recursive: true });

  const mcpJsonPath = join(projectDir, ".mcp.json");
  const opencodeJsonPath = join(projectDir, ".opencode", "opencode.json");
  const factoryMcpPath = join(projectDir, ".factory", "mcp.json");
  const codexConfigPath = join(projectDir, ".codex", "config.toml");
  const qwenSettingsPath = join(projectDir, ".qwen", "settings.json");

  writeFileSync(mcpJsonPath, JSON.stringify({ mcpServers: { petbox: { type: "stdio", command: "not-petbox" } } }, null, 2) + "\n", "utf8");
  writeFileSync(
    opencodeJsonPath,
    JSON.stringify({ mcp: { petbox: { type: "local", command: ["not-petbox"] } } }, null, 2) + "\n",
    "utf8",
  );
  writeFileSync(factoryMcpPath, JSON.stringify({ mcpServers: { petbox: { type: "stdio", command: "not-petbox" } } }, null, 2) + "\n", "utf8");
  writeFileSync(codexConfigPath, '[mcp_servers.petbox]\nurl = "https://not-petbox.example/mcp"\n', "utf8");
  writeFileSync(qwenSettingsPath, JSON.stringify({ mcpServers: { petbox: { httpUrl: "https://not-petbox.example/mcp" } } }, null, 2) + "\n", "utf8");

  try {
    const run = await runWireOnce(fake, homeDir, projectDir);
    const out = run.stdout + run.stderr;
    assert.equal(run.status, 0, `wire run must still succeed despite the name conflicts. Full output:\n${out}`);

    for (const path of [mcpJsonPath, opencodeJsonPath, factoryMcpPath, codexConfigPath, qwenSettingsPath]) {
      assert.match(
        run.stderr,
        new RegExp(`WARNING:.*${path.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}.*already had`),
        `expected a name-conflict warning naming ${path}. Full stderr:\n${run.stderr}`,
      );
    }

    // The conflicting entries are still replaced with petbox's own config (a warning, not a
    // refusal — the observed behavior stays "petbox wins the name", only now announced).
    assert.equal(readJsonFile(mcpJsonPath).mcpServers.petbox.type, "http");
    assert.equal(readJsonFile(opencodeJsonPath).mcp.petbox.type, "remote");
    assert.ok(readJsonFile(factoryMcpPath).mcpServers.petbox.url.endsWith("/mcp"));
    assert.match(readFileSync(codexConfigPath, "utf8"), /startup_timeout_sec = 30/);
    assert.ok(readJsonFile(qwenSettingsPath).mcpServers.petbox.trust === true);
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});
