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
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

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
    // The custom def's single worker role has no requiredCapabilities, so every known harness
    // passes trivially — doctor should exit clean.
    assert.equal(status, 0);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("doctor --offline with NO registry entry / NO LKG cache falls back to the built-in default (unchanged behavior)", () => {
  const homeDir = freshDir("petbox-doctor-home-");
  const projectDir = freshDir("petbox-doctor-proj-");
  try {
    // No ~/.petbox at all — doctor must still work offline against the built-in default.
    const { stdout, stderr, status } = runDoctor(projectDir, homeDir, ["--offline"]);
    const out = stdout + stderr;
    assert.match(out, /definition="default"/, `expected the built-in default when nothing is registered. Full output:\n${out}`);
    assert.match(out, /offline default definition/);
    assert.equal(status, 0);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});
