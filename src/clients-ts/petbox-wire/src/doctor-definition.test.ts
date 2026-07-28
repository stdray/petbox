// Integration test for doctor gating the SAME definition apply would compile
// (bug: doctor-gates-wrong-definition).
//
// wire.ts runs main() at import time (see its own comment on why testable logic lives in side
// modules), so the only way to exercise `doctor`'s actual argv/behavior end-to-end is to spawn
// it as a real subprocess — same technique CI itself uses (`node src/wire.ts <args>`). This test
// seeds a fake ~/.petbox (via USERPROFILE/HOME redirection) with:
//   - a registry entry for a throwaway project directory (identity)
//   - an API key in the key store for that project's env var (resolveProject requires one)
//   - an LKG agent-definition cache carrying a definition whose name is NOT "default" (the
//     built-in DEFAULT_AGENT_DEFINITION.name), so the two are trivially distinguishable
//
// Before the fix, `doctor` ignored all of this and always gated the hard-coded
// DEFAULT_AGENT_DEFINITION (name "default") — so its output would show `definition="default"`
// even with a differently-named LKG cache sitting right there, identical to what `apply
// --offline` would use. After the fix, `doctor --offline` must report the LKG-cached name.
//
// Run: node --test src/doctor-definition.test.ts

import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { DEFAULT_AGENT_DEFINITION, type AgentDefinition } from "./agent-definition.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

// A minimal but shape-valid AgentDefinition + cache envelope (agent-def-fetch.ts's
// AgentDefCacheRecord / parseAgentDefinitionResponse contract).
function makeCustomDefRecord(name: string) {
  return {
    key: "default",
    version: 7,
    fetchedAt: new Date().toISOString(),
    definition: {
      name,
      roles: [
        {
          slug: "worker",
          tier: "worker",
          requiredCapabilities: [],
        },
      ],
    },
  };
}

function runDoctor(cwd: string, homeDir: string, extraArgs: string[] = []): { stdout: string; stderr: string; status: number | null } {
  const res = spawnSync(process.execPath, [WIRE_TS, "doctor", ...extraArgs], {
    cwd,
    encoding: "utf8",
    env: {
      ...process.env,
      // Windows resolves homedir() from USERPROFILE; POSIX from HOME. Set both so the test is
      // portable across the dev machine (win32) and any Linux CI runner.
      USERPROFILE: homeDir,
      HOME: homeDir,
      HOMEDRIVE: undefined,
      HOMEPATH: undefined,
    },
  });
  return { stdout: res.stdout ?? "", stderr: res.stderr ?? "", status: res.status };
}

