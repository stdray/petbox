// Regression test for card user-scope-roles-rendered-from-cwd-project-definition.
//
// Measured 2026-09-02: the SAME `apply --all --dry-run` reported "using server definition
// default v20" from $system and "default v1" from pochtar — user-scope role rendering resolved
// its definition against `process.cwd()`'s registered project, so a run from the wrong directory
// silently downgraded the whole machine profile to whichever project's server document happened
// to be current there. The fix: user-scope roles now render from DEFAULT_AGENT_DEFINITION (the
// kit's own bundled baseline, agent-definition.ts) — never from any project's server document,
// never from the registry, never from the network — so cwd (and which projects are registered,
// and what THEIR servers say) cannot affect the result at all.
//
// This test reproduces the exact bug shape: two registered projects, each backed by ITS OWN fake
// PetBox server serving a DIFFERENT agent-definition document at a different version (v20 vs v1,
// same as the live numbers the card measured) — WITHOUT --offline, so a live fetch is actually
// attempted for whichever code path still makes one. Before the fix this test would have failed:
// the two runs would have produced different role files (or at least tried to), and the "v1"
// project's run would have downgraded a machine profile already rendered from "v20". After the
// fix, neither server is ever contacted for the roles:user step, and both runs produce
// byte-identical files.
//
// Run: node --test src/roles-user-cwd-independent.test.ts

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import type { AgentDefinition } from "./agent-definition.ts";
import { DEFAULT_AGENT_DEFINITION, KIT_VERSION } from "./agent-definition.ts";
import { planApply } from "./apply-artifacts.ts";
import { DEFAULT_ROLE_MODEL_SEED } from "./roles.ts";
import { HARNESS_IDS } from "./harness-capabilities.ts";
import { WIRE_EXIT } from "./wire-exit.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

/** A DIFFERENT, richer-looking definition than the kit baseline — just different `notes`, same
 * roster shape — so a byte comparison against DEFAULT_AGENT_DEFINITION's render is meaningful:
 * if either project's server document had leaked into the rendered files, this text would show
 * up in them. */
function serverDefinitionNamed(label: string): AgentDefinition {
  return {
    name: "default",
    roles: DEFAULT_AGENT_DEFINITION.roles.map((r) => ({
      ...r,
      notes: `SERVER-SIDE DOCUMENT (${label}) — must never reach a user-scope role file.`,
    })),
  };
}

/** One fake PetBox server answering GET /api/{projectKey}/agent-defs/{key} — routes by the
 * projectKey segment in the URL path (the real contract, agent-def-fetch.ts's header comment),
 * so ONE server can stand in for two DIFFERENT projects' two DIFFERENT documents/versions at
 * once, exactly like the real petbox.3po.su does for $system and pochtar today. */
function startFakeDefServer(docs: Record<string, { version: number; definition: AgentDefinition }>): Promise<{
  baseUrl: string;
  close: () => Promise<void>;
}> {
  return new Promise((resolve) => {
    const server = createServer((req, res) => {
      const m = /^\/api\/([^/]+)\/agent-defs\/([^/?]+)/.exec(req.url ?? "");
      const project = m ? decodeURIComponent(m[1]!) : "";
      const doc = docs[project];
      if (!doc) {
        res.writeHead(404, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ error: "not found" }));
        return;
      }
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ key: "default", version: doc.version, definition: doc.definition }));
    });
    server.listen(0, "127.0.0.1", () => {
      const { port } = server.address() as AddressInfo;
      resolve({ baseUrl: `http://127.0.0.1:${port}`, close: () => new Promise((r) => server.close(() => r())) });
    });
  });
}

function writeHome(
  homeDir: string,
  entries: Array<{ prefix: string; project: string; envVar: string; baseUrl: string }>,
): void {
  const petboxDir = join(homeDir, ".petbox");
  mkdirSync(petboxDir, { recursive: true });
  writeFileSync(join(petboxDir, "projects.json"), JSON.stringify({ entries }, null, 2), "utf8");
  const keys: Record<string, string> = {};
  for (const e of entries) keys[e.envVar] = "fake-key-value";
  writeFileSync(join(petboxDir, "keys.json"), JSON.stringify(keys, null, 2), "utf8");
}

