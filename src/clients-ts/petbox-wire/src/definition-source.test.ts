// The kit's ONE definition resolver (definition-source.ts) — the cascade itself, the loudness of
// a broken layer, and the structural ratchet that keeps the server path from creeping back.
//
// Specs: definition-layer-cascade, broken-layer-fails-loudly. Card wire-stops-fetching-definition
// (stage 2), decisions D13/D15/D18.
//
// Run: node --test src/definition-source.test.ts

import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readFileSync, readdirSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { DEFAULT_AGENT_DEFINITION } from "./agent-definition.ts";
import {
  definitionLayerCandidates,
  formatDefinitionLayersLine,
  formatDefinitionProvenance,
  resolveDefinitionForSession,
  resolveLocalDefinition,
  resolveUserScopeDefinition,
} from "./definition-source.ts";
import { isLayerDirectory, LayerSourceError } from "./layer-cascade.ts";

function freshDir(prefix: string): string {
  return realpathSync(mkdtempSync(join(tmpdir(), prefix)));
}

function writeLayer(dir: string, name: string, files: Record<string, string>): string {
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, "layer.json"), JSON.stringify({ name, mode: "overlay" }), "utf8");
  for (const [f, content] of Object.entries(files)) writeFileSync(join(dir, f), content, "utf8");
  return dir;
}

// ---- the cascade: base < user < project, with provenance ------------------------------------

test("base < user < project actually MERGES, and provenance names the source of every field", () => {
  const home = freshDir("petbox-defsrc-home-");
  const root = freshDir("petbox-defsrc-root-");
  try {
    // user changes the worker's TIER; project replaces the worker's PROSE and adds a role.
    // Neither layer restates anything else, which is the whole point of a cascade over a copy.
    writeLayer(join(home, ".petbox", "agents"), "user", {
      "petbox-worker.json": JSON.stringify({ slug: "worker", tier: "worker-highstakes" }),
    });
    writeLayer(join(root, ".petbox", "agents"), "project", {
      "petbox-worker.md": "PROJECT PROSE FOR WORKER",
      "petbox-review.json": JSON.stringify({ slug: "review", tier: "worker", requiredCapabilities: [] }),
      "petbox-review.md": "the project's own reviewer",
    });

    const local = resolveLocalDefinition({ root, homeDir: home });
    assert.deepEqual(local.errors, [], JSON.stringify(local.resolution.diagnostics, null, 2));
    assert.equal(local.definition.name, "base < user < project");

    const worker = local.definition.roles.find((r) => r.slug === "worker");
    assert.ok(worker, "worker must survive the cascade");
    // Three layers, three different origins, in ONE role — the merge is real, not last-one-wins.
    assert.equal(worker!.tier, "worker-highstakes", "the user layer's tier override must win");
    assert.equal(worker!.notes, "PROJECT PROSE FOR WORKER", "the project layer's prose must replace, not append");
    const baseWorker = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === "worker");
    assert.deepEqual(
      worker!.requiredCapabilities,
      baseWorker!.requiredCapabilities,
      "a field NO layer mentions must still come through from the base",
    );

    const prov = local.resolution.provenance.get("worker");
    assert.deepEqual(
      { ...prov },
      {
        tier: "user",
        requiredCapabilities: "base",
        spawn: "base",
        escalation: "base",
        notes: "project",
      },
      "per-field provenance must name the layer that supplied each field",
    );

    // The layer a role was ADDED by is named too, not silently attributed to the floor.
    assert.equal(local.resolution.provenance.get("review")?.tier, "project");

    // …and all of that has to be legible in what a human is shown.
    const rendered = formatDefinitionProvenance(local);
    assert.match(rendered, /worker {2}tier=worker-highstakes {2}provenance: tier=user/);
    assert.match(rendered, /notes=project/);

    const line = formatDefinitionLayersLine(local);
    assert.match(line, /^layers=3: base\[kit v/);
    assert.match(line, /user\[overlay\]/);
    assert.match(line, /project\[overlay\]/);
  } finally {
    rmSync(home, { recursive: true, force: true });
    rmSync(root, { recursive: true, force: true });
  }
});

