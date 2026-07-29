// The CANONICAL agent roster, as the kit sees it (work seed-agent-def-on-project-create).
//
// src/common/default-agents.json is one file with two readers: the PetBox server embeds it and
// seeds it into every project it creates, and this kit copies it into its own package and exports
// it as DEFAULT_AGENT_DEFINITION — the OFFLINE fallback used when PetBox is unreachable and no LKG
// cache exists. There is no "is the kit's copy the same as the server's copy" test anywhere,
// because there is no second copy to compare; what is worth testing is that the single source is
// SOUND and that the offline path can actually reach it.
//
// Validation reuses this module's own validateAgentDefinition — deliberately not a second
// validator written here. The checks below it are the ones that validator does not make
// (referential integrity of spawn/escalation targets, prose actually present), mirroring
// DefaultAgentDefinition.Validate on the C# side.
//
// Run: node --test src/default-agents-baseline.test.ts

import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { test } from "node:test";
import { DEFAULT_AGENT_DEFINITION, validateAgentDefinition } from "./agent-definition.ts";

const EXPECTED_SLUGS = [
  "orchestrator",
  "worker",
  "worker-highstakes",
  "utility",
  "reserve",
  "explore",
];

test("the baseline JSON sits INSIDE the package, next to the module that reads it", () => {
  // Load-bearing for the kit's contract: the offline fallback is used precisely when there is no
  // network, so it must be a file on disk that `files: ["bin", "src", ...]` puts in the tarball —
  // never something fetched. A relative path outside the package would publish an empty kit.
  const path = join(import.meta.dirname, "default-agents.json");
  assert.ok(existsSync(path), `${path} must exist — run \`npm run sync-default-agents\``);

  const parsed = JSON.parse(readFileSync(path, "utf8"));
  assert.deepEqual(parsed, DEFAULT_AGENT_DEFINITION, "the export must BE the file, not a transcription of it");
});

test("the baseline passes the kit's own validateAgentDefinition", () => {
  validateAgentDefinition(DEFAULT_AGENT_DEFINITION);
});

test("every expected role is present, exactly once", () => {
  const slugs = DEFAULT_AGENT_DEFINITION.roles.map((r) => r.slug);
  assert.deepEqual([...slugs].sort(), [...EXPECTED_SLUGS].sort());
  assert.equal(new Set(slugs).size, slugs.length, "role slugs must be unique");
});

test("every role carries a tier, a capability list and real prose", () => {
  for (const role of DEFAULT_AGENT_DEFINITION.roles) {
    assert.ok(role.tier.trim().length > 0, `role '${role.slug}' has no tier`);
    assert.ok(Array.isArray(role.requiredCapabilities), `role '${role.slug}' has no requiredCapabilities`);
    assert.ok(
      typeof role.notes === "string" && role.notes.trim().length > 0,
      `role '${role.slug}' has no notes — apply would render an empty agent artifact`,
    );
  }
});

test("spawn and escalation targets name roles that exist in this document", () => {
  const slugs = new Set(DEFAULT_AGENT_DEFINITION.roles.map((r) => r.slug));
  for (const role of DEFAULT_AGENT_DEFINITION.roles) {
    for (const target of role.spawn?.allowedRoles ?? []) {
      assert.ok(slugs.has(target), `role '${role.slug}' may spawn '${target}', which is not a role here`);
    }
    for (const target of role.escalation?.targets ?? []) {
      assert.ok(slugs.has(target), `role '${role.slug}' escalates to '${target}', which is not a role here`);
    }
  }
});

test("the document carries no model binding anywhere (binding is local, in roles.json)", () => {
  // validateAgentDefinition already walks the tree for this; the raw-text assertion catches a
  // `model` hidden somewhere the typed walk would not visit.
  const raw = readFileSync(join(import.meta.dirname, "default-agents.json"), "utf8");
  assert.ok(!raw.includes('"model"'), "a portable roster must not carry a model field");
});
