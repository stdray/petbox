// END-TO-END for bug artifact-integrity-dangling-and-orphans, through the real CLI.
//
// The unit tests next door (artifact-integrity.test.ts) prove the two primitives. These prove
// the thing the card actually promises a person: run `apply`, and what is left on disk is
// honest. wire.ts runs main() at import time, so the only way to exercise apply's real argv
// path is to spawn it as a subprocess with a redirected HOME — the technique
// apply-unbound-refusal.test.ts and doctor-definition.test.ts already use.
//
// A fake PetBox stands in for GET /api/{project}/agent-defs/{key} (same shape as
// doctor-definition.test.ts's helper) because the orphan sweep is deliberately gated on an
// AUTHORITATIVE definition: a degraded resolve (LKG replica, or the kit's offline baseline
// after a network blip) legitimately holds FEWER roles than the project has, and sweeping
// against one would delete live roles' artifacts because the network hiccuped. `--offline`
// therefore cannot be used to test the sweep — and the last test here pins that refusal down
// so nobody "simplifies" the gate away.
//
// Run: node --test src/apply-integrity.test.ts

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import type { AgentDefinition } from "./agent-definition.ts";
import { agentFilesDir } from "./apply-artifacts.ts";
import { HARNESS_IDS } from "./harness-capabilities.ts";
import { PETBOX_MARKER_LINE } from "./origin-marker.ts";
import { WIRE_EXIT } from "./wire-exit.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

/**
 * A fake agent-defs endpoint whose served document can be SWAPPED between apply runs — that is
 * the whole mechanism under test: the roster shrinks, and the next apply must clean up after it.
 */
function startSwappableDefServer(initial: AgentDefinition): Promise<{
  baseUrl: string;
  serve: (d: AgentDefinition) => void;
  close: () => Promise<void>;
}> {
  return new Promise((resolve) => {
    let current = initial;
    let version = 1;
    const server = createServer((_req, res) => {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ key: "default", version, definition: current }));
    });
    server.listen(0, "127.0.0.1", () => {
      const { port } = server.address() as AddressInfo;
      resolve({
        baseUrl: `http://127.0.0.1:${port}`,
        serve: (d) => {
          current = d;
          version += 1;
        },
        close: () => new Promise((r) => server.close(() => r())),
      });
    });
  });
}

function writeHome(homeDir: string, projectDir: string, project: string, baseUrl: string): void {
  const petboxDir = join(homeDir, ".petbox");
  mkdirSync(petboxDir, { recursive: true });
  const envVar = "PETBOX_APPLY_INTEGRITY_TEST_API_KEY";
  writeFileSync(
    join(petboxDir, "projects.json"),
    JSON.stringify({ entries: [{ prefix: projectDir, project, envVar, baseUrl }] }, null, 2),
    "utf8",
  );
  writeFileSync(join(petboxDir, "keys.json"), JSON.stringify({ [envVar]: "fake-key-value" }, null, 2), "utf8");
  // Explicit bindings for every role x every harness: claude-code is a CLOSED model space, so an
  // unbound role there is a hard truthfulness refusal and nothing would be written at all.
  const roles = { worker: { model: "sonnet" }, reserve: { model: "fable" }, orchestrator: { model: "opus" } };
  writeFileSync(
    join(petboxDir, "roles.json"),
    JSON.stringify(
      {
        activeProfile: "default",
        profiles: {
          default: {
            agents: {
              "claude-code": { roles },
              opencode: { roles: { worker: { model: "inherit" }, reserve: { model: "inherit" }, orchestrator: { model: "inherit" } } },
              droid: { roles: { worker: { model: "inherit" }, reserve: { model: "inherit" }, orchestrator: { model: "inherit" } } },
            },
          },
        },
      },
      null,
      2,
    ),
    "utf8",
  );
}

/**
 * Async spawn, never spawnSync: the subprocess must call BACK into a server running on this
 * process's event loop, and spawnSync would block that loop for the child's whole lifetime —
 * a self-deadlock (doctor-definition.test.ts hit exactly this and documents it).
 */
