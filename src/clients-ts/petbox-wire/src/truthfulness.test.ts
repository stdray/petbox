// Unit tests for the definition truthfulness gate + default agent definition + apply plans.
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
  renderAgentMarkdown,
  renderDroidMarkdown,
  renderOpencodeAgentMarkdown,
} from "./apply-artifacts.ts";
import { HARNESS_IDS, harnessCapabilities, hasCapability } from "./harness-capabilities.ts";
import { checkTruthfulness, formatViolations } from "./truthfulness.ts";

test("claude-code declares role_files + spawn_subagents + mcp_subagent (verified live 2026-07-12)", () => {
  const caps = harnessCapabilities("claude-code");
  assert.equal(caps.has("mcp_main_session"), true);
  assert.equal(caps.has("dynamic_model_at_spawn"), true);
  assert.equal(caps.has("builtin_explore_inherits_model"), true);
  assert.equal(caps.has("hooks"), true);
  assert.equal(caps.has("role_files"), true);
  assert.equal(caps.has("spawn_subagents"), true);
  assert.equal(caps.has("mcp_subagent"), true);
});

test("opencode declares role_files + mcp_subagent + spawn_subagents, not dynamic_model_at_spawn", () => {
  const caps = harnessCapabilities("opencode");
  assert.equal(caps.has("role_files"), true);
  assert.equal(caps.has("mcp_subagent"), true);
  assert.equal(caps.has("builtin_explore_inherits_model"), true);
  assert.equal(caps.has("spawn_subagents"), true);
  assert.equal(caps.has("dynamic_model_at_spawn"), false);
});

test("droid matrix from Factory docs: role_files+spawn+mcp+dynamic+hooks; no explore-inherit claim", () => {
  // https://docs.factory.ai/cli/configuration/custom-droids
  // https://docs.factory.ai/cli/configuration/mcp
  const caps = harnessCapabilities("droid");
  for (const c of [
    "mcp_main_session",
    "mcp_subagent",
    "spawn_subagents",
    "role_files",
    "dynamic_model_at_spawn",
    "hooks",
  ] as const) {
    assert.equal(caps.has(c), true, `droid must declare ${c}`);
  }
  assert.equal(
    hasCapability("droid", "builtin_explore_inherits_model"),
    false,
    "do not claim CC-style Explore inherit without separate verification",
  );
});

test("constructed def requiring missing cap fails with role+capability+harness in message", () => {
  const def: AgentDefinition = {
    name: "t",
    roles: [
      {
        slug: "worker",
        tier: "worker",
        // opencode does not declare dynamic_model_at_spawn (PR #18588) — a capability
        // still genuinely absent from a known harness's row, unlike mcp_subagent which
        // claude-code now declares (verified live 2026-07-12).
        requiredCapabilities: ["dynamic_model_at_spawn"],
      },
    ],
  };
  const v = checkTruthfulness(def, "opencode");
  assert.equal(v.length, 1);
  assert.deepEqual(v[0], {
    role: "worker",
    capability: "dynamic_model_at_spawn",
    harness: "opencode",
  });
  const msg = formatViolations(v);
  assert.match(msg, /worker/);
  assert.match(msg, /dynamic_model_at_spawn/);
  assert.match(msg, /opencode/);
});

test("DEFAULT is truth-clean on all known harnesses (including droid)", () => {
  validateAgentDefinition(DEFAULT_AGENT_DEFINITION);
  for (const h of HARNESS_IDS) {
    const v = checkTruthfulness(DEFAULT_AGENT_DEFINITION, h);
    assert.deepEqual(v, [], `default must pass on ${h}: ${formatViolations(v)}`);
  }
  const explore = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === "explore");
  assert.ok(explore);
  assert.ok(!(explore!.notes ?? "").toLowerCase().includes("inheritance forbidden"));
});

test("gate still fires: role needing undeclared cap on a harness that lacks it", () => {
  // opencode does not declare dynamic_model_at_spawn (PR #18588)
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
  const v = checkTruthfulness(def, "opencode");
  assert.equal(v.length, 1);
  assert.equal(v[0]!.capability, "dynamic_model_at_spawn");
  assert.equal(v[0]!.harness, "opencode");
});

test("planApply: paths for claude-code, opencode, droid (.factory/droids)", () => {
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
  assert.ok(cc.files.some((f) => f.relativePath === ".claude/agents/worker.md"));

  const oc = planApply(portable, "opencode", {});
  assert.equal(oc.violations.length, 0);
  assert.ok(oc.files.every((f) => f.relativePath.startsWith(".opencode/agent/")));

  const dr = planApply(portable, "droid", {});
  assert.equal(dr.violations.length, 0);
  assert.ok(dr.files.every((f) => f.relativePath.startsWith(".factory/droids/")));
  assert.ok(dr.files.some((f) => f.relativePath === ".factory/droids/worker.md"));
});

