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
import { existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { DEFAULT_AGENT_DEFINITION } from "./agent-definition.ts";
import type { ResolvedAgentDefinition } from "./agent-def-fetch.ts";
import { buildProtocol, mcpPetboxTool } from "./protocol.ts";
import type { ResolvedProject } from "./registry.ts";
import { SESSION_BANNER_BUDGET_BYTES } from "./session-budget.ts";
import {
  BANNER_BUDGET_WARN_FRACTION,
  bannerBudgetLegsOrUnreachable,
  bannerBudgetWarnThresholdBytes,
  type BannerBudgetLeg,
  computeBannerBudgetLegs,
  computeRegistryStatusRow,
  computeRosterState,
  formatBannerBudgetLeg,
  formatCanonLeg,
  formatDefinitionSource,
  formatRegistryStatusRow,
  formatRoleModelSource,
  formatRosterState,
  resolveRoleModelSource,
  roleRelativePath,
} from "./status.ts";
import { PETBOX_MARKER_LINE } from "./origin-marker.ts";
import { PROJECT_SKILLS, renderSkillTemplate } from "./skill-files.ts";
import { readArtifactState } from "./origin-marker.ts";
import type { RolesFile } from "./roles.ts";
import { WIRE_EXIT } from "./wire-exit.ts";

const FAKE_PROJECT: ResolvedProject = {
  project: "banner-budget-test-project",
  apiKey: "fake-key",
  baseUrl: "http://unused.invalid",
  envVar: "PETBOX_BANNER_BUDGET_TEST_API_KEY",
};

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
    for (const slug of ["worker-highstakes", "reserve", "explore"]) assert.ok(partialState.missing.includes(slug));
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
              "worker-highstakes": { model: "opus" },
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

// checkSkillFile/formatSkillFile/buildSkillReports moved to skill-files.ts (task
// builtin-definition-drifts-no-catchup item 3) — their unit tests now live in
// skill-files.test.ts, next to the functions.

// ---- session banner budget (card canon-write-gate-banner-budget) -----------

test("computeBannerBudgetLegs: canon is fetched ONCE and reused for both source legs; resume's protocol is strictly longer (recall-nudge suffix)", async () => {
  let fetchCount = 0;
  const canonFetch = async () => {
    fetchCount++;
    return "## PetBox memory canon\n\nsome curated pointer text";
  };
  const legs = await computeBannerBudgetLegs(FAKE_PROJECT, DEFAULT_AGENT_DEFINITION, { canonFetch });
  assert.equal(fetchCount, 1, "canon does not vary by source — must not be fetched twice");
  assert.deepEqual(legs.map((l) => l.source), ["startup", "resume"]);
  assert.equal(legs[0]!.banner.canonBytes, legs[1]!.banner.canonBytes, "same canon on both legs");
  assert.ok(
    legs[1]!.banner.protocolBytes > legs[0]!.banner.protocolBytes,
    "resume's protocol must carry the recall-nudge suffix and be strictly longer than startup's",
  );
  for (const leg of legs) {
    assert.equal(leg.marginBytes, SESSION_BANNER_BUDGET_BYTES - leg.combinedBytes);
    assert.equal(leg.combinedBytes, leg.banner.protocolBytes + 2 + leg.banner.canonBytes);
  }
});

test("computeBannerBudgetLegs: no canon at all -> canonBytes 0, combinedBytes collapses to protocolBytes alone", async () => {
  const legs = await computeBannerBudgetLegs(FAKE_PROJECT, DEFAULT_AGENT_DEFINITION, {
    canonFetch: async () => null,
  });
  for (const leg of legs) {
    assert.equal(leg.banner.canonBytes, 0);
    assert.equal(leg.combinedBytes, leg.banner.protocolBytes);
    assert.equal(leg.banner.canonIncluded, false);
  }
});

test("computeBannerBudgetLegs: a canon that fits startup's shorter protocol can still be DROPPED on resume's longer one — the exact regression this card caught", async () => {
  const startupProtocol = buildProtocol(FAKE_PROJECT.project, mcpPetboxTool, {
    source: "startup",
    harness: "claude-code",
    definition: DEFAULT_AGENT_DEFINITION,
  });
  const resumeProtocol = buildProtocol(FAKE_PROJECT.project, mcpPetboxTool, {
    source: "resume",
    harness: "claude-code",
    definition: DEFAULT_AGENT_DEFINITION,
  });
  const startupBytes = Buffer.byteLength(startupProtocol, "utf8");
  const resumeBytes = Buffer.byteLength(resumeProtocol, "utf8");
  assert.ok(resumeBytes > startupBytes, "precondition: resume protocol strictly longer than startup's");

  // Sized to land EXACTLY at startup's budget edge (fits with zero bytes to spare) but past
  // resume's edge (resume's protocol alone already ate what startup left free for canon).
  const canonBytes = SESSION_BANNER_BUDGET_BYTES - startupBytes - 2;
  const canon = "C".repeat(canonBytes);

  const legs = await computeBannerBudgetLegs(FAKE_PROJECT, DEFAULT_AGENT_DEFINITION, {
    canonFetch: async () => canon,
  });
  const startupLeg = legs.find((l) => l.source === "startup")!;
  const resumeLeg = legs.find((l) => l.source === "resume")!;
  assert.equal(startupLeg.banner.canonIncluded, true, "startup: canon fits exactly");
  assert.equal(resumeLeg.banner.canonIncluded, false, "resume: same canon no longer fits — this is the bug");
  assert.equal(resumeLeg.banner.overBudget, true);
});

test("formatBannerBudgetLeg: canon INCLUDED reads the margin", () => {
  const leg: BannerBudgetLeg = {
    source: "startup",
    banner: { text: "", totalBytes: 150, protocolBytes: 100, canonBytes: 50, canonIncluded: true, canonLegs: "both", canonIncludedBytes: 50, overBudget: false },
    combinedBytes: 152,
    marginBytes: 48,
  };
  assert.match(formatBannerBudgetLeg(leg), /source=startup:.*canon INCLUDED, margin 48B/);
});

test("formatBannerBudgetLeg: canon DROPPED reads the overage, not just a bare 'over budget'", () => {
  const leg: BannerBudgetLeg = {
    source: "resume",
    banner: { text: "", totalBytes: 100, protocolBytes: 100, canonBytes: 50, canonIncluded: false, canonLegs: "none", canonIncludedBytes: 0, overBudget: true },
    combinedBytes: 152,
    marginBytes: -2,
  };
  assert.match(formatBannerBudgetLeg(leg), /source=resume:.*canon DROPPED — over budget by 2B/);
});

test("formatBannerBudgetLeg: protocol ALONE over budget reads distinctly from canon-dropped (nothing left to drop)", () => {
  const leg: BannerBudgetLeg = {
    source: "startup",
    banner: { text: "", totalBytes: 100, protocolBytes: 100, canonBytes: 0, canonIncluded: false, canonLegs: "none", canonIncludedBytes: 0, overBudget: true },
    combinedBytes: 100,
    marginBytes: -5,
  };
  assert.match(formatBannerBudgetLeg(leg), /PROTOCOL ALONE over budget by 5B \(nothing left to drop\)/);
});

test("formatBannerBudgetLeg: no canon at all (healthy) reads distinctly from canon-included", () => {
  const leg: BannerBudgetLeg = {
    source: "startup",
    banner: { text: "", totalBytes: 100, protocolBytes: 100, canonBytes: 0, canonIncluded: false, canonLegs: "none", canonIncludedBytes: 0, overBudget: false },
    combinedBytes: 100,
    marginBytes: 9300,
  };
  assert.match(formatBannerBudgetLeg(leg), /no canon available, margin 9300B/);
});

test("bannerBudgetWarnThresholdBytes: 5% of SESSION_BANNER_BUDGET_BYTES, rounded", () => {
  assert.equal(
    bannerBudgetWarnThresholdBytes(),
    Math.round(SESSION_BANNER_BUDGET_BYTES * BANNER_BUDGET_WARN_FRACTION),
  );
  assert.equal(BANNER_BUDGET_WARN_FRACTION, 0.05);
});

test("bannerBudgetLegsOrUnreachable: server answers the canon endpoint -> ok:true with both legs computed", async () => {
  const server = createServer((_req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ project: { body: "curated text", version: 5 }, workspace: null }));
  });
  await new Promise<void>((r) => server.listen(0, "127.0.0.1", () => r()));
  const port = (server.address() as AddressInfo).port;
  try {
    const resolved: ResolvedProject = { ...FAKE_PROJECT, baseUrl: `http://127.0.0.1:${port}` };
    const result = await bannerBudgetLegsOrUnreachable(resolved, DEFAULT_AGENT_DEFINITION);
    assert.equal(result.ok, true);
    if (result.ok) {
      assert.equal(result.legs.length, 2);
      assert.ok(result.legs[0]!.banner.canonBytes > 0);
    }
  } finally {
    await new Promise<void>((r) => server.close(() => r()));
  }
});

