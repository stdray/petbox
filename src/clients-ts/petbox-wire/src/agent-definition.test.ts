// Unit tests for diffAgentDefinitions (bug: doctor-drift-conflates-degradation-and-divergence).
//
// The built-in DEFAULT_AGENT_DEFINITION is an offline bootstrap minimum, not a mirror of the live
// server document — so "live has a role built-in doesn't" is an expected DEGRADATION, not drift.
// Only "both sides have the role but disagree" (or "built-in promises a role live lacks") is real
// DIVERGENCE worth shouting about. These tests exercise diffAgentDefinitions directly (no
// subprocess) so the classification is pinned at the data layer, not just at doctor's console
// formatting — see doctor-definition.test.ts for the end-to-end CLI-level checks.
//
// Run: node --test src/agent-definition.test.ts

import assert from "node:assert/strict";
import { test } from "node:test";
import { diffAgentDefinitions, type AgentDefinition } from "./agent-definition.ts";

const worker: AgentDefinition["roles"][number] = {
  slug: "worker",
  tier: "worker",
  requiredCapabilities: [],
  notes: "1. one\n2. two",
};

function withRoles(...roles: AgentDefinition["roles"][number][]): AgentDefinition {
  return { name: "default", roles };
}

test("live-only role is a degradation, not a divergence (built-in is a bootstrap minimum, not a mirror)", () => {
  const builtin = withRoles(worker);
  const richerLive = withRoles(worker, {
    slug: "worker-highstakes",
    tier: "worker",
    requiredCapabilities: [],
    notes: "1. one",
  });

  const { degradations, divergences } = diffAgentDefinitions(builtin, richerLive);

  assert.equal(divergences.length, 0, "a role only present live must never count as drift");
  assert.equal(degradations.length, 1);
  assert.match(degradations[0]!, /worker-highstakes/);
  assert.match(degradations[0]!, /exists in the live definition but not in the built-in default/);
  // Wording must say the kit is poorer than the server and that this is expected — not an alarm.
  assert.match(degradations[0]!, /offline bootstrap minimum/);
});

test("built-in-only role is still a divergence (the kit promises a role the project doesn't have)", () => {
  const builtin = withRoles(worker, {
    slug: "ghost",
    tier: "utility",
    requiredCapabilities: [],
    notes: "1. one",
  });
  const live = withRoles(worker);

  const { degradations, divergences } = diffAgentDefinitions(builtin, live);

  assert.equal(degradations.length, 0);
  assert.equal(divergences.length, 1);
  assert.match(divergences[0]!, /'ghost'/);
  assert.match(divergences[0]!, /exists in the built-in default but not in the live definition/);
});

test("same rule count, different notes text on a shared role is a divergence", () => {
  const builtin = withRoles(worker);
  const live = withRoles({ ...worker, notes: "1. one (reworded)\n2. two" });

  const { degradations, divergences } = diffAgentDefinitions(builtin, live);

  assert.equal(degradations.length, 0);
  assert.equal(divergences.length, 1);
  assert.match(divergences[0]!, /notes text differs/);
  assert.match(divergences[0]!, /same rule count: 2/);
});

test("differing rule count on a shared role is a divergence", () => {
  const builtin = withRoles(worker);
  const live = withRoles({ ...worker, notes: "1. one\n2. two\n3. three" });

  const { degradations, divergences } = diffAgentDefinitions(builtin, live);

  assert.equal(degradations.length, 0);
  assert.equal(divergences.length, 1);
  assert.match(divergences[0]!, /built-in default has 2 rule\(s\), live definition has 3/);
});

test("identical definitions produce no degradations and no divergences", () => {
  const builtin = withRoles(worker);
  const live = withRoles({ ...worker });

  const { degradations, divergences } = diffAgentDefinitions(builtin, live);

  assert.equal(degradations.length, 0);
  assert.equal(divergences.length, 0);
});