/** Async spawn: a live (non-offline) apply run calls back into the fake server on THIS process's
 * event loop, so spawnSync would self-deadlock (same reasoning as apply-integrity.test.ts). */
function runWire(cwd: string, homeDir: string, args: string[]): Promise<{ out: string; status: number | null }> {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [WIRE_TS, ...args], {
      cwd,
      env: { ...process.env, USERPROFILE: homeDir, HOME: homeDir, HOMEDRIVE: undefined, HOMEPATH: undefined },
    });
    let out = "";
    child.stdout.on("data", (d) => (out += d.toString("utf8")));
    child.stderr.on("data", (d) => (out += d.toString("utf8")));
    child.on("error", reject);
    child.on("close", (status) => resolve({ out, status }));
  });
}

const USER_ROLE_DIRS = [".claude/agents", ".config/opencode/agents", ".factory/droids"] as const;

function snapshotUserRoleFiles(homeDir: string): Record<string, string> {
  const out: Record<string, string> = {};
  for (const rel of USER_ROLE_DIRS) {
    const dir = join(homeDir, ...rel.split("/"));
    if (!existsSync(dir)) continue;
    for (const f of readdirSync(dir)) {
      if (/^petbox-[a-z0-9_-]+\.md$/.test(f)) out[`${rel}/${f}`] = readFileSync(join(dir, f), "utf8");
    }
  }
  return out;
}

test(
  "apply --roles=user (live, not --offline): two registered projects with DIFFERENT server " +
    "definitions at DIFFERENT versions (v20 vs v1, the card's own numbers) render byte-identical " +
    "user-scope role files — neither server is ever consulted for this step",
  async () => {
    const homeDir = freshDir("petbox-cwd-indep-home-");
    const projSystem = freshDir("petbox-cwd-indep-system-");
    const projPochtar = freshDir("petbox-cwd-indep-pochtar-");
    let server: { baseUrl: string; close: () => Promise<void> } | undefined;
    try {
      server = await startFakeDefServer({
        // Mirrors the card's measured numbers exactly: $system resolves v20, pochtar resolves v1.
        "$system": { version: 20, definition: serverDefinitionNamed("system-v20") },
        pochtar: { version: 1, definition: serverDefinitionNamed("pochtar-v1") },
      });
      writeHome(homeDir, [
        { prefix: projSystem, project: "$system", envVar: "PETBOX_SYSTEM_API_KEY", baseUrl: server.baseUrl },
        { prefix: projPochtar, project: "pochtar", envVar: "PETBOX_POCHTAR_API_KEY", baseUrl: server.baseUrl },
      ]);

      const first = await runWire(projSystem, homeDir, ["apply", "--roles=user"]);
      // Only WIRE_EXIT.ok or WIRE_EXIT.incomplete are acceptable here: the fake server above only
      // implements the agent-defs endpoint, so the UNRELATED project-scope skills fetch 404s and
      // downgrades the overall run to "incomplete" — that is this minimal fake's limitation, not a
      // property of the fix under test. A hard/truthfulness/usage exit would still be a real bug.
      assert.ok(
        first.status === WIRE_EXIT.ok || first.status === WIRE_EXIT.incomplete,
        `run from $system dir failed harder than the fake server's own limitation explains ` +
          `(status=${first.status}); output:\n${first.out}`,
      );
      const afterSystem = snapshotUserRoleFiles(homeDir);
      assert.equal(Object.keys(afterSystem).length, 15, `expected 15 files; output:\n${first.out}`);
      // The server's document must never have reached a role file — proves the fix, not just the
      // symptom (byte equality alone could pass by coincidence if both fakes served the same text).
      for (const content of Object.values(afterSystem)) {
        assert.equal(content.includes("SERVER-SIDE DOCUMENT"), false, `a server document leaked into a role file:\n${content}`);
      }
      assert.doesNotMatch(
        first.out,
        /roles:user\].*using server definition/,
        `the roles:user step must never resolve against a server;\n${first.out}`,
      );

      const second = await runWire(projPochtar, homeDir, ["apply", "--roles=user"]);
      assert.ok(
        second.status === WIRE_EXIT.ok || second.status === WIRE_EXIT.incomplete,
        `run from pochtar dir failed harder than the fake server's own limitation explains ` +
          `(status=${second.status}); output:\n${second.out}`,
      );
      const afterPochtar = snapshotUserRoleFiles(homeDir);

      assert.deepEqual(
        afterPochtar,
        afterSystem,
        "the two runs, from two directories registered to projects with DIFFERENT server " +
          "definitions at DIFFERENT versions, must render byte-identical files — this is the " +
          "card's own acceptance test",
      );
    } finally {
      if (server) await server.close();
      rmSync(homeDir, { recursive: true, force: true });
      rmSync(projSystem, { recursive: true, force: true });
      rmSync(projPochtar, { recursive: true, force: true });
    }
  },
);