test("an ABSENT layer directory is no opinion, never an error — the base alone is a complete, valid resolve", () => {
  const home = freshDir("petbox-defsrc-home-");
  const root = freshDir("petbox-defsrc-root-");
  try {
    const local = resolveLocalDefinition({ root, homeDir: home });
    assert.deepEqual(local.errors, []);
    assert.equal(local.definition.name, "base");
    assert.deepEqual(
      local.definition.roles.map((r) => r.slug),
      DEFAULT_AGENT_DEFINITION.roles.map((r) => r.slug),
    );
    assert.deepEqual(
      definitionLayerCandidates(root, home).map((c) => c.present),
      [false, false],
    );
    // Reported as absent, in the operator's own words — not omitted, which would make "I have no
    // user layer" and "my user layer was skipped" look identical.
    assert.match(formatDefinitionLayersLine(local), /absent, no opinion: user=.*, project=/);
  } finally {
    rmSync(home, { recursive: true, force: true });
    rmSync(root, { recursive: true, force: true });
  }
});

test("resolveUserScopeDefinition takes base < user and IGNORES the project layer (the 15 profile files are a machine fact)", () => {
  const home = freshDir("petbox-defsrc-home-");
  const root = freshDir("petbox-defsrc-root-");
  const cwd = process.cwd();
  try {
    writeLayer(join(home, ".petbox", "agents"), "user", {
      "petbox-worker.md": "USER PROSE",
    });
    writeLayer(join(root, ".petbox", "agents"), "project", {
      "petbox-worker.md": "PROJECT PROSE",
    });
    process.chdir(root);
    const local = resolveUserScopeDefinition({ homeDir: home });
    assert.equal(local.definition.name, "base < user");
    assert.equal(local.definition.roles.find((r) => r.slug === "worker")?.notes, "USER PROSE");
  } finally {
    process.chdir(cwd);
    rmSync(home, { recursive: true, force: true });
    rmSync(root, { recursive: true, force: true });
  }
});

// ---- broken layer: BUILD throws, RENDER degrades loudly --------------------------------------

test("BUILD path: a present-but-unparseable layer THROWS a LayerSourceError carrying the absolute path and the parser's own message", () => {
  const home = freshDir("petbox-defsrc-home-");
  const root = freshDir("petbox-defsrc-root-");
  try {
    const dir = writeLayer(join(root, ".petbox", "agents"), "project", {});
    const broken = join(dir, "petbox-worker.json");
    writeFileSync(broken, "{ this is not json", "utf8");

    let thrown: unknown;
    try {
      resolveLocalDefinition({ root, homeDir: home });
    } catch (e) {
      thrown = e;
    }
    assert.ok(thrown instanceof LayerSourceError, `expected LayerSourceError, got ${String(thrown)}`);
    assert.equal((thrown as LayerSourceError).path, broken);
    assert.match((thrown as LayerSourceError).message, /is not valid JSON/);
    assert.ok((thrown as LayerSourceError).message.includes(broken));
  } finally {
    rmSync(home, { recursive: true, force: true });
    rmSync(root, { recursive: true, force: true });
  }
});

test("RENDER path: a broken layer NEVER throws — it returns a marker naming the file, falls back to the kit base, and leaves a wire.log trace", () => {
  const home = freshDir("petbox-defsrc-home-");
  const root = freshDir("petbox-defsrc-root-");
  try {
    const dir = writeLayer(join(root, ".petbox", "agents"), "project", {});
    const broken = join(dir, "petbox-worker.json");
    writeFileSync(broken, "{ this is not json", "utf8");

    const got = resolveDefinitionForSession({ root, homeDir: home, logSource: "unit-test" });
    assert.equal(got.degraded, true);
    assert.ok(got.note.includes(broken), `the marker must name the broken file:\n${got.note}`);
    assert.match(got.note, /BROKEN/);
    // The protocol is rendered from the shipped FLOOR — never from a previously-successful copy.
    assert.equal(got.definition, DEFAULT_AGENT_DEFINITION);
    assert.doesNotMatch(got.note, /cache|last known good/i);

    const log = readFileSync(join(home, ".petbox", "wire.log"), "utf8");
    assert.match(log, /\[unit-test\]/);
    assert.ok(log.includes(broken), `the wire.log trace must name the broken file:\n${log}`);
  } finally {
    rmSync(home, { recursive: true, force: true });
    rmSync(root, { recursive: true, force: true });
  }
});

