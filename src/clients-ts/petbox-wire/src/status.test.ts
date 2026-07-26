// Tests for `petbox-wire status` (task: wire-status-command).
//
// Two layers:
//   1. Pure unit tests for status.ts's exported building blocks — fast, no subprocess, safe to
//      import directly (status.ts, unlike wire.ts, never runs main() at module top level).
//   2. CLI integration tests spawning `node wire.ts status --offline` with a redirected HOME
//      (same isolated-HOME technique as roles.test.ts / apply-unbound-refusal.test.ts), because
//      that is the only way to exercise the real per-role model-source ENUMERATION end to end:
//        - a HOME with an explicit ~/.petbox/roles.json binding -> "roster"
//        - a HOME with NO roles.json at all -> "seed" for claude-code/droid roles that
//          DEFAULT_ROLE_MODEL_SEED covers, "none" for opencode (never seeded)
//        - a HOME whose roles.json exists but is missing one role's binding -> "none", and the
//          printed line must carry a REMEDY (a `model set ...` command), never read as a blank.
//
// Run: node --test src/status.test.ts

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { DEFAULT_AGENT_DEFINITION } from "./agent-definition.ts";
import type { ResolvedAgentDefinition } from "./agent-def-fetch.ts";
import {
  checkSkillFile,
  computeRosterState,
  formatCanonLeg,
  formatDefinitionSource,
  formatRoleModelSource,
  formatRosterState,
  readArtifactState,
  resolveRoleModelSource,
  roleRelativePath,
} from "./status.ts";
import type { RolesFile } from "./roles.ts";
import { WIRE_EXIT } from "./wire-exit.ts";

const WIRE_TS = join(import.meta.dirname, "wire.ts");

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

const worker = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === "worker")!;

// ---- pure unit tests --------------------------------------------------------

test("roleRelativePath: namespaced petbox-<slug>.md under each harness's dir; droid sanitized", () => {
  assert.equal(roleRelativePath("claude-code", worker), ".claude/agents/petbox-worker.md");
  assert.equal(roleRelativePath("opencode", worker), ".opencode/agent/petbox-worker.md");
  assert.equal(roleRelativePath("droid", worker), ".factory/droids/petbox-worker.md");
});

test("resolveRoleModelSource: bound value always wins as 'roster', regardless of file existence", () => {
  const src = resolveRoleModelSource("worker", "claude-code", true, { worker: "opus" });
  assert.deepEqual(src, { kind: "roster", model: "opus" });
  // Even if the caller claims the file doesn't exist, an actual bound value is still roster —
  // resolveAgentRoles already read it from somewhere; resolveRoleModelSource trusts its input.
  const src2 = resolveRoleModelSource("worker", "claude-code", false, { worker: "opus" });
  assert.deepEqual(src2, { kind: "roster", model: "opus" });
});

test("resolveRoleModelSource: no roles.json -> 'seed' preview for claude-code (DEFAULT_ROLE_MODEL_SEED)", () => {
  assert.deepEqual(
    resolveRoleModelSource("orchestrator", "claude-code", false, {}),
    { kind: "seed", model: "opus" },
  );
  assert.deepEqual(resolveRoleModelSource("worker", "claude-code", false, {}), {
    kind: "seed",
    model: "sonnet",
  });
});

test("resolveRoleModelSource: no roles.json -> 'seed' inherit for droid (every seeded role)", () => {
  assert.deepEqual(resolveRoleModelSource("reserve", "droid", false, {}), {
    kind: "seed",
    model: "inherit",
  });
});

test("resolveRoleModelSource: opencode is NEVER seeded -> 'none' even with no roles.json", () => {
  assert.deepEqual(resolveRoleModelSource("worker", "opencode", false, {}), { kind: "none" });
});

test("resolveRoleModelSource: roles.json exists but has no binding for this role -> 'none'", () => {
  assert.deepEqual(resolveRoleModelSource("explore", "claude-code", true, {}), { kind: "none" });
  // Existence of the file matters — this is the exact distinction from the "seed" preview above.
  assert.notDeepEqual(
    resolveRoleModelSource("explore", "claude-code", true, {}),
    resolveRoleModelSource("explore", "claude-code", false, {}),
  );
});