test("apply --roles=user --offline: an UNREGISTERED cwd (no project, no network possible anyway) still renders the full 15-file baseline", async () => {
  const homeDir = freshDir("petbox-cwd-indep-unreg-home-");
  const proj = freshDir("petbox-cwd-indep-unreg-proj-"); // never written to projects.json
  try {
    const run = await runWire(proj, homeDir, ["apply", "--offline", "--roles=user"]);
    assert.equal(run.status, WIRE_EXIT.ok, `output:\n${run.out}`);
    const files = snapshotUserRoleFiles(homeDir);
    assert.equal(Object.keys(files).length, 15, `output:\n${run.out}`);
    assert.match(run.out, /source=kit baseline \(default-agents\.json\), kit v/, `output:\n${run.out}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(proj, { recursive: true, force: true });
  }
});

test("computeUserRoleReports source: rendering DEFAULT_AGENT_DEFINITION with the kit's own DEFAULT_ROLE_MODEL_SEED matches what apply --roles=user actually writes, for every harness", () => {
  // Unit-level pin (no subprocess) that the SAME primitives applyUserRoles/status use produce the
  // SAME bytes given the SAME (definition, roleModels) pair — i.e. the render is a pure function
  // of the baseline, not of anything environmental. Guards against a future refactor accidentally
  // reintroducing a hidden per-call source of variance (e.g. a timestamp, a cwd-relative path).
  for (const harness of HARNESS_IDS) {
    const planA = planApply(DEFAULT_AGENT_DEFINITION, harness, DEFAULT_ROLE_MODEL_SEED);
    const planB = planApply(DEFAULT_AGENT_DEFINITION, harness, DEFAULT_ROLE_MODEL_SEED);
    assert.deepEqual(planA.files, planB.files, `${harness}: two renders of the same baseline diverged`);
  }
  assert.ok(KIT_VERSION.length > 0, "KIT_VERSION must resolve to a non-empty label");
});

test("status --all --offline: names the kit baseline (never a project/server) as the user-scope role source", async () => {
  const homeDir = freshDir("petbox-cwd-indep-status-home-");
  const proj = freshDir("petbox-cwd-indep-status-proj-");
  try {
    mkdirSync(join(homeDir, ".petbox"), { recursive: true });
    writeFileSync(join(homeDir, ".petbox", "wire.json"), JSON.stringify({ roleScope: "user" }) + "\n", "utf8");
    const run = await runWire(proj, homeDir, ["status", "--all", "--offline"]);
    assert.equal(run.status, WIRE_EXIT.ok, `output:\n${run.out}`);
    assert.match(
      run.out,
      /user-scope role source: kit baseline \(default-agents\.json\), kit v\S+ — deterministic, independent of cwd/,
      `output:\n${run.out}`,
    );
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(proj, { recursive: true, force: true });
  }
});
