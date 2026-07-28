// Regression test for apply-exit-race-libuv: `runApply` (wire.ts) used to end with a hard
// `process.exit(result.code)` right after `performApply`, which — whenever apply actually reaches
// the server (definition fetch + the skills workspace probe, both live network round-trips) —
// races Windows' async-handle teardown for whatever socket is still closing:
//   Assertion failed: !(handle->flags & UV_HANDLE_CLOSING), file src\win\async.c, line 76
// The caller then observes exit 127 ("command not found" to most automation) instead of the
// WIRE_EXIT code apply's own stderr message just named. This is the third instance of the same
// defect; `doctor` (wire.ts, see its two exit points above runApply) and `status` (status.ts:
// ~500-503) were already fixed with the same pattern: `process.exitCode = …; unrefLingeringHandles();
// return;` instead of a hard exit. This test applies the identical fix to `apply` and asserts the
// OBSERVABLE EXIT CODE after a clobber refusal — the exact failure mode the bug report describes —
// is 1 (WIRE_EXIT.hard), never 127 and never 0.
//
// wire.ts runs main() at import time (see its own comment on why testable logic lives in side
// modules), so the only way to exercise `apply`'s actual argv/behavior end-to-end is to spawn it
// as a real subprocess. To reproduce the SAME network shape that turned the race from latent to
// reproducible (two sequential live fetches: agent-def resolve, then the workspace probe), this
// spins up a real local HTTP server standing in for PetBox and registers the project against it —
// same technique doctor-definition.test.ts's online tests use, and the same one whose comment in
// skill-files.ts's probeWorkspace documents actually reproducing the UV_HANDLE_CLOSING assertion
// on this machine. spawn (async), not spawnSync, so this process's event loop stays free to accept
// the child's callback into the fake server (spawnSync would self-deadlock — see
// doctor-definition.test.ts's runDoctorOnline comment).
//
// Run: node --test src/apply-exit-race-libuv.test.ts

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { WIRE_EXIT } from "./wire-exit.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

// Minimal but shape-valid single-role definition (mirrors doctor-definition.test.ts's
// makeCustomDefRecord) — no requiredCapabilities, so every harness passes the truthfulness gate
// trivially and the ONLY thing that can drive a non-zero exit here is the clobber refusal under
// test, not an unrelated policy block.
function startFakeServer(): Promise<{ baseUrl: string; close: () => Promise<void> }> {
  return new Promise((resolve) => {
    const server = createServer((req, res) => {
      const url = req.url ?? "";
      if (url.includes("/agent-defs/")) {
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(
          JSON.stringify({
            key: "default",
            version: 1,
            definition: {
              name: "apply-exit-race-test-def",
              roles: [{ slug: "worker", tier: "worker", requiredCapabilities: [] }],
            },
          }),
        );
        return;
      }
      if (url.includes("/api/auth/validate")) {
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ workspace: "apply-exit-race-ws" }));
        return;
      }
      res.writeHead(404, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ error: "not found" }));
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
  const envVar = "PETBOX_APPLY_EXIT_RACE_TEST_API_KEY";
  writeFileSync(
    join(petboxDir, "projects.json"),
    JSON.stringify({ entries: [{ prefix: projectDir, project, envVar, baseUrl }] }, null, 2),
    "utf8",
  );
  writeFileSync(join(petboxDir, "keys.json"), JSON.stringify({ [envVar]: "fake-key-value" }, null, 2), "utf8");
}

// Async spawn (see module comment) — runs `apply` (NOT --offline) against the fake server above,
// so it takes the exact two-live-fetch path (definition resolve + workspace probe) the bug report
// implicates.
function runApplyOnline(
  cwd: string,
  homeDir: string,
): Promise<{ stdout: string; stderr: string; status: number | null }> {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [WIRE_TS, "apply"], {
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

test("apply (online) exits 1 (not 127, not 0) after a clobber refusal — no hard process.exit race", async () => {
  const homeDir = freshDir("petbox-apply-race-home-");
  const projectDir = freshDir("petbox-apply-race-proj-");
  const fake = await startFakeServer();
  try {
    writeOnlineRegistry(homeDir, projectDir, "apply-race-test-proj", fake.baseUrl);

    // Seed a foreign (non-PetBox) file at exactly the path apply must write the sole "worker"
    // role's claude-code artifact — no origin marker, so writeArtifact refuses it (kind:
    // "blocked"), which is what sets clobberBlocked -> WIRE_EXIT.hard, independent of the
    // truthfulness axis.
    const clobberPath = join(projectDir, ".claude", "agents", "petbox-worker.md");
    mkdirSync(join(projectDir, ".claude", "agents"), { recursive: true });
    writeFileSync(clobberPath, "# not a petbox file\nsome real content the owner wrote by hand\n", "utf8");

    const { stdout, stderr, status } = await runApplyOnline(projectDir, homeDir);
    const out = stdout + stderr;

    // The regression itself: before the fix, a hard process.exit(1) racing socket teardown could
    // surface as 127 (or an abnormal/null status on a crash) instead of the code apply computed.
    assert.equal(
      status,
      WIRE_EXIT.hard,
      `expected exit ${WIRE_EXIT.hard} (clobber refusal, not the libuv-race 127 or a false 0). Full output:\n${out}`,
    );
    assert.notEqual(status, 127, `must never surface as 127 (the libuv-race symptom). Full output:\n${out}`);

    assert.match(out, /REFUSED to overwrite/, `expected the clobber-refusal message. Full output:\n${out}`);
    assert.match(out, /hard failure/, `expected apply to name this a hard failure. Full output:\n${out}`);

    // The foreign file itself must be untouched (writeArtifact's own contract) — confirms this
    // really is the clobber path, not some other hard failure.
    assert.equal(existsSync(clobberPath), true);
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});
