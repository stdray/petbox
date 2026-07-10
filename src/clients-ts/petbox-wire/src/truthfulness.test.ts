// Unit tests for the definition truthfulness gate + default agent definition.
//
// Run: node --test src/truthfulness.test.ts   (Node >= 23.6 native TS)

import assert from "node:assert/strict";
import { test } from "node:test";
import {
  DEFAULT_AGENT_DEFINITION,
  validateAgentDefinition,
  type AgentDefinition,
} from "./agent-definition.ts";
import {
  formatApplyBlocked,
  planOpencodeApply,
  renderOpencodeAgentMarkdown,
} from "./apply-artifacts.ts";
import { HARNESS_IDS, harnessCapabilities, hasCapability } from "./harness-capabilities.ts";
import { checkTruthfulness, formatViolations } from "./truthfulness.ts";

test("claude-code declares mcp_main_session and dynamic_model_at_spawn, not mcp_subagent", () => {
  const caps = harnessCapabilities("claude-code");
  assert.equal(caps.has("mcp_main_session"), true);
  assert.equal(caps.has("dynamic_model_at_spawn"), true);
  assert.equal(caps.has("builtin_explore_inherits_model"), true);
  assert.equal(caps.has("hooks"), true);
  assert.equal(caps.has("mcp_subagent"), false);
  assert.equal(caps.has("role_files"), false);
});

test("opencode declares role_files + mcp_subagent, not dynamic_model_at_spawn", () => {
  const caps = harnessCapabilities("opencode");
  assert.equal(caps.has("role_files"), true);
  assert.equal(caps.has("mcp_subagent"), true);
  assert.equal(caps.has("builtin_explore_inherits_model"), true);
  assert.equal(caps.has("dynamic_model_at_spawn"), false);
});

test("droid declares hooks + mcp_main_session (gated surface)", () => {
  assert.equal(hasCapability("droid", "hooks"), true);
  assert.equal(hasCapability("droid", "mcp_main_session"), true);
  assert.equal(hasCapability("droid", "dynamic_model_at_spawn"), false);
});

test("role requiring mcp_subagent on claude-code → violation", () => {
  const def: AgentDefinition = {
    name: "t",
    roles: [
      {
        slug: "worker",
        tier: "worker",
        requiredCapabilities: ["mcp_subagent"],
      },
    ],
  };
  const v = checkTruthfulness(def, "claude-code");
  assert.equal(v.length, 1);
  assert.deepEqual(v[0], {
    role: "worker",
    capability: "mcp_subagent",
    harness: "claude-code",
  });
  assert.match(formatViolations(v), /mcp_subagent/);
});

test("role only needing declared caps → ok (empty violations)", () => {
  const def: AgentDefinition = {
    name: "t",
    roles: [
      {
        slug: "orchestrator",
        tier: "orchestrator",
        requiredCapabilities: ["mcp_main_session", "hooks"],
      },
    ],
  };
  assert.deepEqual(checkTruthfulness(def, "claude-code"), []);
  assert.deepEqual(checkTruthfulness(def, "droid"), []);
});

test("default definition is truth-clean on every known harness", () => {
  validateAgentDefinition(DEFAULT_AGENT_DEFINITION);
  for (const h of HARNESS_IDS) {
    const v = checkTruthfulness(DEFAULT_AGENT_DEFINITION, h);
    assert.deepEqual(v, [], `default definition must pass on ${h}: ${formatViolations(v)}`);
  }
  const explore = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === "explore");
  assert.ok(explore, "default roster includes explore");
  assert.match(explore!.notes ?? "", /builtin_explore_inherits_model|inheritance/i);
  assert.ok(
    !(explore!.notes ?? "").toLowerCase().includes("inheritance forbidden"),
    "explore notes must not claim inheritance forbidden",
  );
});

test("planOpencodeApply: bound model in frontmatter; unbound omits model", () => {
  const withModel = planOpencodeApply(DEFAULT_AGENT_DEFINITION, {
    worker: "deepseek/deepseek-v4-pro",
  });
  assert.equal(withModel.violations.length, 0);
  const worker = withModel.files.find((f) => f.relativePath.endsWith("worker.md"));
  assert.ok(worker);
  assert.match(worker!.content, /^---\nmodel: deepseek\/deepseek-v4-pro\n/m);
  assert.match(worker!.content, /# worker/);

  const unbound = planOpencodeApply(DEFAULT_AGENT_DEFINITION, {});
  const orch = unbound.files.find((f) => f.relativePath.endsWith("orchestrator.md"));
  assert.ok(orch);
  assert.ok(!/^model:/m.test(orch!.content.split("---")[1] ?? ""), "no invented model");
  // description-only frontmatter is fine
  assert.match(orch!.content, /^---\n/);
});

test("planOpencodeApply: violations block files and formatApplyBlocked is loud", () => {
  const def: AgentDefinition = {
    name: "bad",
    roles: [
      {
        slug: "worker",
        tier: "worker",
        // dynamic_model_at_spawn is a CC capability, not opencode
        requiredCapabilities: ["dynamic_model_at_spawn"],
      },
    ],
  };
  const plan = planOpencodeApply(def, {});
  assert.equal(plan.files.length, 0);
  assert.equal(plan.violations.length, 1);
  const msg = formatApplyBlocked(plan.violations, "opencode");
  assert.match(msg, /refusing to write/);
  assert.match(msg, /dynamic_model_at_spawn/);
});

test("renderOpencodeAgentMarkdown explore body does not forbid inheritance", () => {
  const explore = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === "explore")!;
  const md = renderOpencodeAgentMarkdown(explore);
  assert.match(md, /Model inheritance/i);
  assert.ok(!md.toLowerCase().includes("inheritance forbidden"));
});

test("validateAgentDefinition rejects role.model", () => {
  assert.throws(
    () =>
      validateAgentDefinition({
        name: "x",
        roles: [
          {
            slug: "w",
            tier: "worker",
            requiredCapabilities: [],
            // @ts-expect-error intentional
            model: "should-not-be-here",
          },
        ],
      }),
    /model is not allowed/,
  );
});