test("bannerBudgetLegsOrUnreachable: canon endpoint unreachable -> ok:false (never throws, never fabricates a healthy reading)", async () => {
  // Bind, read the port, then close immediately — the port is guaranteed to refuse connections
  // (nothing else on the machine can grab it in this window in practice for a test's lifetime).
  const probe = createServer();
  await new Promise<void>((r) => probe.listen(0, "127.0.0.1", () => r()));
  const port = (probe.address() as AddressInfo).port;
  await new Promise<void>((r) => probe.close(() => r()));

  const resolved: ResolvedProject = { ...FAKE_PROJECT, baseUrl: `http://127.0.0.1:${port}` };
  const result = await bannerBudgetLegsOrUnreachable(resolved, DEFAULT_AGENT_DEFINITION);
  assert.equal(result.ok, false);
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
                    "worker-highstakes": { model: "opus" },
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

// ---- registry-wide status (task kit-version-lands-everywhere-and-sweeps item 4) --------------

const REGISTRY_STATUS_TEMPLATES_ROOT = join(import.meta.dirname, "templates");

function writeFixtureSkill(root: string, dir: string, content: string): void {
  const skillDir = join(root, ".claude", "skills", dir);
  mkdirSync(skillDir, { recursive: true });
  writeFileSync(join(skillDir, "SKILL.md"), content, "utf8");
}

test("computeRegistryStatusRow: missing directory -> verdict 'missing-dir', every skill counted missing", () => {
  const dir = freshDir("petbox-registry-status-missing-");
  rmSync(dir, { recursive: true, force: true }); // the whole point: it must NOT exist
  const row = computeRegistryStatusRow({ prefix: dir, project: "p", envVar: "X" }, REGISTRY_STATUS_TEMPLATES_ROOT);
  assert.equal(row.verdict, "missing-dir");
  assert.equal(row.presentSkills, 0);
  assert.equal(row.missingSkills.length, PROJECT_SKILLS.length);
});

test("computeRegistryStatusRow: every skill materialized and byte-identical to its template -> verdict 'ok'", () => {
  const dir = freshDir("petbox-registry-status-ok-");
  try {
    const project = "registry-status-ok-project";
    for (const spec of PROJECT_SKILLS) {
      const tpl = readFileSync(join(REGISTRY_STATUS_TEMPLATES_ROOT, spec.dir, "SKILL.md"), "utf8");
      // needsWorkspace templates render fine with "" — computeRegistryStatusRow itself never
      // compares them (rendered stays undefined for those), so any marker-bearing content there
      // that isn't foreign is enough for "present"; using the real render keeps this fixture
      // honest either way.
      const rendered = renderSkillTemplate(tpl, project, "");
      writeFixtureSkill(dir, spec.dir, rendered);
    }
    const row = computeRegistryStatusRow({ prefix: dir, project, envVar: "X" }, REGISTRY_STATUS_TEMPLATES_ROOT);
    assert.equal(row.verdict, "ok", JSON.stringify(row));
    assert.equal(row.presentSkills, PROJECT_SKILLS.length);
    assert.deepEqual(row.missingSkills, []);
    assert.deepEqual(row.driftedSkills, []);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("computeRegistryStatusRow: a materialized skill that no longer matches its template -> verdict 'stale', named in driftedSkills", () => {
  const dir = freshDir("petbox-registry-status-drift-");
  try {
    const project = "registry-status-drift-project";
    for (const spec of PROJECT_SKILLS) {
      const tpl = readFileSync(join(REGISTRY_STATUS_TEMPLATES_ROOT, spec.dir, "SKILL.md"), "utf8");
      const rendered = renderSkillTemplate(tpl, project, "");
      const nonWorkspace = !spec.needsWorkspace;
      writeFixtureSkill(dir, spec.dir, nonWorkspace ? rendered + "\nstale extra line\n" : rendered);
    }
    const row = computeRegistryStatusRow({ prefix: dir, project, envVar: "X" }, REGISTRY_STATUS_TEMPLATES_ROOT);
    assert.equal(row.verdict, "stale");
    // Every non-workspace skill was mutated, so every one of them must be named as drifted.
    const expectedDrifted = PROJECT_SKILLS.filter((s) => !s.needsWorkspace).map((s) => s.dir).sort();
    assert.deepEqual([...row.driftedSkills].sort(), expectedDrifted);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("computeRegistryStatusRow: a skill never materialized -> named in missingSkills, verdict 'stale'", () => {
  const dir = freshDir("petbox-registry-status-partial-");
  try {
    const project = "registry-status-partial-project";
    const specs = PROJECT_SKILLS.slice(1); // skip the first entry entirely
    for (const spec of specs) {
      const tpl = readFileSync(join(REGISTRY_STATUS_TEMPLATES_ROOT, spec.dir, "SKILL.md"), "utf8");
      writeFixtureSkill(dir, spec.dir, renderSkillTemplate(tpl, project, ""));
    }
    const row = computeRegistryStatusRow({ prefix: dir, project, envVar: "X" }, REGISTRY_STATUS_TEMPLATES_ROOT);
    assert.equal(row.verdict, "stale");
    assert.deepEqual(row.missingSkills, [PROJECT_SKILLS[0]!.dir]);
    assert.equal(row.presentSkills, specs.length);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("computeRegistryStatusRow: a foreign (non-PetBox) file at a skill path -> counted present but named in foreignPaths, verdict 'stale'", () => {
  const dir = freshDir("petbox-registry-status-foreign-");
  try {
    const project = "registry-status-foreign-project";
    const [foreignSpec, ...rest] = PROJECT_SKILLS;
    writeFixtureSkill(dir, foreignSpec!.dir, "# someone else's file, no petbox marker at all\n");
    for (const spec of rest) {
      const tpl = readFileSync(join(REGISTRY_STATUS_TEMPLATES_ROOT, spec.dir, "SKILL.md"), "utf8");
      writeFixtureSkill(dir, spec.dir, renderSkillTemplate(tpl, project, ""));
    }
    const row = computeRegistryStatusRow({ prefix: dir, project, envVar: "X" }, REGISTRY_STATUS_TEMPLATES_ROOT);
    assert.equal(row.verdict, "stale");
    assert.equal(row.foreignPaths.length, 1);
    assert.ok(row.foreignPaths[0]!.includes(foreignSpec!.dir));
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("computeRegistryStatusRow: a legacy pre-rename skill dir still present -> named in legacyLeftovers, verdict 'stale'", () => {
  const dir = freshDir("petbox-registry-status-legacy-");
  try {
    const project = "registry-status-legacy-project";
    const specWithLegacy = PROJECT_SKILLS.find((s) => (s.legacyDirs?.length ?? 0) > 0);
    assert.ok(specWithLegacy, "expected at least one PROJECT_SKILLS entry with legacyDirs to exercise this");
    for (const spec of PROJECT_SKILLS) {
      const tpl = readFileSync(join(REGISTRY_STATUS_TEMPLATES_ROOT, spec.dir, "SKILL.md"), "utf8");
      writeFixtureSkill(dir, spec.dir, renderSkillTemplate(tpl, project, ""));
    }
    // Plant the pre-rename leftover the sweep is supposed to have removed but didn't.
    writeFixtureSkill(dir, specWithLegacy!.legacyDirs![0]!, `${PETBOX_MARKER_LINE}\nstale legacy copy\n`);
    const row = computeRegistryStatusRow({ prefix: dir, project, envVar: "X" }, REGISTRY_STATUS_TEMPLATES_ROOT);
    assert.equal(row.verdict, "stale");
    assert.equal(row.legacyLeftovers.length, 1);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("formatRegistryStatusRow: every verdict reads distinctly", () => {
  const ok = formatRegistryStatusRow({
    project: "p1",
    dir: "/d1",
    verdict: "ok",
    presentSkills: 8,
    totalSkills: 8,
    missingSkills: [],
    driftedSkills: [],
    foreignPaths: [],
    legacyLeftovers: [],
  });
  const stale = formatRegistryStatusRow({
    project: "p2",
    dir: "/d2",
    verdict: "stale",
    presentSkills: 6,
    totalSkills: 8,
    missingSkills: ["petbox-card-check"],
    driftedSkills: ["petbox-methodology"],
    foreignPaths: [],
    legacyLeftovers: [],
  });
  const missing = formatRegistryStatusRow({
    project: "p3",
    dir: "/d3",
    verdict: "missing-dir",
    presentSkills: 0,
    totalSkills: 8,
    missingSkills: [],
    driftedSkills: [],
    foreignPaths: [],
    legacyLeftovers: [],
  });
  assert.match(ok, /OK/);
  assert.match(stale, /STALE/);
  assert.match(stale, /petbox-card-check/);
  assert.match(stale, /petbox-methodology/);
  assert.match(missing, /MISSING DIRECTORY/);
});
