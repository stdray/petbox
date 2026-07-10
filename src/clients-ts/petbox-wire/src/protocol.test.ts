// Protocol inject: memory always; spawn prescriptions only when harness has spawn_subagents.
//
// Run: node --test src/protocol.test.ts

import assert from "node:assert/strict";
import { test } from "node:test";
import {
  buildProtocol,
  mcpPetboxTool,
  orchestrationPrescriptionsAllowed,
} from "./protocol.ts";

const project = "demo";

test("orchestrationPrescriptionsAllowed tracks spawn_subagents", () => {
  assert.equal(orchestrationPrescriptionsAllowed("claude-code"), true);
  assert.equal(orchestrationPrescriptionsAllowed("opencode"), true);
  assert.equal(orchestrationPrescriptionsAllowed("droid"), false);
  assert.equal(orchestrationPrescriptionsAllowed(undefined), false);
  assert.equal(orchestrationPrescriptionsAllowed("unknown"), false);
});

test("buildProtocol for droid MUST NOT contain spawn-by-default prose", () => {
  const text = buildProtocol(project, mcpPetboxTool, { harness: "droid" });
  const lower = text.toLowerCase();
  assert.ok(!lower.includes("spawn workers"), `droid protocol must not SPAWN workers:\n${text}`);
  assert.ok(
    !lower.includes("delegate by default"),
    `droid protocol must not delegate by DEFAULT:\n${text}`,
  );
  assert.ok(!lower.includes("fan-out is default"), `droid must not claim fan-out default:\n${text}`);
  // Memory protocol still present.
  assert.match(text, /search before rework/i);
  assert.match(text, /PetBox memory active/);
});

test("buildProtocol for claude-code / opencode MUST contain spawn/delegate language", () => {
  for (const harness of ["claude-code", "opencode"] as const) {
    const text = buildProtocol(project, mcpPetboxTool, { harness });
    const lower = text.toLowerCase();
    assert.ok(
      lower.includes("spawn workers") || lower.includes("delegate by default"),
      `${harness} protocol must prescribe spawn/delegate:\n${text}`,
    );
    assert.match(text, /orchestrator/i);
    // Derived from definition notes when spawn allowed.
    assert.match(text, /plan, decompose, delegate|Orchestrator notes/i);
  }
});

test("buildProtocol without harness omits spawn prescriptions (safe default)", () => {
  const text = buildProtocol(project, mcpPetboxTool);
  const lower = text.toLowerCase();
  assert.ok(!lower.includes("spawn workers"));
  assert.ok(!lower.includes("delegate by default"));
});
