// Integration tests for doctor's session-banner-budget check (card
// canon-write-gate-banner-budget) — a READ-time instrument, not the write gate the card
// originally asked for: it warns when either `source` leg (startup/resume) of the actual
// SessionStart assembly (buildProtocol + fetchCanonBlock + assembleSessionBanner, the SAME path
// pull-memory.ts runs — see status.ts's computeBannerBudgetLegs) is left with less than
// BANNER_BUDGET_WARN_FRACTION (5%) of SESSION_BANNER_BUDGET_BYTES margin. Never gates the exit
// code — same taxonomy as the definition-drift / skill-drift checks it sits next to in
// runDoctor: --offline / unregistered project / unreachable server all degrade to a named skip.
//
// Same spawn-based technique as doctor-skill-drift.test.ts (wire.ts runs main() at module top
// level, so the CLI must be exercised as a real subprocess; spawn, not spawnSync, for the online
// cases so this process's event loop stays free to answer the fake server).
//
// Run: node --test src/doctor-banner-budget.test.ts

import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { DEFAULT_AGENT_DEFINITION } from "./agent-definition.ts";
import { buildProtocol, mcpPetboxTool } from "./protocol.ts";
import { SESSION_BANNER_BUDGET_BYTES } from "./session-budget.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

function runDoctor(cwd: string, homeDir: string, extraArgs: string[] = []): { stdout: string; stderr: string; status: number | null } {
  const res = spawnSync(process.execPath, [WIRE_TS, "doctor", ...extraArgs], {
    cwd,
    encoding: "utf8",
    env: {
      ...process.env,
      USERPROFILE: homeDir,
      HOME: homeDir,
      HOMEDRIVE: undefined,
      HOMEPATH: undefined,
    },
  });
  return { stdout: res.stdout ?? "", stderr: res.stderr ?? "", status: res.status };
}

function runDoctorOnline(cwd: string, homeDir: string): Promise<{ stdout: string; stderr: string; status: number | null }> {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [WIRE_TS, "doctor"], {
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

// Fake server answering every endpoint doctor's checks touch: the agent-def fetch (any
// /api/*/agent-defs/* path), the canon fetch (/api/memory/{project}/canon — `canonBody` null
// skips it, i.e. a curated-empty leg), and /api/auth/validate (the skill check's workspace
// probe — answered so that check doesn't itself error and clutter the output).
function startFakeServer(canonBody: string | null): Promise<{ baseUrl: string; close: () => Promise<void> }> {
  return new Promise((resolve) => {
    const server = createServer((req, res) => {
      if (req.url?.startsWith("/api/auth/validate")) {
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ workspace: "doctor-banner-budget-ws" }));
        return;
      }
      if (req.url?.startsWith("/api/memory/")) {
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(
          JSON.stringify({
            project: canonBody === null ? null : { body: canonBody, version: 5 },
            workspace: null,
          }),
        );
        return;
      }
      // agent-defs
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ key: "default", version: 10, definition: DEFAULT_AGENT_DEFINITION }));
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

// A canon endpoint that always 500s (agent-defs still healthy) — simulates the server being up
// for one API but the canon route specifically failing/unreachable.
function startFakeServerWithBrokenCanon(): Promise<{ baseUrl: string; close: () => Promise<void> }> {
  return new Promise((resolve) => {
    const server = createServer((req, res) => {
      if (req.url?.startsWith("/api/auth/validate")) {
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ workspace: "doctor-banner-budget-ws" }));
        return;
      }
      if (req.url?.startsWith("/api/memory/")) {
        res.writeHead(500);
        res.end("boom");
        return;
      }
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ key: "default", version: 10, definition: DEFAULT_AGENT_DEFINITION }));
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

function writeOnlineRegistry(homeDir: string, projectDir: string, project: string, baseUrl: string): void {
  const petboxDir = join(homeDir, ".petbox");
  mkdirSync(petboxDir, { recursive: true });
  const envVar = "PETBOX_DOCTOR_BANNER_BUDGET_TEST_API_KEY";
  writeFileSync(
    join(petboxDir, "projects.json"),
    JSON.stringify({ entries: [{ prefix: projectDir, project, envVar, baseUrl }] }, null, 2),
    "utf8",
  );
  writeFileSync(join(petboxDir, "keys.json"), JSON.stringify({ [envVar]: "fake-key-value" }, null, 2), "utf8");
}

