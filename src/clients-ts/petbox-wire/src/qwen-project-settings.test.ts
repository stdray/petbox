// Acceptance proof for card wire-qwen-project-settings-mcp-and-skills (two gaps of ONE file,
// caught live 09.09.2026 in D:\my\prj\petsonde):
//
//   1. `apply` never wrote `<project>/.qwen/settings.json` at all — only a full `wire` did. A
//      project wired by an older kit and kept current with `apply` therefore had no `.qwen`
//      directory: qwen fell back to `.mcp.json` (claude-code's format, which qwen does not
//      env-var-resolve), reported `needs authentication`, and exposed zero tools on a key that
//      was verified healthy against the live server.
//   2. Nothing ever pointed qwen at this project's skills. `SKILL_SURFACES` covers `.claude` and
//      `.factory` only; qwen reads foreign skill roots through `skills.directories`.
//
// The two traps this file pins, both measured against qwen 0.23.2's real SkillManager (the card's
// reconnaissance) and both silent failures if got wrong:
//
//   - The `skills.directories` entry MUST be absolute. qwen resolves a relative one against the
//     RUNTIME's cwd, not against the project the settings file belongs to, so a relative entry
//     points at a different directory every time qwen is launched from somewhere else.
//   - Entries from `skills.directories` always load at qwen's `user` LEVEL. The key must
//     therefore live in the PROJECT settings only: in `~/.qwen/settings.json` it would spill one
//     project's skills into every project on the machine. That is the last test below, and it is
//     a negative assertion on the user-scope file written by the very same `wire` run that writes
//     the project one — a pair, so it cannot pass by writing nothing anywhere.
//
// Merge, not clobber (regression class of commit 1655231a — `.mcp.json`/`opencode.json`
// regenerated whole, destroying foreign entries): qwen itself writes `$version` into this file,
// and a person may hold their own MCP servers and their own skill directories in it.
//
// wire.ts runs main() at module top level (see its own header), so `wire`/`apply` can only be
// exercised as real subprocesses — spawn (async), not spawnSync, so this process's event loop
// stays free to answer the fake server.
//
// Run: node --test src/qwen-project-settings.test.ts

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { isAbsolute, join } from "node:path";
import { test } from "node:test";
import { mergeQwenProjectSettings } from "./qwen-project-settings.ts";
import { makeGitWorkingTree } from "./test-git-tree.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");
const PROJECT = "qwen-project-settings-proj";
const ENV_VAR = "PETBOX_QWEN_PROJECT_SETTINGS_TEST_API_KEY";

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

type Fake = { readonly baseUrl: string; readonly close: () => Promise<void> };

function startFakeServer(): Promise<Fake> {
  const handler = (req: IncomingMessage, res: ServerResponse): void => {
    const url = req.url ?? "";
    if (url.includes("/api/auth/validate")) {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ project: PROJECT, workspace: "qwen-ps-ws" }));
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
        close: () => new Promise<void>((r) => server.close(() => r())),
      });
    });
  });
}

type Run = { readonly stdout: string; readonly stderr: string; readonly status: number | null };