test("formatRoleModelSource: 'none' on a CLOSED harness (claude-code) reads as a PROBLEM with a remedy command, never a blank", () => {
  const { line, problem } = formatRoleModelSource("reserve", "claude-code", { kind: "none" });
  assert.equal(problem, true);
  assert.match(line, /PROBLEM/);
  assert.match(line, /HARD-REFUSES/);
  assert.match(line, /petbox-wire model set reserve <model> --agent claude-code/);
});

test("formatRoleModelSource: 'none' on an OPEN harness (opencode) still reads as a PROBLEM+remedy, but names the warn-not-block consequence", () => {
  const { line, problem } = formatRoleModelSource("worker", "opencode", { kind: "none" });
  assert.equal(problem, true);
  assert.match(line, /PROBLEM/);
  assert.match(line, /WARNS/);
  assert.match(line, /petbox-wire model set worker <model> --agent opencode/);
});

test("formatRoleModelSource: 'roster' and 'seed' are non-problems and name their own remedy", () => {
  const roster = formatRoleModelSource("worker", "claude-code", { kind: "roster", model: "opus" });
  assert.equal(roster.problem, false);
  assert.match(roster.line, /source: roster/);
  assert.match(roster.line, /model set worker <model> --agent claude-code/);

  const seed = formatRoleModelSource("worker", "claude-code", { kind: "seed", model: "sonnet" });
  assert.equal(seed.problem, false);
  assert.match(seed.line, /source: seed/);
  assert.match(seed.line, /not yet written/);
});

test("computeRosterState: absent / empty-shell / partial / complete", () => {
  const empty: RolesFile = { activeProfile: "default", profiles: {} };
  assert.deepEqual(computeRosterState(DEFAULT_AGENT_DEFINITION, empty, false), { kind: "absent" });

  const shell: RolesFile = { activeProfile: "default", profiles: { default: { agents: {} } } };
  assert.deepEqual(computeRosterState(DEFAULT_AGENT_DEFINITION, shell, true), {
    kind: "empty-shell",
    activeProfile: "default",
  });

  const partial: RolesFile = {
    activeProfile: "default",
    profiles: {
      default: {
        agents: {
          "claude-code": {
            roles: { orchestrator: { model: "opus" }, worker: { model: "sonnet" } },
          },
        },
      },
    },
  };
  const partialState = computeRosterState(DEFAULT_AGENT_DEFINITION, partial, true);
  assert.equal(partialState.kind, "partial");
  if (partialState.kind === "partial") {
    for (const slug of ["utility", "reserve", "explore"]) assert.ok(partialState.missing.includes(slug));
  }

  const complete: RolesFile = {
    activeProfile: "default",
    profiles: {
      default: {
        agents: {
          "claude-code": {
            roles: {
              orchestrator: { model: "opus" },
              worker: { model: "sonnet" },
              utility: { model: "haiku" },
              explore: { model: "haiku" },
              reserve: { model: "fable" },
            },
          },
        },
      },
    },
  };
  assert.deepEqual(computeRosterState(DEFAULT_AGENT_DEFINITION, complete, true), {
    kind: "complete",
    activeProfile: "default",
  });
});

test("formatRosterState names every state distinctly", () => {
  assert.match(formatRosterState({ kind: "absent" }), /absent/);
  assert.match(
    formatRosterState({ kind: "empty-shell", activeProfile: "default" }),
    /EMPTY/,
  );
  assert.match(
    formatRosterState({ kind: "partial", activeProfile: "default", missing: ["explore"] }),
    /PARTIAL.*explore/,
  );
  assert.match(
    formatRosterState({ kind: "complete", activeProfile: "default" }),
    /COMPLETE/,
  );
});

test("formatDefinitionSource: server / LKG (degraded) / built-in (degraded) / built-in (normal, 404)", () => {
  const server: ResolvedAgentDefinition = {
    definition: DEFAULT_AGENT_DEFINITION,
    source: "server",
    stale: false,
    key: "default",
    version: 3,
  };
  assert.match(formatDefinitionSource(server), /^server \(live\)/);

  const lkg: ResolvedAgentDefinition = {
    definition: DEFAULT_AGENT_DEFINITION,
    source: "lkg",
    stale: true,
    key: "default",
    version: 2,
    staleMarker: "stale",
  };
  assert.match(formatDefinitionSource(lkg), /LKG CACHE — DEGRADED/);

  const builtinDegraded: ResolvedAgentDefinition = {
    definition: DEFAULT_AGENT_DEFINITION,
    source: "default",
    stale: false,
  };
  assert.match(formatDefinitionSource(builtinDegraded), /built-in copy — DEGRADED/);

  const builtinNormal: ResolvedAgentDefinition = {
    definition: DEFAULT_AGENT_DEFINITION,
    source: "default",
    stale: false,
    notFoundOnServer: true,
  };
  assert.match(formatDefinitionSource(builtinNormal), /normal for a fresh project/);
  assert.doesNotMatch(formatDefinitionSource(builtinNormal), /DEGRADED/);
});

