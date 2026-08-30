// The definition layer cascade, gated against the HAND-WRITTEN expectation
// (research/wire-source-of-truth/prototype/RESOLVED.md, 2026-08-30).
//
// The order matters and is the point: the expected resolve of that fixture was written out by a
// person BEFORE any resolver existed. This file asserts that document, clause by clause, so the
// test can fail the implementation rather than merely re-describing it.
//
// Fixture layout (three layers, lowest priority first):
//   base/     mode=replace  3 roles, .json + .md      — the basis declares the roster outright
//   user/     mode=overlay  tombstone / field patch / prose replacement
//   project/  mode=overlay  new role / field patch / prose addendum
//
// The fixture is DELIBERATELY broken in exactly one way: user/ removed `reserve` but left
// orchestrator.escalation.targets pointing at it. Exactly one E1 is the expected report.

import { cpSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import assert from "node:assert/strict";

import {
  LayerSourceError,
  cascadeErrors,
  cascadeWarnings,
  formatCascadeProvenance,
  formatCascadeTrace,
  resolveDefinitionLayers,
} from "./layer-cascade.ts";
import { validateAgentDefinition } from "./agent-definition.ts";
import { buildRoleBody } from "./apply-artifacts.ts";

const FIXTURE = join(import.meta.dirname, "..", "test-fixtures", "layer-cascade");
const RESEARCH = join(
  import.meta.dirname,
  "..", "..", "..", "..",
  "research", "wire-source-of-truth", "prototype",
);
const LAYERS = ["base", "user", "project"].map((d) => join(FIXTURE, d));

function resolveFixture() {
  return resolveDefinitionLayers(LAYERS);
}

/** A throwaway copy of the fixture, for the negative cases that must MUTATE a layer. */
function copyFixture(): { dirs: string[]; root: string } {
  const root = mkdtempSync(join(tmpdir(), "petbox-cascade-"));
  cpSync(FIXTURE, root, { recursive: true });
  return { root, dirs: ["base", "user", "project"].map((d) => join(root, d)) };
}

// ── The RESOLVED.md walkthrough ─────────────────────────────────────────────────────────────

test("roster after the cascade is exactly three roles — reserve is GONE (tombstone in user/)", () => {
  const r = resolveFixture();
  assert.deepEqual(
    r.definition.roles.map((x) => x.slug).sort(),
    ["orchestrator", "review", "worker"],
  );
});

test("provenance is PER FIELD: orchestrator draws on all three layers at once", () => {
  // RESOLVED.md: "tier, requiredCapabilities, escalation из default; проза из user:stdray
  // (замещена целиком); spawn из project:petbox. Три слоя в одной роли."
  const r = resolveFixture();
  assert.deepEqual(r.provenance.get("orchestrator"), {
    tier: "default",
    requiredCapabilities: "default",
    escalation: "default",
    spawn: "project:petbox",
    notes: "user:stdray",
  });
});

test("provenance: worker keeps the basis except escalation, and carries the project's addendum", () => {
  // RESOLVED.md: "всё из default, кроме escalation (из user:stdray, available:false) и
  // дополнения к прозе (из project:petbox, отдельной секцией)."
  const r = resolveFixture();
  assert.deepEqual(r.provenance.get("worker"), {
    tier: "default",
    requiredCapabilities: "default",
    spawn: "default",
    escalation: "user:stdray",
    notes: "default",
  });
  const worker = r.definition.roles.find((x) => x.slug === "worker");
  assert.deepEqual(worker?.escalation, { available: false });
  assert.deepEqual(r.addenda.get("worker")?.map((a) => a.layer), ["project:petbox"]);
});

test("provenance: review is wholly the project's, a role no lower layer knows", () => {
  const r = resolveFixture();
  assert.deepEqual(r.provenance.get("review"), {
    tier: "project:petbox",
    requiredCapabilities: "project:petbox",
    spawn: "project:petbox",
    escalation: "project:petbox",
    notes: "project:petbox",
  });
});

test("prose REPLACES, it does not merge: the basis text of orchestrator is gone entirely", () => {
  const r = resolveFixture();
  const notes = r.definition.roles.find((x) => x.slug === "orchestrator")?.notes ?? "";
  assert.match(notes, /Резерва нет/, "the user layer's replacement text is missing");
  assert.doesNotMatch(
    notes,
    /см\. rejectModelFields/,
    "basis prose survived a replacement — prose was merged, not replaced",
  );
});

test("an addendum is attributed to its layer BY NAME and lands as its own section", () => {
  const r = resolveFixture();
  const worker = r.definition.roles.find((x) => x.slug === "worker");
  assert.match(worker?.notes ?? "", /## Layer addendum \(project:petbox\)/);
  assert.match(worker?.notes ?? "", /доску `observations`/);
  // The basis prose it is appended TO is still there — an addendum adds, it does not replace.
  assert.match(worker?.notes ?? "", /Ты лист/);
  // ...and the section reaches the rendered artifact, not just the data structure.
  assert.match(buildRoleBody(worker!), /## Layer addendum \(project:petbox\)/);
});

test("a replacement RESETS the addenda collected below it — no orphan paragraph survives", () => {
  // The trap this rule exists for: layer 2 appends a paragraph commenting on layer 1's prose;
  // layer 3 throws that prose away. Keeping the addendum would leave a paragraph discussing
  // text the reader can no longer see. The fixture cannot express this (its only addendum is on
  // the TOP layer), so the case gets its own three-layer set.
  const root = mkdtempSync(join(tmpdir(), "petbox-cascade-reset-"));
  try {
    const layer = (name: string, mode: string, files: Record<string, string>) => {
      const dir = join(root, name);
      mkdirSync(dir, { recursive: true });
      writeFileSync(join(dir, "layer.json"), JSON.stringify({ name, mode }), "utf8");
      for (const [f, body] of Object.entries(files)) writeFileSync(join(dir, f), body, "utf8");
      return dir;
    };
    const dirs = [
      layer("l1", "replace", {
        "petbox-worker.json": JSON.stringify({ slug: "worker", tier: "worker", requiredCapabilities: [] }),
        "petbox-worker.md": "BASE PROSE",
      }),
      layer("l2", "overlay", { "petbox-worker.append.md": "L2 ADDENDUM" }),
      layer("l3", "overlay", { "petbox-worker.md": "REPLACEMENT PROSE" }),
    ];

    const r = resolveDefinitionLayers(dirs);
    const notes = r.definition.roles.find((x) => x.slug === "worker")?.notes ?? "";
    assert.match(notes, /REPLACEMENT PROSE/);
    assert.doesNotMatch(notes, /BASE PROSE/, "prose was merged, not replaced");
    assert.doesNotMatch(
      notes,
      /L2 ADDENDUM/,
      "an addendum written against discarded prose survived — that is the orphan paragraph",
    );
    assert.deepEqual(r.addenda.get("worker"), []);
    assert.equal(r.provenance.get("worker")?.notes, "l3");
    // ...and the trace SAYS the addenda were reset, so the loss is never silent.
    assert.match(formatCascadeTrace(r), /l3: ~ worker → notes \(replaced; addenda reset\)/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("the fixture reports EXACTLY ONE error: the dangling escalation target user/ left behind", () => {
  // RESOLVED.md: "Ожидаемый отчёт валидатора — ровно одна ошибка:
  // E1 orchestrator.escalation.targets → \"reserve\": роли нет в резолве, exit-код 1."
  const r = resolveFixture();
  const errors = cascadeErrors(r);
  assert.equal(errors.length, 1, `expected 1 error, got: ${errors.map((e) => e.message).join(" | ")}`);
  assert.equal(errors[0]?.code, "E1");
  assert.match(errors[0]?.message ?? "", /^orchestrator\.escalation\.targets → "reserve":/);
});

test("the clean fixture produces no warnings at all", () => {
  // RESOLVED.md is explicit that the negative cases live outside the fixture on purpose, so a
  // clean run yields exactly one error and nothing else.
  const r = resolveFixture();
  assert.deepEqual(cascadeWarnings(r), []);
});

test("a one-field patch is a legitimate layer — the layer schema is NOT the resolve schema", () => {
  // user/petbox-worker.json carries only slug + escalation. validateAgentDefinition would reject
  // it as a definition; the cascade accepts it as a patch and the RESULT still validates.
  const raw = JSON.parse(readFileSync(join(FIXTURE, "user", "petbox-worker.json"), "utf8")) as
    Record<string, unknown>;
  assert.equal(raw["tier"], undefined);
  assert.equal(raw["requiredCapabilities"], undefined);
  assert.doesNotThrow(() => validateAgentDefinition(resolveFixture().definition));
});

test("the trace names every operation the cascade performed, in order", () => {
  const r = resolveFixture();
  assert.deepEqual(
    r.trace.map((t) => `${t.layer}|${t.kind}|${"slug" in t ? t.slug : ""}`),
    [
      "default|add|orchestrator",
      "default|add|reserve",
      "default|add|worker",
      "user:stdray|update|orchestrator",
      "user:stdray|remove|reserve",
      "user:stdray|update|worker",
      "project:petbox|update|orchestrator",
      "project:petbox|add|review",
      "project:petbox|update|worker",
    ],
  );
  assert.match(formatCascadeTrace(r), /user:stdray: − reserve \(tombstone\)/);
  assert.match(formatCascadeProvenance(r), /orchestrator {2}tier=orchestrator/);
});

test("the resolved document names the layers it came from, lowest first", () => {
  assert.equal(resolveFixture().definition.name, "default < user:stdray < project:petbox");
});

test("the package fixture is byte-identical to the research prototype it was copied from", () => {
  for (const layer of ["base", "user", "project"]) {
    const names = readdirSync(join(FIXTURE, layer)).sort();
    assert.deepEqual(names, readdirSync(join(RESEARCH, layer)).sort(), `${layer}: file set drifted`);
    for (const name of names) {
      assert.deepEqual(
        readFileSync(join(FIXTURE, layer, name)),
        readFileSync(join(RESEARCH, layer, name)),
        `${layer}/${name} drifted from the research prototype`,
      );
    }
  }
});

// ── Negative cases (RESOLVED.md ran these on a COPY; here they are permanent) ────────────────

test("E2: a tombstone for a role no lower layer defines", () => {
  const { root, dirs } = copyFixture();
  writeFileSync(
    join(root, "user", "petbox-ghost.json"),
    JSON.stringify({ slug: "ghost", removed: true }),
    "utf8",
  );
  const errors = cascadeErrors(resolveDefinitionLayers(dirs));
  const e2 = errors.filter((e) => e.code === "E2");
  assert.equal(e2.length, 1);
  assert.match(e2[0]?.message ?? "", /tombstone for role "ghost"/);
  rmSync(root, { recursive: true, force: true });
});

test("E4: one layer both replacing and appending the same role's prose — the append is dropped", () => {
  const { root, dirs } = copyFixture();
  writeFileSync(join(root, "user", "petbox-orchestrator.append.md"), "orphan paragraph", "utf8");
  const r = resolveDefinitionLayers(dirs);
  const e4 = cascadeErrors(r).filter((e) => e.code === "E4");
  assert.equal(e4.length, 1);
  assert.match(e4[0]?.message ?? "", /both REPLACES prose .* and APPENDS/);
  assert.doesNotMatch(
    r.definition.roles.find((x) => x.slug === "orchestrator")?.notes ?? "",
    /orphan paragraph/,
    "the append survived its own layer's replacement — that is the orphan paragraph E4 exists to stop",
  );
  rmSync(root, { recursive: true, force: true });
});

test("E5: a filename off the schema, and a slug that disagrees with its filename", () => {
  const { root, dirs } = copyFixture();
  writeFileSync(join(root, "project", "reviewer.json"), "{}", "utf8");
  writeFileSync(
    join(root, "project", "petbox-review.json"),
    JSON.stringify({ slug: "reviewer", tier: "worker", requiredCapabilities: [] }),
    "utf8",
  );
  const e5 = cascadeErrors(resolveDefinitionLayers(dirs)).filter((e) => e.code === "E5");
  assert.equal(e5.length, 2);
  assert.ok(e5.some((e) => /filename does not follow/.test(e.message)));
  assert.ok(e5.some((e) => /"slug" is "reviewer" but the filename says "review"/.test(e.message)));
  rmSync(root, { recursive: true, force: true });
});

test("E3: a new role that no lower layer completes", () => {
  const { root, dirs } = copyFixture();
  writeFileSync(
    join(root, "project", "petbox-auditor.json"),
    JSON.stringify({ slug: "auditor", spawn: { allowed: false } }),
    "utf8",
  );
  writeFileSync(join(root, "project", "petbox-scribe.md"), "prose with no json", "utf8");
  const e3 = cascadeErrors(resolveDefinitionLayers(dirs)).filter((e) => e.code === "E3");
  assert.equal(e3.length, 2);
  assert.ok(e3.some((e) => /"auditor" is incomplete, missing: tier, requiredCapabilities/.test(e.message)));
  assert.ok(e3.some((e) => /"scribe" has prose .* but no petbox-scribe\.json/.test(e.message)));
  rmSync(root, { recursive: true, force: true });
});

test("E0: an unknown layer mode is named, and the layer is then treated as an overlay", () => {
  const { root, dirs } = copyFixture();
  writeFileSync(
    join(root, "user", "layer.json"),
    JSON.stringify({ name: "user:stdray", mode: "merge" }),
    "utf8",
  );
  const r = resolveDefinitionLayers(dirs);
  const e0 = cascadeErrors(r).filter((e) => e.code === "E0");
  assert.equal(e0.length, 1);
  assert.match(e0[0]?.message ?? "", /unknown mode "merge"/);
  // Treated as overlay: the tombstone still applied.
  assert.ok(!r.definition.roles.some((x) => x.slug === "reserve"));
  rmSync(root, { recursive: true, force: true });
});

test("W3: a layer that sets a field to the value it already had, and one that changes nothing", () => {
  const { root, dirs } = copyFixture();
  // Same value as the basis → the patch is a no-op.
  writeFileSync(
    join(root, "user", "petbox-worker.json"),
    JSON.stringify({ slug: "worker", escalation: { available: true, targets: ["reserve"] } }),
    "utf8",
  );
  const w3 = cascadeWarnings(resolveDefinitionLayers(dirs)).filter((w) => w.code === "W3");
  assert.ok(
    w3.some((w) => /worker\.escalation is set to the value it already had/.test(w.message)),
    `expected a no-op W3, got: ${w3.map((w) => w.message).join(" | ")}`,
  );
  rmSync(root, { recursive: true, force: true });
});

test("W3: prose byte-identical to the layer below is a REPLICA, not a layer", () => {
  const { root, dirs } = copyFixture();
  cpSync(join(root, "base", "petbox-orchestrator.md"), join(root, "user", "petbox-orchestrator.md"));
  const w3 = cascadeWarnings(resolveDefinitionLayers(dirs)).filter((w) => w.code === "W3");
  assert.ok(w3.some((w) => /byte-identical to the layer below/.test(w.message)));
  rmSync(root, { recursive: true, force: true });
});

test("mode=replace drops every role below it, and the trace says which ones", () => {
  const { root, dirs } = copyFixture();
  writeFileSync(
    join(root, "project", "layer.json"),
    JSON.stringify({ name: "project:petbox", mode: "replace" }),
    "utf8",
  );
  const r = resolveDefinitionLayers(dirs);
  // project/ only completes `review`; orchestrator's and worker's patches have nothing under
  // them any more, so the roster collapses to the one complete role the layer declares itself.
  assert.deepEqual(r.definition.roles.map((x) => x.slug), ["review"]);
  const reset = r.trace.find((t) => t.kind === "reset");
  assert.deepEqual(reset?.kind === "reset" ? reset.dropped : null, ["orchestrator", "worker"]);
  rmSync(root, { recursive: true, force: true });
});

// ── D15: a broken source fails LOUD, it never degrades to something that still works ────────

test("a layer whose JSON is broken throws, naming the file and the parser's position", () => {
  const { root, dirs } = copyFixture();
  writeFileSync(join(root, "user", "petbox-worker.json"), '{"slug": "worker",,}', "utf8");
  assert.throws(
    () => resolveDefinitionLayers(dirs),
    (err: unknown) => {
      assert.ok(err instanceof LayerSourceError, "a broken layer must be a LayerSourceError");
      assert.match(err.message, /petbox-worker\.json is not valid JSON/);
      assert.match(err.message, /position \d+/, "the parser position must survive into the message");
      return true;
    },
  );
  rmSync(root, { recursive: true, force: true });
});

test("a directory with no layer.json is not a layer, and says so", () => {
  const { root, dirs } = copyFixture();
  rmSync(join(root, "user", "layer.json"));
  assert.throws(() => resolveDefinitionLayers(dirs), /has no layer\.json/);
  rmSync(root, { recursive: true, force: true });
});

test("a layer directory that does not exist throws instead of resolving without it", () => {
  assert.throws(
    () => resolveDefinitionLayers([join(FIXTURE, "base"), join(FIXTURE, "nope")]),
    /does not exist or is not a directory/,
  );
});
