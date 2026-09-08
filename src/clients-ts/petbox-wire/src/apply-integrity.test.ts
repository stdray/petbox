// END-TO-END for bug artifact-integrity-dangling-and-orphans, through the real CLI.
//
// The unit tests next door (artifact-integrity.test.ts) prove the two primitives. These prove
// the thing the card actually promises a person: run `apply`, and what is left on disk is
// honest. wire.ts runs main() at import time, so the only way to exercise apply's real argv
// path is to spawn it as a subprocess with a redirected HOME — the technique
// apply-unbound-refusal.test.ts and doctor-layers.test.ts already use.
//
// There is NO fake PetBox in this file, and its absence is the point. These tests used to stand
// up an HTTP server for GET /api/{project}/agent-defs/{key}, because the orphan sweep was gated
// on an AUTHORITATIVE (server-sourced) definition: a degraded network resolve legitimately holds
// FEWER roles than the project has, and sweeping against one would delete live roles' artifacts
// because a socket hiccuped. Card wire-stops-fetching-definition removed the network leg
// entirely (definition-source.ts): the definition is now built from FILES — base < user <
// project — which either read or hard-refuse the whole run, so "this roster might be an
// accidental subset" is no longer a state apply can be in, and the sweep runs unconditionally.
// The roster is grown and shrunk here the way an operator really does it: by editing a layer.
//
// Run: node --test src/apply-integrity.test.ts

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { DEFAULT_AGENT_DEFINITION } from "./agent-definition.ts";
import { agentFilesDir } from "./apply-artifacts.ts";
import { HARNESS_IDS } from "./harness-capabilities.ts";
import { PETBOX_MARKER_LINE } from "./origin-marker.ts";
import { WIRE_EXIT } from "./wire-exit.ts";
import { makeGitWorkingTree } from "./test-git-tree.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

/**
 * A HOME with an EXPLICIT binding for every base role on every harness: claude-code is a CLOSED
 * model space, so an unbound role there is a hard truthfulness refusal and nothing would be
 * written at all. Read off DEFAULT_AGENT_DEFINITION rather than hardcoded, so a role added to
 * the baseline does not silently turn these tests into truthfulness-block tests.
 *
 * The project directory is deliberately NOT registered: apply's only remaining network call is
 * the workspace probe behind the skill refresh, and an unregistered directory is an INTENTIONAL
 * skip (exit stays 0). The definition resolve needs no registry entry and never did after the
 * cascade landed — which is itself worth pinning down.
 */