test("formatCanonLeg: absent / empty (via version, never marker-text comparison) / content with char count", () => {
  assert.match(formatCanonLeg("project", { kind: "absent" }), /absent/);
  assert.match(formatCanonLeg("project", { kind: "empty" }), /empty \(0 of 10000 chars\)/);
  assert.match(formatCanonLeg("project", { kind: "content", chars: 1234 }), /1234 of 10000 chars/);
});

test("readArtifactState: absent / ours (origin marker) / foreign (no marker)", () => {
  const dir = freshDir("petbox-status-artifact-");
  try {
    const absent = join(dir, "absent.md");
    assert.equal(readArtifactState(absent), "absent");

    const ours = join(dir, "ours.md");
    writeFileSync(ours, "---\nname: petbox-worker\npetbox: managed\n---\nbody\n", "utf8");
    assert.equal(readArtifactState(ours), "ours");

    const foreign = join(dir, "foreign.md");
    writeFileSync(foreign, "---\nname: my-own-agent\n---\nbody\n", "utf8");
    assert.equal(readArtifactState(foreign), "foreign");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("checkSkillFile: absent -> false; foreign -> false; ours+rendered unknown -> 'unknown'; ours+match/mismatch", () => {
  const dir = freshDir("petbox-status-skill-");
  try {
    const absent = join(dir, "a.md");
    assert.deepEqual(checkSkillFile(absent, "anything"), {
      path: absent,
      state: "absent",
      matchesTemplate: false,
    });

    const foreign = join(dir, "f.md");
    writeFileSync(foreign, "not a petbox file\n", "utf8");
    const foreignReport = checkSkillFile(foreign, "anything");
    assert.equal(foreignReport.state, "foreign");
    assert.equal(foreignReport.matchesTemplate, false);

    const ours = join(dir, "o.md");
    const rendered = "---\nname: petbox\npetbox: managed\n---\nbody\n";
    writeFileSync(ours, rendered, "utf8");
    assert.deepEqual(checkSkillFile(ours, undefined), {
      path: ours,
      state: "ours",
      matchesTemplate: "unknown",
    });
    assert.deepEqual(checkSkillFile(ours, rendered), {
      path: ours,
      state: "ours",
      matchesTemplate: true,
    });
    assert.deepEqual(checkSkillFile(ours, rendered + "drift"), {
      path: ours,
      state: "ours",
      matchesTemplate: false,
    });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ---- CLI integration tests (isolated HOME, same pattern as roles.test.ts /
// apply-unbound-refusal.test.ts) -----------------------------------------------

function runStatusCli(
  cwd: string,
  homeDir: string,
): { stdout: string; stderr: string; status: number | null } {
  const res = spawnSync(process.execPath, [WIRE_TS, "status", "--offline"], {
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

test("CLI: fresh HOME (no roles.json) -> 'seed' for claude-code/droid roles DEFAULT_ROLE_MODEL_SEED covers, 'none' for opencode, exit 0", () => {
  const homeDir = freshDir("petbox-status-fresh-home-");
  const projectDir = freshDir("petbox-status-fresh-proj-");
  try {
    assert.equal(existsSync(join(homeDir, ".petbox", "roles.json")), false, "precondition");
    const { stdout, stderr, status } = runStatusCli(projectDir, homeDir);
    const out = stdout + stderr;

    // status must never write roles.json — it only reads/previews.
    assert.equal(
      existsSync(join(homeDir, ".petbox", "roles.json")),
      false,
      `status must not write roles.json. Output:\n${out}`,
    );

    assert.equal(status, WIRE_EXIT.ok, `status always exits 0. Output:\n${out}`);
    assert.match(out, /\[claude-code\]/);
    assert.match(out, /orchestrator -> .*-> model=opus \(source: seed/);
    assert.match(out, /worker -> .*-> model=sonnet \(source: seed/);
    assert.match(out, /\[droid\]/);
    assert.match(out, /reserve -> .*-> model=inherit \(source: seed/);
    assert.match(out, /\[opencode\]/);
    assert.match(out, /source: none → inherits session model\).*PROBLEM/);
    assert.match(out, /petbox-wire model set .* --agent opencode/);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("CLI: HOME with an explicit roles.json binding -> 'roster', not 'seed'", () => {
  const homeDir = freshDir("petbox-status-roster-home-");
  const projectDir = freshDir("petbox-status-roster-proj-");
  try {
    const petboxDir = join(homeDir, ".petbox");
    mkdirSync(petboxDir, { recursive: true });
    writeFileSync(
      join(petboxDir, "roles.json"),
      JSON.stringify(
        {
          activeProfile: "default",
          profiles: {
            default: {
              agents: {
                "claude-code": { roles: { worker: { model: "opus" } } },
              },
            },
          },
        },
        null,
        2,
      ),
      "utf8",
    );

    const { stdout, stderr, status } = runStatusCli(projectDir, homeDir);
    const out = stdout + stderr;
    assert.equal(status, WIRE_EXIT.ok);
    assert.match(out, /worker -> .*-> model=opus \(source: roster\)/);
    // Every OTHER claude-code role has no binding at all, and the file DOES exist — that is
    // "none", not "seed" (the file-existence distinction resolveRoleModelSource encodes).
    assert.match(out, /orchestrator -> .*-> model=\(none\) \(source: none/);
    assert.match(out, /PROBLEM: apply HARD-REFUSES this role on 'claude-code'/);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("CLI: roles.json missing exactly one role's binding -> that role prints 'none' with a remedy command, never a blank line", () => {
  const homeDir = freshDir("petbox-status-partial-home-");
  const projectDir = freshDir("petbox-status-partial-proj-");
  try {
    const petboxDir = join(homeDir, ".petbox");
    mkdirSync(petboxDir, { recursive: true });
    writeFileSync(
      join(petboxDir, "roles.json"),
      JSON.stringify(
        {
          activeProfile: "default",
          profiles: {
            default: {
              agents: {
                "claude-code": {
                  roles: {
                    orchestrator: { model: "opus" },
                    worker: { model: "sonnet" },
                    utility: { model: "haiku" },
                    reserve: { model: "fable" },
                    // "explore" deliberately absent.
                  },
                },
              },
            },
          },
        },
        null,
        2,
      ),
      "utf8",
    );

    const { stdout, stderr, status } = runStatusCli(projectDir, homeDir);
    const out = stdout + stderr;
    assert.equal(status, WIRE_EXIT.ok, "status never gates on this — always 0");
    assert.match(out, /pillar 2\/4 — roster: present but PARTIAL/);
    assert.match(out, /missing: explore/);
    assert.match(
      out,
      /explore -> .*-> model=\(none\) \(source: none → inherits session model\) — PROBLEM.*change: `petbox-wire model set explore <model> --agent claude-code`/,
    );
    assert.match(out, /worker -> .*-> model=sonnet \(source: roster\)/);
    assert.match(out, /status: done — see PROBLEM line\(s\)/);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});

test("CLI: unregistered, non-git cwd -> canon/skills report n/a, still exits 0", () => {
  const homeDir = freshDir("petbox-status-unreg-home-");
  const projectDir = freshDir("petbox-status-unreg-proj-");
  try {
    // Deliberately NOT --offline: an unregistered project directory has no baseUrl/apiKey to
    // fetch with, so resolveAgentDefinitionWithLkg / the canon+skills probes all short-circuit
    // on the missing projectKey before any network call — this exercises the "not registered"
    // branch specifically, distinct from the "--offline" branch covered by every other test here.
    const res = spawnSync(process.execPath, [WIRE_TS, "status"], {
      cwd: projectDir,
      encoding: "utf8",
      env: {
        ...process.env,
        USERPROFILE: homeDir,
        HOME: homeDir,
        HOMEDRIVE: undefined,
        HOMEPATH: undefined,
      },
    });
    const { stdout, stderr, status } = { stdout: res.stdout ?? "", stderr: res.stderr ?? "", status: res.status };
    const out = stdout + stderr;
    assert.equal(status, WIRE_EXIT.ok);
    assert.match(out, /pillar 3\/4 — canon: n\/a — .* is not a registered project/);
    assert.match(out, /pillar 4\/4 — skills: workspace unknown/);
  } finally {
    rmSync(homeDir, { recursive: true, force: true });
    rmSync(projectDir, { recursive: true, force: true });
  }
});