test("RENDER path: a cascade ERROR (not just an unreadable file) also degrades loudly rather than shipping a half-applied roster", () => {
  const home = freshDir("petbox-defsrc-home-");
  const root = freshDir("petbox-defsrc-root-");
  try {
    // The baseline's orchestrator escalates to `reserve`; tombstoning it makes that target dangle.
    writeLayer(join(root, ".petbox", "agents"), "project", {
      "petbox-reserve.json": JSON.stringify({ slug: "reserve", removed: true, reason: "test" }),
    });
    const got = resolveDefinitionForSession({ root, homeDir: home, logSource: "unit-test" });
    assert.equal(got.degraded, true);
    assert.match(got.note, /E1/);
    assert.equal(got.definition, DEFAULT_AGENT_DEFINITION);
  } finally {
    rmSync(home, { recursive: true, force: true });
    rmSync(root, { recursive: true, force: true });
  }
});


// ---- service files and empty directories: a folder is not an opinion -------------------------
//
// Reproduced live before this fix: `~/.petbox/agents/` holding a valid `layer.json` plus a
// `.DS_Store` produced an E5 ERROR — which meant `degraded: true`, a "definition layers are
// BROKEN" marker at the head of the banner on all three harnesses, and a hard exit 1 from
// `apply`/`doctor`. Opening a folder in Finder must not be able to do that. Neither must
// `mkdir ~/.petbox/agents` in preparation for writing an override later.

const SERVICE_FILES = [".DS_Store", "Thumbs.db", "desktop.ini", ".gitkeep", ".gitignore", "README.md"];

test("service files in a layer directory are ignored SILENTLY — not E5, not degraded, not a marker", () => {
  const home = freshDir("petbox-defsrc-home-");
  const root = freshDir("petbox-defsrc-root-");
  try {
    const dir = writeLayer(join(home, ".petbox", "agents"), "user", {
      "petbox-worker.json": JSON.stringify({ slug: "worker", tier: "worker-highstakes" }),
    });
    for (const f of SERVICE_FILES) writeFileSync(join(dir, f), "not a layer document", "utf8");

    const local = resolveLocalDefinition({ root, homeDir: home });
    assert.deepEqual(
      local.errors,
      [],
      `a desktop/git service file must never be an error:\n${JSON.stringify(local.resolution.diagnostics, null, 2)}`,
    );
    // Not even a warning: these are not near-misses, they are known non-layers.
    for (const d of local.resolution.diagnostics) {
      for (const f of SERVICE_FILES) {
        assert.ok(!d.message.includes(f), `${f} was mentioned in a diagnostic: ${d.message}`);
      }
    }
    // The layer itself still works.
    assert.equal(local.definition.roles.find((r) => r.slug === "worker")?.tier, "worker-highstakes");

    // …and the SessionStart path stays clean, which is where the damage actually showed.
    const session = resolveDefinitionForSession({ root, homeDir: home });
    assert.equal(session.degraded, false);
    assert.equal(session.note, "");
  } finally {
    rmSync(home, { recursive: true, force: true });
    rmSync(root, { recursive: true, force: true });
  }
});

test("a directory that declares NOTHING (empty, or only service files) is absent — no opinion, not a missing-manifest error", () => {
  const home = freshDir("petbox-defsrc-home-");
  const root = freshDir("petbox-defsrc-root-");
  try {
    // The commonest first move: make the directory now, put an override in it later.
    mkdirSync(join(home, ".petbox", "agents"), { recursive: true });
    mkdirSync(join(root, ".petbox", "agents"), { recursive: true });
    writeFileSync(join(root, ".petbox", "agents", ".DS_Store"), "\0\0", "utf8");

    assert.deepEqual(
      definitionLayerCandidates(root, home).map((c) => c.present),
      [false, false],
      "a directory declaring nothing must read as absent, exactly like one that was never created",
    );

    const local = resolveLocalDefinition({ root, homeDir: home });
    assert.deepEqual(local.errors, []);
    assert.equal(local.definition.name, "base");

    const session = resolveDefinitionForSession({ root, homeDir: home });
    assert.equal(session.degraded, false);
    assert.equal(session.note, "");
  } finally {
    rmSync(home, { recursive: true, force: true });
    rmSync(root, { recursive: true, force: true });
  }
});