test("planApply DEFAULT writes all three harnesses including droid droids", () => {
  for (const h of HARNESS_IDS) {
    const plan = planApply(DEFAULT_AGENT_DEFINITION, h, {});
    assert.equal(plan.violations.length, 0, formatViolations(plan.violations));
    assert.ok(plan.files.length >= 5, `${h} should emit all default roles`);
  }
  const droid = planApply(DEFAULT_AGENT_DEFINITION, "droid", {
    orchestrator: "custom:deepseek-v4-pro",
  });
  const orch = droid.files.find((f) => f.relativePath.includes("orchestrator"));
  assert.ok(orch);
  assert.match(orch!.content, /^---\nname: orchestrator\n/m);
  assert.match(orch!.content, /model: custom:deepseek-v4-pro/);
  assert.match(orch!.content, /mcpServers: \["petbox"\]/);
});

test("renderDroidMarkdown: unbound model is inherit; bound uses roles.json value", () => {
  const role = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === "worker")!;
  const inherit = renderDroidMarkdown(role);
  assert.match(inherit, /name: worker/);
  assert.match(inherit, /model: inherit/);

  const pinned = renderDroidMarkdown(role, "claude-sonnet-4-5-20250929");
  assert.match(pinned, /model: claude-sonnet-4-5-20250929/);
});

test("planApply: emits clean roles, skips only dirty ones", () => {
  const def: AgentDefinition = {
    name: "mixed",
    roles: [
      {
        slug: "worker",
        tier: "worker",
        requiredCapabilities: [],
        spawn: { allowed: false },
      },
      {
        slug: "needs-dyn",
        tier: "worker",
        requiredCapabilities: ["dynamic_model_at_spawn"],
        spawn: { allowed: false },
      },
    ],
  };
  // opencode lacks dynamic_model_at_spawn
  const plan = planApply(def, "opencode", {});
  assert.equal(plan.files.length, 1);
  assert.ok(plan.files[0]!.relativePath.endsWith("worker.md"));
  assert.deepEqual(plan.skippedRoles, ["needs-dyn"]);
  assert.equal(plan.violations.length, 1);
  assert.match(formatApplyBlocked(plan.violations, "opencode", plan.skippedRoles), /needs-dyn/);
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
  assert.match(worker!.content, /^---\nname: worker\nmodel: anthropic\/claude-sonnet-4\n/m);

  const unbound = planApply(portable, "claude-code", {});
  const body = unbound.files[0]!.content;
  assert.match(body, /^---\nname: worker\n/m, "name: is always emitted");
  assert.ok(!/^model:/m.test(body.split("---")[1] ?? ""), "no invented model");
});

test("planOpencodeApply: bound model in frontmatter; unbound omits model", () => {
  const withModel = planOpencodeApply(DEFAULT_AGENT_DEFINITION, {
    worker: "deepseek/deepseek-v4-pro",
  });
  assert.equal(withModel.violations.length, 0);
  const worker = withModel.files.find((f) => f.relativePath.endsWith("worker.md"));
  assert.ok(worker);
  assert.match(worker!.content, /^---\nname: worker\nmodel: deepseek\/deepseek-v4-pro\n/m);
});

test("renderOpencodeAgentMarkdown explore body does not forbid inheritance", () => {
  const explore = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === "explore")!;
  const md = renderOpencodeAgentMarkdown(explore);
  assert.match(md, /Model inheritance/i);
  assert.ok(!md.toLowerCase().includes("inheritance forbidden"));
});

test("validateAgentDefinition rejects role.model and nested model", () => {
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
  assert.throws(
    () =>
      validateAgentDefinition({
        name: "x",
        // @ts-expect-error intentional
        model: "root-bad",
        roles: [{ slug: "w", tier: "worker", requiredCapabilities: [] }],
      }),
    /model is not allowed/,
  );
  assert.throws(
    () =>
      validateAgentDefinition({
        name: "x",
        roles: [
          {
            slug: "w",
            tier: "worker",
            requiredCapabilities: [],
            spawn: {
              allowed: false,
              // @ts-expect-error intentional
              model: "nested-bad",
            },
          },
        ],
      }),
    /model is not allowed/,
  );
});

test("renderAgentMarkdown: generated claude-code role file carries name: as first frontmatter key", () => {
  const role = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === "worker")!;
  const md = renderAgentMarkdown(role);
  assert.match(md, /^---\nname: worker\n/, "name: must be the first frontmatter key");
  // No tools: key — omission means the file inherits the harness's full tool set,
  // including MCP (that is the intended policy; see harness-capabilities.ts).
  assert.ok(!/^tools:/m.test(md), "must not emit a tools: key");
});

test("renderAgentMarkdown / renderDroidMarkdown: role.notes land in the rendered body", () => {
  const role = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === "orchestrator")!;
  assert.ok(role.notes && role.notes.length > 0);
  const cc = renderAgentMarkdown(role);
  assert.ok(cc.includes(role.notes!), "claude-code/opencode body must include role.notes");
  const droid = renderDroidMarkdown(role);
  assert.ok(droid.includes(role.notes!), "droid body must include role.notes");
});

test("leaf role body states the no-spawn rule imperatively, independent of harness tool grants", () => {
  const worker = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === "worker")!;
  assert.equal(worker.spawn?.allowed, false);
  const md = renderAgentMarkdown(worker);
  assert.match(md, /MUST NOT spawn subagents/i);
});

test("claude-code declares mcp_subagent (verified live 2026-07-12)", () => {
  assert.equal(hasCapability("claude-code", "mcp_subagent"), true);
});