// Async variant for the two online-drift tests below: they run an in-PROCESS fake HTTP server
// that the spawned doctor subprocess must call back into. spawnSync would BLOCK this process's
// event loop for the whole subprocess lifetime, starving that very server of the chance to accept
// the connection — a self-deadlock (observed empirically: every online run fell through to the
// built-in default after exactly the AGENT_DEF_FETCH_TIMEOUT_MS abort, never reaching the fake
// server's request handler). spawn (async) keeps this process's event loop free while waiting.
function runDoctorOnline(
  cwd: string,
  homeDir: string,
  extraArgs: string[] = [],
): Promise<{ stdout: string; stderr: string; status: number | null }> {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [WIRE_TS, "doctor", ...extraArgs], {
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

test("doctor --offline gates the LKG-cached definition, not the hard-coded built-in default", () => {
  const homeDir = freshDir("petbox-doctor-home-");
  const projectDir = freshDir("petbox-doctor-proj-");
  try {
    const petboxDir = join(homeDir, ".petbox");
    mkdirSync(petboxDir, { recursive: true });
    mkdirSync(join(petboxDir, "cache"), { recursive: true });

    const envVar = "PETBOX_DOCTOR_TEST_API_KEY";
    writeFileSync(
      join(petboxDir, "projects.json"),
      JSON.stringify({ entries: [{ prefix: projectDir, project: "doctor-test-proj", envVar }] }, null, 2),
      "utf8",
    );
    writeFileSync(join(petboxDir, "keys.json"), JSON.stringify({ [envVar]: "fake-key-value" }, null, 2), "utf8");
    writeFileSync(
      join(petboxDir, "cache", "doctor-test-proj.agent-def.json"),
      JSON.stringify(makeCustomDefRecord("custom-lkg-def"), null, 2),
      "utf8",
    );

    const { stdout, stderr, status } = runDoctor(projectDir, homeDir, ["--offline"]);
    const out = stdout + stderr;

    assert.match(
      out,
      /definition="custom-lkg-def"/,
      `doctor must gate the LKG-cached definition (custom-lkg-def), not the built-in default. Full output:\n${out}`,
    );
    assert.doesNotMatch(
      out,
      /definition="default"/,
      `doctor must NOT fall back to the built-in DEFAULT_AGENT_DEFINITION when an LKG cache exists. Full output:\n${out}`,
    );
    assert.match(out, /using LKG definition/, "doctor must name which link it resolved (LKG here)");
    // --offline is a DELIBERATE skip, never indistinguishable from a genuine failure (bug:
    // doctor-reports-answering-server-unreachable) — the built-in-vs-live drift check
    // (bug: builtin-definition-drifts-no-catchup) has nothing live to compare against here,
    // and that must be a clean, honestly-labeled skip, never a failure.
    assert.match(
      out,
      /drift check skipped \(--offline\)/,
      "an intentional --offline run must never be reported the same way as an unreachable server",
    );
    // Round 2 of the same bug: the VERY FIRST line (the LKG stale-marker banner, printed by
    // resolveApplyDefinition before doctor's own drift-check block even runs) used to say
    // "PetBox unreachable" unconditionally for source:"lkg", never checking --offline. Sweep the
    // WHOLE output, not just the drift-check line — a fix that only touches one line while an
    // earlier line still lies is not a fix.
    assert.match(out, /--offline/, `the LKG banner itself must name --offline. Full output:\n${out}`);
    assert.doesNotMatch(
      out,
      /unreachable/i,
      `an intentional --offline run must never print "unreachable" anywhere in its output. Full output:\n${out}`,
    );
    assert.doesNotMatch(
      out,
      /could not reach/i,
      `an intentional --offline run must never print "could not reach" anywhere. Full output:\n${out}`,
    );
    // The custom def's single worker role has no requiredCapabilities, so every known harness
    // passes trivially — doctor should exit clean.
    assert.equal(status, 0);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor --offline, right after a run that JUST reached the live server (same LKG cache, same home): never says 'unreachable'/'could not reach' anywhere", async () => {
  // The exact live regression report: `doctor` (no flag) reaches the server, writes the LKG
  // cache; `doctor --offline` moments later, in the SAME home/registry, still described the
  // cache as "PetBox unreachable" — a lie, since the very same run history proves otherwise.
  const homeDir = freshDir("petbox-doctor-home-");
  const projectDir = freshDir("petbox-doctor-proj-");
  const fake = await startFakeAgentDefServer(DEFAULT_AGENT_DEFINITION);
  try {
    writeOnlineRegistry(homeDir, projectDir, "doctor-offline-after-online-proj", fake.baseUrl);

    // 1. Reach the live (fake) server for real — this writes the LKG cache.
    const online = await runDoctorOnline(projectDir, homeDir);
    assert.match(
      online.stdout + online.stderr,
      /using server definition/,
      "setup: the first run must actually reach the server and write the LKG cache",
    );

    // 2. Same home, same registry, --offline this time — no live fetch is attempted, but the
    // cache from step 1 exists, so this takes the "lkg" + offline path.
    const { stdout, stderr, status } = runDoctor(projectDir, homeDir, ["--offline"]);
    const out = stdout + stderr;

    assert.match(out, /using LKG definition/, `Full output:\n${out}`);
    assert.match(out, /--offline/, `the LKG banner must name --offline, not connectivity. Full output:\n${out}`);
    assert.doesNotMatch(
      out,
      /unreachable/i,
      `a server reached moments earlier in this SAME home must never be called unreachable just because this run passed --offline. Full output:\n${out}`,
    );
    assert.doesNotMatch(out, /could not reach/i, `Full output:\n${out}`);
    assert.equal(status, 0);
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor --offline with NO registry entry / NO LKG cache falls back to the built-in default, named as --offline (not 'no server'/'unreachable')", () => {
  const homeDir = freshDir("petbox-doctor-home-");
  const projectDir = freshDir("petbox-doctor-proj-");
  try {
    // No ~/.petbox at all — doctor must still work offline against the built-in default.
    const { stdout, stderr, status } = runDoctor(projectDir, homeDir, ["--offline"]);
    const out = stdout + stderr;
    assert.match(out, /definition="default"/, `expected the built-in default when nothing is registered. Full output:\n${out}`);
    assert.match(out, /--offline — using kit default baseline/, `Full output:\n${out}`);
    // --offline: a deliberate skip, never the "unreachable" wording (bug:
    // doctor-reports-answering-server-unreachable, both rounds).
    assert.match(out, /drift check skipped \(--offline\)/);
    assert.doesNotMatch(out, /unreachable/i, `Full output:\n${out}`);
    assert.doesNotMatch(out, /could not reach/i, `Full output:\n${out}`);
    assert.equal(status, 0);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

// ---- online drift check (bug: builtin-definition-drifts-no-catchup) -------------------------
//
// The two tests above never reach a server (--offline / no registry), so they can only prove the
// drift check SKIPS cleanly. These two spin up a throwaway HTTP server standing in for PetBox's
// GET /api/{project}/agent-defs/{key} so doctor actually takes the "source === server" branch and
// exercises diffAgentDefinitions (agent-definition.ts) for real, end to end through the CLI.

function startFakeAgentDefServer(definition: AgentDefinition): Promise<{ baseUrl: string; close: () => Promise<void> }> {
  return new Promise((resolve) => {
    const server = createServer((_req, res) => {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ key: "default", version: 10, definition }));
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

function writeOnlineRegistry(homeDir: string, projectDir: string, project: string, baseUrl: string): string {
  const petboxDir = join(homeDir, ".petbox");
  mkdirSync(petboxDir, { recursive: true });
  const envVar = "PETBOX_DOCTOR_ONLINE_TEST_API_KEY";
  writeFileSync(
    join(petboxDir, "projects.json"),
    JSON.stringify({ entries: [{ prefix: projectDir, project, envVar, baseUrl }] }, null, 2),
    "utf8",
  );
  writeFileSync(join(petboxDir, "keys.json"), JSON.stringify({ [envVar]: "fake-key-value" }, null, 2), "utf8");
  return envVar;
}

test("doctor (online) names built-in-vs-live drift by substance — role and rule count, not a raw diff", async () => {
  const homeDir = freshDir("petbox-doctor-home-");
  const projectDir = freshDir("petbox-doctor-proj-");
  const orchestrator = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === "orchestrator")!;
  // Recreate the exact historical shape this bug describes (fewer rules on the live side) by
  // dropping the last rule from a live-server copy of the orchestrator role, everything else
  // identical.
  const driftedOrchestrator = {
    ...orchestrator,
    notes: (orchestrator.notes ?? "").replace(/\n6\.\s[\s\S]*$/, ""),
  };
  const liveDefinition: AgentDefinition = {
    name: DEFAULT_AGENT_DEFINITION.name,
    roles: DEFAULT_AGENT_DEFINITION.roles.map((r) => (r.slug === "orchestrator" ? driftedOrchestrator : r)),
  };
  const fake = await startFakeAgentDefServer(liveDefinition);
  try {
    writeOnlineRegistry(homeDir, projectDir, "doctor-drift-proj", fake.baseUrl);
    const { stdout, stderr, status } = await runDoctorOnline(projectDir, homeDir);
    const out = stdout + stderr;

    assert.match(out, /using server definition/, `doctor must report a live server fetch. Full output:\n${out}`);
    assert.match(
      out,
      /built-in default definition has drifted from the live server definition/i,
      `doctor must call out the drift instead of staying silent. Full output:\n${out}`,
    );
    assert.match(
      out,
      /role 'orchestrator': built-in default has 6 rule\(s\), live definition has 5/,
      `drift message must name the role and the rule-count disagreement, human-readable — not a byte diff. Full output:\n${out}`,
    );
    // Drift is informational (definition-truthfulness is a separate axis) — it must never turn
    // into a truthfulness/hard-failure exit code on its own.
    assert.equal(status, 0);
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor (online) does not raise alarm when the live definition is richer than the built-in default", async () => {
  const homeDir = freshDir("petbox-doctor-home-");
  const projectDir = freshDir("petbox-doctor-proj-");
  // Live has every built-in role UNCHANGED plus one extra role the built-in doesn't know about —
  // this is the "kit is poorer than the server" case (bug:
  // doctor-drift-conflates-degradation-and-divergence), which is NORM (an offline compile just
  // ships without the extra role), not drift, and must never hit console.error / "drifted".
  // "worker-fixture-extra" is a fictitious slug chosen specifically because it is NOT one of
  // DEFAULT_AGENT_DEFINITION's roles (worker-highstakes used to serve as this example, back when
  // it was itself the live-only gap the built-in didn't know about — see the roster-vs-seed card
  // that added it to DEFAULT_AGENT_DEFINITION; reusing that slug here now would duplicate it).
  const liveDefinition: AgentDefinition = {
    name: DEFAULT_AGENT_DEFINITION.name,
    roles: [
      ...DEFAULT_AGENT_DEFINITION.roles,
      {
        slug: "worker-fixture-extra",
        tier: "worker",
        requiredCapabilities: [],
        notes: "1. one",
      },
    ],
  };
  const fake = await startFakeAgentDefServer(liveDefinition);
  try {
    writeOnlineRegistry(homeDir, projectDir, "doctor-degradation-proj", fake.baseUrl);
    const { stdout, stderr, status } = await runDoctorOnline(projectDir, homeDir);
    const out = stdout + stderr;

    assert.match(out, /using server definition/, `doctor must report a live server fetch. Full output:\n${out}`);
    assert.doesNotMatch(
      stderr,
      /drifted/i,
      `a live-only role is a degradation, not drift — must never hit console.error. Full stderr:\n${stderr}`,
    );
    assert.match(
      out,
      /missing 1 role\(s\) present in the live server definition — normal/,
      `doctor must name the degradation at info level. Full output:\n${out}`,
    );
    assert.match(out, /worker-fixture-extra/);
    assert.equal(status, 0);
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor (online) reports no drift when the live definition matches the built-in default", async () => {
  const homeDir = freshDir("petbox-doctor-home-");
  const projectDir = freshDir("petbox-doctor-proj-");
  const fake = await startFakeAgentDefServer(DEFAULT_AGENT_DEFINITION);
  try {
    writeOnlineRegistry(homeDir, projectDir, "doctor-nodrift-proj", fake.baseUrl);
    const { stdout, stderr, status } = await runDoctorOnline(projectDir, homeDir);
    const out = stdout + stderr;

    assert.match(out, /using server definition/, `doctor must report a live server fetch. Full output:\n${out}`);
    assert.match(
      out,
      /built-in default definition matches the live server definition — no drift/,
      `Full output:\n${out}`,
    );
    assert.equal(status, 0);
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

// ---- doctor never calls an answered server "unreachable" (bug:
// doctor-reports-answering-server-unreachable, same class as
// probe-collapses-http-errors-into-network) --------------------------------------------------
//
// Reproduced live 28.07: a fresh project's server answers 404 on GET /api/{project}/agent-defs/
// default (no server-side definition yet — normal), and doctor folded that into "drift check
// skipped (server unreachable)" — a straight lie, since the FIRST line of the very same run
// ("no server-side definition for this project yet") already proves the server responded.

function startFakeStatusServer(status: number, body: unknown): Promise<{ baseUrl: string; close: () => Promise<void> }> {
  return new Promise((resolve) => {
    const server = createServer((_req, res) => {
      res.writeHead(status, { "Content-Type": "application/json" });
      res.end(JSON.stringify(body));
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

test("doctor (online) 404 on agent-defs: drift check names 'nothing to compare', never 'unreachable'", async () => {
  const homeDir = freshDir("petbox-doctor-home-");
  const projectDir = freshDir("petbox-doctor-proj-");
  const fake = await startFakeStatusServer(404, { error: "not_found" });
  try {
    writeOnlineRegistry(homeDir, projectDir, "doctor-404-proj", fake.baseUrl);
    const { stdout, stderr, status } = await runDoctorOnline(projectDir, homeDir);
    const out = stdout + stderr;

    assert.match(out, /no server-side definition for this project yet/, `Full output:\n${out}`);
    assert.match(
      out,
      /drift check skipped \(server reachable, but this project has no server-side definition yet/,
      `Full output:\n${out}`,
    );
    assert.doesNotMatch(out, /unreachable/, `an answered 404 must never say "unreachable". Full output:\n${out}`);
    assert.equal(status, 0);
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor (online) 403 on agent-defs: drift check names the scope problem, never 'unreachable'", async () => {
  const homeDir = freshDir("petbox-doctor-home-");
  const projectDir = freshDir("petbox-doctor-proj-");
  const fake = await startFakeStatusServer(403, { error: "forbidden" });
  try {
    writeOnlineRegistry(homeDir, projectDir, "doctor-403-proj", fake.baseUrl);
    const { stdout, stderr, status } = await runDoctorOnline(projectDir, homeDir);
    const out = stdout + stderr;

    assert.match(out, /agents:read scope/, `Full output:\n${out}`);
    assert.doesNotMatch(out, /drift check skipped \(server unreachable\)/, `Full output:\n${out}`);
    assert.equal(status, 0);
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor (online) 503 on agent-defs: drift check names the HTTP status as self-recovering, never 'unreachable'", async () => {
  const homeDir = freshDir("petbox-doctor-home-");
  const projectDir = freshDir("petbox-doctor-proj-");
  const fake = await startFakeStatusServer(503, { error: "service_unavailable", reason: "deploy_in_progress", retryAfterSeconds: 60 });
  try {
    writeOnlineRegistry(homeDir, projectDir, "doctor-503-proj", fake.baseUrl);
    const { stdout, stderr, status } = await runDoctorOnline(projectDir, homeDir);
    const out = stdout + stderr;

    assert.match(out, /HTTP 503/, `Full output:\n${out}`);
    assert.match(out, /self-recovering/, `Full output:\n${out}`);
    assert.match(out, /retry in ~60s/, `Full output:\n${out}`);
    assert.doesNotMatch(out, /drift check skipped \(server unreachable\)/, `Full output:\n${out}`);
    assert.equal(status, 0);
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor (online) genuine connection failure (bad port, not --offline): drift check honestly says 'server unreachable'", async () => {
  const homeDir = freshDir("petbox-doctor-home-");
  const projectDir = freshDir("petbox-doctor-proj-");
  try {
    // Port 1 is reserved/unroutable — a real connection failure, never a --offline run, so the
    // ONLY correct label left is the honest one: the server never answered at all.
    writeOnlineRegistry(homeDir, projectDir, "doctor-deadhost-proj", "http://127.0.0.1:1");
    const { stdout, stderr, status } = await runDoctorOnline(projectDir, homeDir);
    const out = stdout + stderr;

    assert.match(out, /drift check skipped \(server unreachable\)/, `Full output:\n${out}`);
    assert.equal(status, 0);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});