test("doctor --offline skips the banner-budget check without touching the exit code", () => {
  const homeDir = freshDir("petbox-doctor-banner-home-");
  const projectDir = freshDir("petbox-doctor-banner-proj-");
  try {
    const { stdout, stderr, status } = runDoctor(projectDir, homeDir, ["--offline"]);
    const out = stdout + stderr;
    assert.match(out, /banner-budget check skipped \(--offline\)/, `Full output:\n${out}`);
    assert.equal(status, 0);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor with no registered project skips the banner-budget check, names why, exit 0", () => {
  const homeDir = freshDir("petbox-doctor-banner-home-");
  const projectDir = freshDir("petbox-doctor-banner-proj-");
  try {
    const { stdout, stderr, status } = runDoctor(projectDir, homeDir, []);
    const out = stdout + stderr;
    assert.match(out, /banner-budget check skipped \(.*not a registered project/, `Full output:\n${out}`);
    assert.equal(status, 0);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor (online, canon endpoint unreachable/500) skips the banner-budget check by name, exit 0", async () => {
  const homeDir = freshDir("petbox-doctor-banner-home-");
  const projectDir = freshDir("petbox-doctor-banner-proj-");
  const fake = await startFakeServerWithBrokenCanon();
  try {
    writeOnlineRegistry(homeDir, projectDir, "doctor-banner-unreachable-proj", fake.baseUrl);
    const { stdout, stderr, status } = await runDoctorOnline(projectDir, homeDir);
    const out = stdout + stderr;
    assert.match(
      out,
      /banner-budget check skipped \(server did not answer GET \/api\/memory\/\{project\}\/canon\)/,
      `Full output:\n${out}`,
    );
    assert.equal(status, 0);
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor (online, tiny/no canon) reports a healthy margin on both sources, no warning, exit 0", async () => {
  const homeDir = freshDir("petbox-doctor-banner-home-");
  const projectDir = freshDir("petbox-doctor-banner-proj-");
  const fake = await startFakeServer("small curated pointer");
  try {
    writeOnlineRegistry(homeDir, projectDir, "doctor-banner-healthy-proj", fake.baseUrl);
    const { stdout, stderr, status } = await runDoctorOnline(projectDir, homeDir);
    const out = stdout + stderr;
    assert.match(
      out,
      /banner budget — every source keeps at least 5% margin/,
      `Full output:\n${out}`,
    );
    assert.doesNotMatch(out, /banner budget — \d+ of \d+ source\(s\) below/);
    assert.equal(status, 0);
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor (online, canon sized to leave a thin margin against resume's longer protocol) WARNS by name but does not change the exit code", async () => {
  const homeDir = freshDir("petbox-doctor-banner-home-");
  const projectDir = freshDir("petbox-doctor-banner-proj-");

  // Size the canon relative to the WORST-case (resume) protocol so the margin lands under the 5%
  // warn threshold regardless of future edits to protocol.ts's static prose — a hardcoded byte
  // count here would be exactly the fragile-constant trap this card's own history warns against.
  const resumeProtocol = buildProtocol("doctor-banner-thin-proj", mcpPetboxTool, {
    source: "resume",
    harness: "claude-code",
    definition: DEFAULT_AGENT_DEFINITION,
  });
  const resumeBytes = Buffer.byteLength(resumeProtocol, "utf8");
  const warnThreshold = Math.round(SESSION_BANNER_BUDGET_BYTES * 0.05);
  // Margin well inside the warn band (half the threshold) but still non-negative (canon still
  // included, not dropped) — exercises the "thin but not yet broken" amber zone specifically.
  const targetMargin = Math.floor(warnThreshold / 2);
  const canonBytes = SESSION_BANNER_BUDGET_BYTES - resumeBytes - 2 - targetMargin;
  assert.ok(canonBytes > 0, "test setup: canon size must be positive for this scenario to be meaningful");
  const canon = "C".repeat(canonBytes);

  const fake = await startFakeServer(canon);
  try {
    writeOnlineRegistry(homeDir, projectDir, "doctor-banner-thin-proj", fake.baseUrl);
    const { stdout, stderr, status } = await runDoctorOnline(projectDir, homeDir);
    const out = stdout + stderr;
    assert.match(
      out,
      /banner budget — \d+ of 2 source\(s\) below the 5% margin threshold/,
      `Full output:\n${out}`,
    );
    assert.match(out, /source=resume:/, `Full output:\n${out}`);
    assert.equal(status, 0, "banner-budget is informational only — must never change doctor's exit code");
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});