function runApply(cwd: string, homeDir: string, args: string[] = []): Promise<{ out: string; status: number | null }> {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [WIRE_TS, "apply", ...args], {
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

function role(slug: string, extra: Record<string, unknown> = {}) {
  return { slug, tier: "worker", requiredCapabilities: [], notes: `notes for ${slug}`, ...extra };
}

const TWO_ROLES: AgentDefinition = {
  name: "integrity-test",
  roles: [role("worker"), role("reserve")] as AgentDefinition["roles"],
};
const ONE_ROLE: AgentDefinition = {
  name: "integrity-test",
  roles: [role("worker")] as AgentDefinition["roles"],
};

function artifactPaths(projectDir: string, base: string): string[] {
  return HARNESS_IDS.map((h) => join(projectDir, agentFilesDir(h), base));
}

test("apply removes the artifact of a role that left the definition — every harness, marker-gated", async () => {
  const homeDir = freshDir("petbox-integrity-home-");
  const projectDir = freshDir("petbox-integrity-proj-");
  const fake = await startSwappableDefServer(TWO_ROLES);
  try {
    writeHome(homeDir, projectDir, "integrity-proj", fake.baseUrl);

    // 1. Both roles exist → both artifacts land, on all three harnesses.
    // Exit 4 (incomplete), not 0: this fake PetBox serves agent-defs only, so apply's skill
    // refresh correctly reports itself skipped. Every ROLE was written — which is what these
    // tests are about — and asserting 4 keeps the skills path honest instead of masking it.
    const first = await runApply(projectDir, homeDir);
    assert.equal(first.status, WIRE_EXIT.incomplete, `setup apply must write every role. Output:\n${first.out}`);
    for (const p of [...artifactPaths(projectDir, "petbox-worker.md"), ...artifactPaths(projectDir, "petbox-reserve.md")]) {
      assert.ok(existsSync(p), `setup: ${p} was not written. Output:\n${first.out}`);
    }

    // 2. A user's OWN file lands in our namespace — no origin marker. It must survive.
    const foreign = join(projectDir, agentFilesDir("claude-code"), "petbox-mine.md");
    writeFileSync(foreign, "---\nname: mine\n---\n\nhand written\n", "utf8");
    const foreignBytes = readFileSync(foreign);

    // 3. The role is removed from the definition.
    fake.serve(ONE_ROLE);
    const second = await runApply(projectDir, homeDir);
    assert.equal(second.status, WIRE_EXIT.incomplete, `Output:\n${second.out}`);

    for (const p of artifactPaths(projectDir, "petbox-reserve.md")) {
      assert.ok(
        !existsSync(p),
        `${p} survived — removing a role from the definition is still physically impossible. Output:\n${second.out}`,
      );
    }
    for (const p of artifactPaths(projectDir, "petbox-worker.md")) {
      assert.ok(existsSync(p), `${p}: a live role's artifact was destroyed. Output:\n${second.out}`);
    }
    assert.match(second.out, /removed .*petbox-reserve\.md — its role is no longer in definition/);

    assert.ok(existsSync(foreign), `apply deleted a user's own file. Output:\n${second.out}`);
    assert.deepEqual(readFileSync(foreign), foreignBytes, "a foreign file was modified");
    assert.match(second.out, /left .*petbox-mine\.md in place — no role by that name/);
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("apply REFUSES a definition whose artifact would name a role that does not exist, and writes nothing", async () => {
  const homeDir = freshDir("petbox-integrity-home-");
  const projectDir = freshDir("petbox-integrity-proj-");
  const dangling: AgentDefinition = {
    name: "integrity-test",
    roles: [
      role("orchestrator", { escalation: { available: true, targets: ["reserve"] } }),
      role("worker"),
    ] as AgentDefinition["roles"],
  };
  const fake = await startSwappableDefServer(dangling);
  try {
    writeHome(homeDir, projectDir, "integrity-dangling-proj", fake.baseUrl);
    const { out, status } = await runApply(projectDir, homeDir);

    assert.equal(status, WIRE_EXIT.hard, `a dangling target must be a hard refusal, not a warning. Output:\n${out}`);
    assert.match(out, /E1 orchestrator\.escalation\.targets → "reserve"/, `Output:\n${out}`);
    assert.match(out, /Nothing was written/, `Output:\n${out}`);
    for (const h of HARNESS_IDS) {
      assert.ok(
        !existsSync(join(projectDir, agentFilesDir(h))),
        `${h}: artifacts were written despite the refusal — a half-written set where one file lies is worse than none. Output:\n${out}`,
      );
    }
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("a DEGRADED resolve never sweeps: --offline leaves a project-only role's artifact alone", async () => {
  // The destructive failure mode this gate exists for: the built-in baseline holds 5 roles, a
  // project's live definition may hold more. If a transient network failure let apply sweep
  // against the baseline, every project-specific role's artifact would be deleted by a blip.
  const homeDir = freshDir("petbox-integrity-home-");
  const projectDir = freshDir("petbox-integrity-proj-");
  try {
    const dir = join(projectDir, agentFilesDir("claude-code"));
    mkdirSync(dir, { recursive: true });
    const projectOnly = join(dir, "petbox-review.md");
    writeFileSync(projectOnly, `---\nname: petbox-review\n${PETBOX_MARKER_LINE}\n---\n\nours\n`, "utf8");

    const { out, status } = await runApply(projectDir, homeDir, ["--offline"]);
    assert.equal(status, WIRE_EXIT.ok, `Output:\n${out}`);
    assert.ok(
      existsSync(projectOnly),
      `a degraded (offline baseline) resolve deleted a role's artifact. Output:\n${out}`,
    );
    assert.match(out, /orphan sweep skipped — the definition came from 'default'/, `Output:\n${out}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("apply's summary line names the resolution path and version — the D18 stage-2 evidence", async () => {
  const homeDir = freshDir("petbox-integrity-home-");
  const projectDir = freshDir("petbox-integrity-proj-");
  const fake = await startSwappableDefServer(ONE_ROLE);
  try {
    writeHome(homeDir, projectDir, "integrity-source-proj", fake.baseUrl);
    const { out } = await runApply(projectDir, homeDir);
    assert.match(
      out,
      /apply: definition="integrity-test" source=server key=default v\d+, harnesses=/,
      `apply must state WHICH document it compiled and where it came from. Output:\n${out}`,
    );
  } finally {
    await fake.close();
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});
