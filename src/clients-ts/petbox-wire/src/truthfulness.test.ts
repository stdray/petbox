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
  planApply,
  planOpencodeApply,
  renderOpencodeAgentMarkdown,
} from "./apply-artifacts.ts";
import { HARNESS_IDS, harnessCapabilities, hasCapability } from "./harness-capabilities.ts";
import { checkTruthfulness, formatViolations } from "./truthfulness.ts";

test("claude-code declares role_files + spawn_subagents; still no mcp_subagent", () => {
  const caps = harnessCapabilities("claude-code");
  assert.equal(caps.has("mcp_main_session"), true);
  assert.equal(caps.has("dynamic_model_at_spawn"), true);
  assert.equal(caps.has("builtin_explore_inherits_model"), true);
  assert.equal(caps.has("hooks"), true);
  assert.equal(caps.has("role_files"), true);
  assert.equal(caps.has("spawn_subagents"), true);
  assert.equal(caps.has("mcp_subagent"), false);
});

test("opencode declares role_files + mcp_subagent + spawn_subagents, not dynamic_model_at_spawn", () => {
  const caps = harnessCapabilities("opencode");
  assert.equal(caps.has("role_files"), true);
  assert.equal(caps.has("mcp_subagent"), true);
  assert.equal(caps.has("builtin_explore_inherits_model"), true);
  assert.equal(caps.has("spawn_subagents"), true);
  assert.equal(caps.has("dynamic_model_at_spawn"), false);
});

test("droid declares only hooks — no unconditional mcp_main_session", () => {
  assert.equal(hasCapability("droid", "hooks"), true);
  assert.equal(hasCapability("droid", "mcp_main_session"), false);
  assert.equal(hasCapability("droid", "spawn_subagents"), false);
  assert.equal(hasCapability("droid", "dynamic_model_at_spawn"), false);
  const caps = harnessCapabilities("droid");
  assert.deepEqual([...caps], ["hooks"]);
});

test("constructed def requiring missing cap fails with role+capability+harness in message", () => {
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
  const msg = formatViolations(v);
  assert.match(msg, /worker/);
  assert.match(msg, /mcp_subagent/);
  assert.match(msg, /claude-code/);
});

test("orchestrator spawn.allowed implies spawn_subagents violation on droid", () => {
  const def: AgentDefinition = {
    name: "t",
    roles: [
      {
        slug: "orchestrator",
        tier: "orchestrator",
        // spawn.allowed alone is enough — spawn_subagents is implicit
        requiredCapabilities: [],
        spawn: { allowed: true, allowedRoles: ["worker"] },
      },
    ],
  };
  const v = checkTruthfulness(def, "droid");
  assert.ok(v.some((x) => x.role === "orchestrator" && x.capability === "spawn_subagents"));
  assert.match(formatViolations(v), /spawn_subagents/);
  assert.match(formatViolations(v), /droid/);
  // claude-code declares spawn_subagents → clean for this minimal def
  assert.deepEqual(checkTruthfulness(def, "claude-code"), []);
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
  // droid lacks mcp_main_session
  const droidV = checkTruthfulness(def, "droid");
  assert.ok(droidV.some((x) => x.capability === "mcp_main_session"));
});