function runWire(argv: readonly string[], cwd: string, homeDir: string, extraEnv: NodeJS.ProcessEnv = {}): Promise<Run> {
  return new Promise<Run>((resolve, reject) => {
    const child = spawn(process.execPath, [WIRE_TS, ...argv], {
      cwd,
      env: {
        ...process.env,
        // BOTH, plus the Windows pair: node's homedir() reads USERPROFILE on win32 and HOME
        // elsewhere, and a leaked real home would put this test's writes into the operator's own
        // ~/.qwen/settings.json — the exact file this card forbids touching.
        USERPROFILE: homeDir,
        HOME: homeDir,
        HOMEDRIVE: undefined,
        HOMEPATH: undefined,
        QWEN_HOME: undefined,
        ...extraEnv,
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

/** Register `projectDir` the way a prior `wire` would have, so `apply` can resolve envVar/baseUrl
 * without inventing a project identity of its own. */
function seedRegistry(homeDir: string, projectDir: string, baseUrl: string): void {
  const petbox = join(homeDir, ".petbox");
  mkdirSync(petbox, { recursive: true });
  writeFileSync(
    join(petbox, "projects.json"),
    JSON.stringify({ entries: [{ prefix: projectDir, project: PROJECT, envVar: ENV_VAR, baseUrl }] }, null, 2),
    "utf8",
  );
  writeFileSync(join(petbox, "keys.json"), JSON.stringify({ [ENV_VAR]: "fake-key-value" }, null, 2), "utf8");
}

function readJsonFile(path: string): Record<string, unknown> {
  const parsed: unknown = JSON.parse(readFileSync(path, "utf8"));
  assert.ok(parsed !== null && typeof parsed === "object" && !Array.isArray(parsed), `${path} is not a JSON object`);
  return parsed as Record<string, unknown>;
}

function asObject(value: unknown, what: string): Record<string, unknown> {
  assert.ok(value !== null && typeof value === "object" && !Array.isArray(value), `${what} is not an object`);
  return value as Record<string, unknown>;
}

// ---- acceptance 1 + 2: `apply` alone creates the file, with an ABSOLUTE skills pointer ---------

test("`apply` in a registered project creates .qwen/settings.json with mcpServers AND an absolute skills.directories", async () => {
  const fake = await startFakeServer();
  const homeDir = freshDir("petbox-qwen-ps-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-qwen-ps-proj-"));
  seedRegistry(homeDir, projectDir, fake.baseUrl);

  try {
    const settingsPath = join(projectDir, ".qwen", "settings.json");
    assert.equal(existsSync(settingsPath), false, "precondition: the fresh project has no .qwen at all");

    const run = await runWire(["apply", "--roles=project"], projectDir, homeDir);
    const out = run.stdout + run.stderr;
    assert.equal(run.status, 0, `apply must succeed. Full output:\n${out}`);

    assert.equal(
      existsSync(settingsPath),
      true,
      `apply must CREATE ${settingsPath} — this is the petsonde gap: only a full wire ever did. ` +
        `Full output:\n${out}`,
    );

    const settings = readJsonFile(settingsPath);
    const petbox = asObject(asObject(settings["mcpServers"], "mcpServers")["petbox"], "mcpServers.petbox");
    // The base url is wire.ts's DEFAULT_BASE_URL constant, exactly like the five configs
    // writeProjectFiles emits — deliberately NOT the registry entry's (see performApply's own
    // comment on why a sixth site with its own opinion would flip-flop against `wire`).
    assert.match(
      String(petbox["httpUrl"]),
      /^https:\/\/[^/]+\/mcp$/,
      "qwen needs `httpUrl` (not claude-code's `url`) pointing at the kit's own base url",
    );
    assert.deepEqual(
      petbox["headers"],
      { "X-Api-Key": `\${${ENV_VAR}}` },
      "the header must stay an ${ENV} placeholder — qwen's own settings loader resolves it",
    );
    assert.equal(petbox["trust"], true);
    assert.equal(petbox["alwaysLoadTools"], true);

    const directories = asObject(settings["skills"], "skills")["directories"];
    assert.ok(Array.isArray(directories), `skills.directories must be an array. Full output:\n${out}`);
    const expected = join(projectDir, ".claude", "skills");
    assert.deepEqual(directories, [expected]);
    assert.equal(
      isAbsolute(String(directories[0])),
      true,
      "a RELATIVE entry resolves against qwen's runtime cwd, not the project — measured trap #1",
    );

    // A second apply changes nothing: the union is a union, not an append.
    const before = readFileSync(settingsPath, "utf8");
    const run2 = await runWire(["apply", "--roles=project"], projectDir, homeDir);
    assert.equal(run2.status, 0, `second apply must succeed. Full output:\n${run2.stdout}${run2.stderr}`);
    assert.equal(
      readFileSync(settingsPath, "utf8"),
      before,
      "a repeat apply must leave .qwen/settings.json byte-identical (no duplicated directory entry)",
    );
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

// ---- acceptance 3: an existing .qwen/settings.json MERGES ---------------------------------------

test("`apply` merges into an existing .qwen/settings.json — foreign keys, servers and skill directories all survive", async () => {
  const fake = await startFakeServer();
  const homeDir = freshDir("petbox-qwen-ps-merge-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-qwen-ps-merge-proj-"));
  seedRegistry(homeDir, projectDir, fake.baseUrl);

  const settingsPath = join(projectDir, ".qwen", "settings.json");
  const foreignSkillsDir = join(projectDir, "vendor", "their-skills");
  mkdirSync(join(projectDir, ".qwen"), { recursive: true });
  writeFileSync(
    settingsPath,
    JSON.stringify(
      {
        // `$version` is qwen's OWN bookkeeping key — it is what a real file always carries, and
        // losing it is exactly the clobber shape commit 1655231a fixed for the other two configs.
        $version: 3,
        ui: { theme: "their-theme" },
        mcpServers: { theirServer: { httpUrl: "https://theirs.example/mcp" } },
        skills: { directories: [foreignSkillsDir] },
      },
      null,
      2,
    ) + "\n",
    "utf8",
  );

  try {
    const run = await runWire(["apply", "--roles=project"], projectDir, homeDir);
    const out = run.stdout + run.stderr;
    assert.equal(run.status, 0, `apply must succeed. Full output:\n${out}`);

    const settings = readJsonFile(settingsPath);
    assert.equal(settings["$version"], 3, "qwen's own $version must survive");
    assert.deepEqual(settings["ui"], { theme: "their-theme" }, "an unrelated top-level key must survive");

    const servers = asObject(settings["mcpServers"], "mcpServers");
    assert.deepEqual(servers["theirServer"], { httpUrl: "https://theirs.example/mcp" }, "a foreign server must survive");
    assert.ok(asObject(servers["petbox"], "mcpServers.petbox")["httpUrl"], "ours must still be written");

    const directories = asObject(settings["skills"], "skills")["directories"];
    assert.ok(Array.isArray(directories));
    assert.deepEqual(
      directories,
      [foreignSkillsDir, join(projectDir, ".claude", "skills")],
      "skills.directories is a UNION — the project's own entry stays, ours is appended",
    );

    const before = readFileSync(settingsPath, "utf8");
    const run2 = await runWire(["apply", "--roles=project"], projectDir, homeDir);
    assert.equal(run2.status, 0, `second apply must succeed. Full output:\n${run2.stdout}${run2.stderr}`);
    assert.equal(readFileSync(settingsPath, "utf8"), before, "the merge must be byte-idempotent");
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

// ---- acceptance 4: the USER-scope qwen settings never gains skills.directories -------------------

test("a full `wire` writes skills.directories into the PROJECT settings and never into ~/.qwen/settings.json", async () => {
  const fake = await startFakeServer();
  const homeDir = freshDir("petbox-qwen-ps-user-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-qwen-ps-user-proj-"));

  try {
    const run = await runWire([projectDir, PROJECT, "--key", "fake-key-value"], projectDir, homeDir, {
      PETBOX_WIRE_TEST_LOOPBACK_BASE_URL: fake.baseUrl,
    });
    const out = run.stdout + run.stderr;
    assert.equal(run.status, 0, `full wire must succeed. Full output:\n${out}`);

    // The positive half of the pair — without it, "the user file has no skills key" would also
    // pass on a run that wrote the key nowhere at all.
    const projectSettings = readJsonFile(join(projectDir, ".qwen", "settings.json"));
    assert.deepEqual(asObject(projectSettings["skills"], "skills")["directories"], [
      join(projectDir, ".claude", "skills"),
    ]);

    const userSettingsPath = join(homeDir, ".qwen", "settings.json");
    assert.equal(existsSync(userSettingsPath), true, `wire must still write ${userSettingsPath}. Full output:\n${out}`);
    const userSettings = readJsonFile(userSettingsPath);
    assert.equal(
      "skills" in userSettings,
      false,
      `${userSettingsPath} must have NO "skills" key: qwen loads every skills.directories entry at ` +
        `USER level, so this project's skills would appear in every other project on the machine. ` +
        `Got: ${JSON.stringify(userSettings["skills"])}. Full output:\n${out}`,
    );
    // The user-scope MCP entry is a separate, deliberate thing and must be untouched by this.
    assert.ok(
      asObject(asObject(userSettings["mcpServers"], "mcpServers")["petbox"], "petbox")["httpUrl"],
      "the user-scope mcpServers.petbox entry must still be written",
    );
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

// ---- unit: the absolute-path rule is enforced at the primitive, not only at the call site -------

test("mergeQwenProjectSettings refuses a relative skills directory", () => {
  const dir = freshDir("petbox-qwen-ps-unit-");
  try {
    assert.throws(
      () =>
        mergeQwenProjectSettings({
          settingsPath: join(dir, ".qwen", "settings.json"),
          skillsDir: join(".claude", "skills"),
          mcpEntry: { httpUrl: "https://example/mcp" },
        }),
      /ABSOLUTE path/,
    );
    assert.equal(existsSync(join(dir, ".qwen", "settings.json")), false, "a refused merge writes nothing");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("mergeQwenProjectSettings leaves a malformed `skills` key alone and still writes the MCP entry", () => {
  const dir = freshDir("petbox-qwen-ps-malformed-");
  const settingsPath = join(dir, "settings.json");
  writeFileSync(settingsPath, JSON.stringify({ skills: true }, null, 2) + "\n", "utf8");
  try {
    const outcome = mergeQwenProjectSettings({
      settingsPath,
      skillsDir: join(dir, ".claude", "skills"),
      mcpEntry: { httpUrl: "https://example/mcp" },
    });
    assert.equal(outcome.reason, "own");
    assert.match(String(outcome.skillsWarning), /not an object/);
    const settings = readJsonFile(settingsPath);
    assert.equal(settings["skills"], true, "the malformed key is reported, never reinterpreted");
    assert.ok(asObject(settings["mcpServers"], "mcpServers")["petbox"], "the MCP half still lands");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("mergeQwenProjectSettings honours dryRun — outcome computed, nothing written", () => {
  const dir = freshDir("petbox-qwen-ps-dry-");
  const settingsPath = join(dir, ".qwen", "settings.json");
  try {
    const outcome = mergeQwenProjectSettings({
      settingsPath,
      skillsDir: join(dir, ".claude", "skills"),
      mcpEntry: { httpUrl: "https://example/mcp" },
      dryRun: true,
    });
    assert.equal(outcome.reason, "new");
    assert.equal(existsSync(settingsPath), false);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
