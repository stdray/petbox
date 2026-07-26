// Integration tests for doctor's skill-template drift check (bugs:
// skill-files-clobber-and-apply-skips item 3, builtin-definition-drifts-no-catchup item 3 — the
// one item both cards' verdicts named as the last thing left undone: `runDoctor` never looked at
// skills at all).
//
// Same spawn-based technique as doctor-definition.test.ts (wire.ts runs main() at module top
// level, so the CLI must be exercised as a real subprocess; spawn, not spawnSync, for the online
// cases so this process's event loop stays free to answer the fake server — see that file's
// comment on the self-deadlock spawnSync causes here).
//
// Run: node --test src/doctor-skill-drift.test.ts

import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import {
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { test } from "node:test";
import { DEFAULT_AGENT_DEFINITION, type AgentDefinition } from "./agent-definition.ts";
import { writeSkillFiles } from "./skill-files.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");
const TEMPLATES_ROOT = join(dirname(fileURLToPath(import.meta.url)), "templates");

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

// Fake server answering BOTH endpoints doctor needs online: the agent-def fetch (any path falls
// through to this) and GET /api/auth/validate (the workspace probe the skill check needs).
function startFakeServer(definition: AgentDefinition, workspace: string | undefined): Promise<{ baseUrl: string; close: () => Promise<void> }> {
  return new Promise((resolve) => {
    const server = createServer((req, res) => {
      if (req.url?.startsWith("/api/auth/validate")) {
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify(workspace !== undefined ? { workspace } : {}));
        return;
      }
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

function writeOnlineRegistry(homeDir: string, projectDir: string, project: string, baseUrl: string): void {
  const petboxDir = join(homeDir, ".petbox");
  mkdirSync(petboxDir, { recursive: true });
  const envVar = "PETBOX_DOCTOR_SKILL_TEST_API_KEY";
  writeFileSync(
    join(petboxDir, "projects.json"),
    JSON.stringify({ entries: [{ prefix: projectDir, project, envVar, baseUrl }] }, null, 2),
    "utf8",
  );
  writeFileSync(join(petboxDir, "keys.json"), JSON.stringify({ [envVar]: "fake-key-value" }, null, 2), "utf8");
}

test("doctor --offline skips the skill check without touching the exit code", () => {
  const homeDir = freshDir("petbox-doctor-skill-home-");
  const projectDir = freshDir("petbox-doctor-skill-proj-");
  try {
    const { stdout, stderr, status } = runDoctor(projectDir, homeDir, ["--offline"]);
    const out = stdout + stderr;
    assert.match(out, /skill check skipped \(--offline\)/, `Full output:\n${out}`);
    assert.equal(status, 0);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor with no registered project skips the skill check, names why, exit 0", () => {
  const homeDir = freshDir("petbox-doctor-skill-home-");
  const projectDir = freshDir("petbox-doctor-skill-proj-");
  try {
    // No ~/.petbox/projects.json entry for projectDir at all.
    const { stdout, stderr, status } = runDoctor(projectDir, homeDir, []);
    const out = stdout + stderr;
    assert.match(out, /skill check skipped \(.*not a registered project/, `Full output:\n${out}`);
    assert.equal(status, 0);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor (online, server never reports a workspace) skips the skill check by name, exit 0", async () => {
  const homeDir = freshDir("petbox-doctor-skill-home-");
  const projectDir = freshDir("petbox-doctor-skill-proj-");
  const fake = await startFakeServer(DEFAULT_AGENT_DEFINITION, undefined);
  try {
    writeOnlineRegistry(homeDir, projectDir, "doctor-skill-noworkspace-proj", fake.baseUrl);
    const { stdout, stderr, status } = await runDoctorOnline(projectDir, homeDir);
    const out = stdout + stderr;
    assert.match(out, /skill check skipped \(the server responded but did not report a workspace/, `Full output:\n${out}`);
    assert.equal(status, 0);
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor (online, workspace resolved) reports NO drift when every materialized skill matches its template", async () => {
  const homeDir = freshDir("petbox-doctor-skill-home-");
  const projectDir = freshDir("petbox-doctor-skill-proj-");
  const project = "doctor-skill-clean-proj";
  const workspace = "doctor-skill-ws";
  const fake = await startFakeServer(DEFAULT_AGENT_DEFINITION, workspace);
  try {
    writeOnlineRegistry(homeDir, projectDir, project, fake.baseUrl);
    writeSkillFiles(projectDir, TEMPLATES_ROOT, project, workspace);

    const { stdout, stderr, status } = await runDoctorOnline(projectDir, homeDir);
    const out = stdout + stderr;
    assert.match(
      out,
      /skill files — every materialized copy matches its current template, no foreign files/,
      `Full output:\n${out}`,
    );
    assert.doesNotMatch(out, /BLOCKED/);
    assert.doesNotMatch(out, /DRIFTED/);
    assert.equal(status, 0);
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor (online) names a foreign (BLOCKED) skill file distinctly from one that DRIFTED from the template, exit code unaffected", async () => {
  const homeDir = freshDir("petbox-doctor-skill-home-");
  const projectDir = freshDir("petbox-doctor-skill-proj-");
  const project = "doctor-skill-mixed-proj";
  const workspace = "doctor-skill-ws";
  const fake = await startFakeServer(DEFAULT_AGENT_DEFINITION, workspace);
  try {
    writeOnlineRegistry(homeDir, projectDir, project, fake.baseUrl);
    writeSkillFiles(projectDir, TEMPLATES_ROOT, project, workspace);

    // A real user file: no origin marker, unrelated content.
    const foreignPath = join(projectDir, ".claude", "skills", "petbox-agent-factory", "SKILL.md");
    writeFileSync(foreignPath, "# my own notes on the factory skill\n\nnot generated by wire\n", "utf8");

    // A drifted file: still carries the marker (still "ours"), but its body no longer matches
    // what the CURRENT template renders — simulates a template edit landing after materialization.
    const driftedPath = join(projectDir, ".factory", "skills", "petbox-methodology", "SKILL.md");
    const driftedOriginal = readFileSync(driftedPath, "utf8");
    writeFileSync(driftedPath, `${driftedOriginal}\n<!-- stale local copy -->\n`, "utf8");

    const { stdout, stderr, status } = await runDoctorOnline(projectDir, homeDir);
    const out = stdout + stderr;

    assert.match(
      out,
      /skill files — 1 foreign \(BLOCKED\) file\(s\), not ours to fix:/,
      `Full output:\n${out}`,
    );
    assert.ok(out.includes(foreignPath), `expected the foreign path (${foreignPath}) named. Full output:\n${out}`);
    assert.match(
      out,
      /skill files — 1 file\(s\) drifted from the current template \(run `petbox-wire apply` to refresh\):/,
      `Full output:\n${out}`,
    );
    assert.match(out, /BLOCKED — a foreign \(non-PetBox\) file sits here/, `Full output:\n${out}`);
    assert.match(out, /DRIFTED from the current template/, `Full output:\n${out}`);
    // Informational only — same policy as the definition drift check right above it in doctor's
    // output: neither a foreign skill file nor a drifted one is a truthfulness violation, so the
    // exit code must stay whatever the harness gate alone decides (0 here — DEFAULT is
    // truth-clean with no local bindings).
    assert.equal(status, 0);
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});