test("default definition fails droid (no spawn_subagents / mcp_main_session) — intentional honesty", () => {
  validateAgentDefinition(DEFAULT_AGENT_DEFINITION);
  // Harnesses with spawn + mcp (claude-code, opencode) pass.
  for (const h of ["claude-code", "opencode"] as const) {
    const v = checkTruthfulness(DEFAULT_AGENT_DEFINITION, h);
    assert.deepEqual(v, [], `default must pass on ${h}: ${formatViolations(v)}`);
  }
  // droid lacks spawn_subagents and mcp_main_session → DEFAULT fails (not a silent pass).
  const droidV = checkTruthfulness(DEFAULT_AGENT_DEFINITION, "droid");
  assert.ok(droidV.length > 0, "default must fail truthfulness on droid");
  assert.ok(
    droidV.some((x) => x.capability === "spawn_subagents" && x.role === "orchestrator"),
    `expected orchestrator/spawn_subagents on droid: ${formatViolations(droidV)}`,
  );
  assert.ok(
    droidV.some((x) => x.capability === "mcp_main_session"),
    `expected mcp_main_session violation on droid: ${formatViolations(droidV)}`,
  );

  const explore = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === "explore");
  assert.ok(explore, "default roster includes explore");
  assert.match(explore!.notes ?? "", /builtin_explore_inherits_model|inheritance/i);
  assert.ok(
    !(explore!.notes ?? "").toLowerCase().includes("inheritance forbidden"),
    "explore notes must not claim inheritance forbidden",
  );

  // Document: only harnesses missing spawn/mcp fail — not a blanket "all green" lie.
  const failing = HARNESS_IDS.filter(
    (h) => checkTruthfulness(DEFAULT_AGENT_DEFINITION, h).length > 0,
  );
  assert.deepEqual(failing, ["droid"]);
});

test("planApply: paths for claude-code, opencode, droid", () => {
  // Minimal def that is truth-clean on all three (no mcp/spawn requirements).
  const portable: AgentDefinition = {
    name: "portable",
    roles: [
      {
        slug: "worker",
        tier: "worker",
        requiredCapabilities: [],
        spawn: { allowed: false },
      },
    ],
  };
  const cc = planApply(portable, "claude-code", {});
  assert.equal(cc.violations.length, 0);
  assert.ok(cc.files.every((f) => f.relativePath.startsWith(".claude/agents/")));
  assert.ok(cc.files.some((f) => f.relativePath === ".claude/agents/worker.md"));

  const oc = planApply(portable, "opencode", {});
  assert.equal(oc.violations.length, 0);
  assert.ok(oc.files.every((f) => f.relativePath.startsWith(".opencode/agent/")));

  const dr = planApply(portable, "droid", {});
  assert.equal(dr.violations.length, 0);
  assert.ok(dr.files.every((f) => f.relativePath.startsWith(".factory/agents/")));
  assert.ok(dr.files.some((f) => f.relativePath === ".factory/agents/worker.md"));
});

test("planApply: bound model in claude-code frontmatter; unbound omits model", () => {
  const portable: AgentDefinition = {
    name: "p",
    roles: [{ slug: "worker", tier: "worker", requiredCapabilities: [] }],
  };
  const withModel = planApply(portable, "claude-code", {
    worker: "anthropic/claude-sonnet-4",
  });
  assert.equal(withModel.violations.length, 0);
  const worker = withModel.files.find((f) => f.relativePath.endsWith("worker.md"));
  assert.ok(worker);
  assert.match(worker!.content, /^---\nmodel: anthropic\/claude-sonnet-4\n/m);

  const unbound = planApply(portable, "claude-code", {});
  const body = unbound.files[0]!.content;
  assert.ok(!/^model:/m.test(body.split("---")[1] ?? ""), "no invented model");
});

test("planApply: violations block files and formatApplyBlocked is loud", () => {
  const def: AgentDefinition = {
    name: "bad",
    roles: [
      {
        slug: "worker",
        tier: "worker",
        requiredCapabilities: ["dynamic_model_at_spawn"],
      },
    ],
  };
  const plan = planApply(def, "opencode", {});
  assert.equal(plan.files.length, 0);
  assert.equal(plan.violations.length, 1);
  const msg = formatApplyBlocked(plan.violations, "opencode");
  assert.match(msg, /refusing to write/);
  assert.match(msg, /dynamic_model_at_spawn/);
  assert.match(msg, /worker/);
  assert.match(msg, /opencode/);
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
  assert.match(msg, /worker/);
  assert.match(msg, /opencode/);
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