test("a file that CLAIMS to be a role document but is malformed is still E5 — that is the case E5 exists for", () => {
  const home = freshDir("petbox-defsrc-home-");
  const root = freshDir("petbox-defsrc-root-");
  try {
    const dir = writeLayer(join(root, ".petbox", "agents"), "project", {});
    // Right namespace, wrong shape: an extension the schema does not define.
    writeFileSync(join(dir, "petbox-worker.jsonc"), "{}", "utf8");
    // And a near-miss that is NOT in the namespace: visible as a warning, never fatal.
    writeFileSync(join(dir, "worker.json"), "{}", "utf8");

    const local = resolveLocalDefinition({ root, homeDir: home });
    const e5 = local.errors.filter((d) => d.code === "E5");
    assert.equal(e5.length, 1, JSON.stringify(local.resolution.diagnostics, null, 2));
    assert.ok(e5[0]!.message.includes("petbox-worker.jsonc"), e5[0]!.message);

    const warned = local.resolution.diagnostics.filter(
      (d) => d.severity === "warning" && d.message.includes("worker.json") && !d.message.includes("petbox-worker"),
    );
    assert.equal(warned.length, 1, "a forgotten petbox- prefix must be VISIBLE, but as a warning");
  } finally {
    rmSync(home, { recursive: true, force: true });
    rmSync(root, { recursive: true, force: true });
  }
});

test("isLayerDirectory: role documents without a layer.json still DECLARE a layer, so that stays a loud refusal", () => {
  const home = freshDir("petbox-defsrc-home-");
  const root = freshDir("petbox-defsrc-root-");
  try {
    const dir = join(root, ".petbox", "agents");
    mkdirSync(dir, { recursive: true });
    writeFileSync(join(dir, "petbox-worker.json"), JSON.stringify({ slug: "worker" }), "utf8");
    assert.equal(isLayerDirectory(dir), true, "role documents are an unambiguous statement of intent");
    assert.throws(() => resolveLocalDefinition({ root, homeDir: home }), /has no layer\.json/);
  } finally {
    rmSync(home, { recursive: true, force: true });
    rmSync(root, { recursive: true, force: true });
  }
});

// ---- structural ratchet ----------------------------------------------------------------------

test("no non-test source in this package names the server definition path — the machine guarantee that it cannot come back quietly", () => {
  // Same class of guard as npm-wire-drift's and the process.exit whitelist's: a BEHAVIORAL test
  // can only prove that today's code paths do not fetch. This proves the SHAPE is gone. The two
  // strings are the whole server contract that was removed:
  //   "/agent-defs/"    — GET {baseUrl}/api/{projectKey}/agent-defs/{key}, the fetch itself
  //   "agent-def.json"  — ~/.petbox/cache/<project>.agent-def.json, the last-known-good replica
  //                       whose entire purpose was to keep answering after the source broke (D15)
  //
  // Test files are exempt on purpose: a future test may legitimately need to name the retired
  // path to assert it is NOT taken, and a guard that forbids describing the thing it forbids is
  // a guard nobody can write a regression test against.
  //
  // The canon's own offline cache (~/.petbox/cache/<project>.canon.md, canon.ts) is deliberately
  // NOT covered here: memory canon still lives on the server, so an LKG replica of it is
  // justified. This ratchet is about the DEFINITION, not about caching in general.
  const FORBIDDEN = ["/agent-defs/", "agent-def.json", "agent-def-fetch"];
  const dir = import.meta.dirname;
  const offenders: string[] = [];
  for (const file of readdirSync(dir)) {
    if (!file.endsWith(".ts") || file.endsWith(".test.ts")) continue;
    const text = readFileSync(join(dir, file), "utf8");
    for (const needle of FORBIDDEN) {
      if (text.includes(needle)) offenders.push(`${file}: ${needle}`);
    }
  }
  assert.deepEqual(
    offenders,
    [],
    `the server definition path reappeared in non-test source:\n  ${offenders.join("\n  ")}\n` +
      `The definition is built from files (definition-source.ts). If a server leg is genuinely ` +
      `wanted again, that is an owner decision reversing D13/D18, not a refactor.`,
  );
});