function writeHome(homeDir: string): void {
  const petboxDir = join(homeDir, ".petbox");
  mkdirSync(petboxDir, { recursive: true });
  const ccRoles: Record<string, { model: string }> = {};
  const openRoles: Record<string, { model: string }> = {};
  // `review` is not a base role — it is the one a layer ADDS below. Bound here too, so the
  // layer-added role is exercised for what these tests are about (artifact integrity) rather
  // than turning into a truthfulness-block test the moment it appears.
  for (const slug of [...DEFAULT_AGENT_DEFINITION.roles.map((r) => r.slug), "review"]) {
    ccRoles[slug] = { model: "sonnet" };
    openRoles[slug] = { model: "inherit" };
  }
  writeFileSync(
    join(petboxDir, "roles.json"),
    JSON.stringify(
      {
        activeProfile: "default",
        profiles: {
          default: {
            agents: {
              "claude-code": { roles: ccRoles },
              opencode: { roles: openRoles },
              droid: { roles: openRoles },
              codex: { roles: openRoles },
              qwen: { roles: openRoles },
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

/** Write `<root>/.petbox/agents` — the PROJECT layer, exactly where apply looks for it. */
function writeProjectLayer(root: string, files: Record<string, string>): string {
  const dir = join(root, ".petbox", "agents");
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, "layer.json"), JSON.stringify({ name: "project", mode: "overlay" }), "utf8");
  for (const [name, content] of Object.entries(files)) writeFileSync(join(dir, name), content, "utf8");
  return dir;
}

function runApply(cwd: string, homeDir: string, args: string[] = []): { out: string; status: number | null } {
  const res = spawnSync(process.execPath, [WIRE_TS, "apply", ...args], {
    cwd,
    encoding: "utf8",
    env: { ...process.env, USERPROFILE: homeDir, HOME: homeDir, HOMEDRIVE: undefined, HOMEPATH: undefined },
  });
  return { out: (res.stdout ?? "") + (res.stderr ?? ""), status: res.status };
}

// `base` is the petbox-<slug> stem, WITHOUT extension — every harness but codex emits `.md`,
// codex emits `.toml` (apply-artifacts.ts's renderCodexAgentToml).
function artifactPaths(projectDir: string, base: string): string[] {
  return HARNESS_IDS.map((h) => join(projectDir, agentFilesDir(h), `${base}.${h === "codex" ? "toml" : "md"}`));
}

test("a layer ADDS a role, then stops declaring it: the artifact appears and is then swept — every harness, marker-gated", async () => {
  const homeDir = freshDir("petbox-integrity-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-integrity-proj-"));
  try {
    writeHome(homeDir);

    // 1. A project layer adds a role of its own. It must be COMPLETE — nothing below defines it,
    //    so nothing below can supply its tier/capabilities (layer-cascade.ts's E3).
    const layerDir = writeProjectLayer(projectDir, {
      "petbox-review.json": JSON.stringify({ slug: "review", tier: "worker", requiredCapabilities: [] }),
      "petbox-review.md": "the project's own reviewer role",
    });
    const first = runApply(projectDir, homeDir);
    assert.equal(first.status, WIRE_EXIT.ok, `setup apply must write every role. Output:\n${first.out}`);
    for (const p of [...artifactPaths(projectDir, "petbox-worker"), ...artifactPaths(projectDir, "petbox-review")]) {
      assert.ok(existsSync(p), `setup: ${p} was not written. Output:\n${first.out}`);
    }

    // 2. A user's OWN file lands in our namespace — no origin marker. It must survive.
    const foreign = join(projectDir, agentFilesDir("claude-code"), "petbox-mine.md");
    writeFileSync(foreign, "---\nname: mine\n---\n\nhand written\n", "utf8");
    const foreignBytes = readFileSync(foreign);

    // 3. The layer stops declaring it. The roster shrinks; the artifacts must follow it down.
    rmSync(join(layerDir, "petbox-review.json"));
    rmSync(join(layerDir, "petbox-review.md"));
    const second = runApply(projectDir, homeDir);
    assert.equal(second.status, WIRE_EXIT.ok, `Output:\n${second.out}`);

    for (const p of artifactPaths(projectDir, "petbox-review")) {
      assert.ok(
        !existsSync(p),
        `${p} survived — removing a role from the definition is still physically impossible. Output:\n${second.out}`,
      );
    }
    for (const p of artifactPaths(projectDir, "petbox-worker")) {
      assert.ok(existsSync(p), `${p}: a live role's artifact was destroyed. Output:\n${second.out}`);
    }
    assert.match(second.out, /removed .*petbox-review\.md — its role is no longer in definition/);

    assert.ok(existsSync(foreign), `apply deleted a user's own file. Output:\n${second.out}`);
    assert.deepEqual(readFileSync(foreign), foreignBytes, "a foreign file was modified");
    assert.match(second.out, /left .*petbox-mine\.md in place — no role by that name/);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("apply REFUSES when the cascade leaves a rendered escalation target dangling, and writes nothing", async () => {
  const homeDir = freshDir("petbox-integrity-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-integrity-proj-"));
  try {
    writeHome(homeDir);
    // The baseline's orchestrator escalates to `reserve`. Tombstoning `reserve` in a layer is
    // exactly how a dangling target is born — and E1 is a property of the RESOLVED document, so
    // the cascade reports it before a single file is touched.
    writeProjectLayer(projectDir, {
      "petbox-reserve.json": JSON.stringify({ slug: "reserve", removed: true, reason: "test" }),
    });
    const { out, status } = runApply(projectDir, homeDir);

    assert.equal(status, WIRE_EXIT.hard, `a dangling target must be a hard refusal, not a warning. Output:\n${out}`);
    assert.match(out, /E1 .*orchestrator\.escalation\.targets → "reserve"/, `Output:\n${out}`);
    assert.match(out, /Nothing was written/, `Output:\n${out}`);
    for (const h of HARNESS_IDS) {
      assert.ok(
        !existsSync(join(projectDir, agentFilesDir(h))),
        `${h}: artifacts were written despite the refusal — a half-written set where one file lies is worse than none. Output:\n${out}`,
      );
    }
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("the orphan sweep runs UNCONDITIONALLY — there is no 'degraded resolve' left to gate it on, and --offline changes nothing about it", async () => {
  // The gate this replaces (`resolved.source === "server"`) existed for a failure mode that no
  // longer has a mechanism: a network resolve returning fewer roles than the project has. After
  // the cascade landed, that condition became permanently FALSE — so keeping it would have
  // disabled the sweep forever, silently. `--offline` is asserted here on purpose: it no longer
  // has anything to do with the definition, so it must not change what the sweep does either.
  const homeDir = freshDir("petbox-integrity-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-integrity-proj-"));
  try {
    writeHome(homeDir);
    const dir = join(projectDir, agentFilesDir("claude-code"));
    mkdirSync(dir, { recursive: true });
    const ours = join(dir, "petbox-review.md");
    writeFileSync(ours, `---\nname: petbox-review\n${PETBOX_MARKER_LINE}\n---\n\nours\n`, "utf8");

    const { out, status } = runApply(projectDir, homeDir, ["--offline"]);
    assert.equal(status, WIRE_EXIT.ok, `Output:\n${out}`);
    assert.ok(
      !existsSync(ours),
      `an orphan carrying our own marker survived the sweep. Output:\n${out}`,
    );
    assert.match(out, /removed .*petbox-review\.md — its role is no longer in definition/, `Output:\n${out}`);
    assert.doesNotMatch(out, /orphan sweep skipped/, `Output:\n${out}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("apply's summary names the LAYERS and, per field, which layer supplied it — the D18 stage-2 evidence", async () => {
  const homeDir = freshDir("petbox-integrity-home-");
  const projectDir = makeGitWorkingTree(freshDir("petbox-integrity-proj-"));
  try {
    writeHome(homeDir);
    writeProjectLayer(projectDir, {
      "petbox-worker.md": "PROJECT-LAYER-WORKER-PROSE",
    });
    const { out } = runApply(projectDir, homeDir);

    // The layer line: which layers, in order, with their real paths.
    assert.match(
      out,
      /apply: definition="base < project" layers=2: base\[kit v[^\]]*\] .*default-agents\.json {2}< {2}project\[overlay\] /,
      `apply must state WHICH layers it compiled. Output:\n${out}`,
    );
    // The provenance block: a human must be able to see which layer gave which FIELD.
    assert.match(out, /per-field provenance/, `Output:\n${out}`);
    assert.match(
      out,
      /worker {2}tier=worker {2}provenance: .*tier=base.*notes=project/,
      `the provenance line must name the layer behind each field. Output:\n${out}`,
    );
    assert.match(
      out,
      /orchestrator {2}tier=orchestrator {2}provenance: tier=base/,
      `a role no layer touched must still be attributed to base. Output:\n${out}`,
    );
    // Nothing anywhere claims a server, a cache, or a version envelope.
    assert.doesNotMatch(out, /source=server|source=lkg|LKG/, `Output:\n${out}`);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});
