// Protocol inject: memory always; spawn prescriptions when harness has spawn_subagents.
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

test("orchestrationPrescriptionsAllowed tracks spawn_subagents for all three harnesses", () => {
  assert.equal(orchestrationPrescriptionsAllowed("claude-code"), true);
  assert.equal(orchestrationPrescriptionsAllowed("opencode"), true);
  // Factory Task tool on main session — https://docs.factory.ai/cli/configuration/custom-droids
  assert.equal(orchestrationPrescriptionsAllowed("droid"), true);
  assert.equal(orchestrationPrescriptionsAllowed(undefined), false);
  assert.equal(orchestrationPrescriptionsAllowed("unknown"), false);
});

test("buildProtocol for droid CONTAINS spawn/orchestrator prose (Factory spawns via Task)", () => {
  const text = buildProtocol(project, mcpPetboxTool, { harness: "droid" });
  const lower = text.toLowerCase();
  assert.ok(
    lower.includes("spawn workers") || lower.includes("delegate by default"),
    `droid protocol must prescribe spawn/delegate:\n${text}`,
  );
  assert.match(text, /orchestrator/i);
  assert.match(text, /PetBox memory active/);
  assert.match(text, /search before rework/i);
  // Must not fall back to "· main" no-spawn self-intro
  assert.ok(!lower.includes("· main"), `droid must not use no-spawn main intro:\n${text}`);
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
    assert.match(text, /plan, decompose, delegate|Orchestrator notes/i);
  }
});

test("buildProtocol without harness omits spawn prescriptions (safe default)", () => {
  const text = buildProtocol(project, mcpPetboxTool);
  const lower = text.toLowerCase();
  assert.ok(!lower.includes("spawn workers"));
  assert.ok(!lower.includes("delegate by default"));
});
