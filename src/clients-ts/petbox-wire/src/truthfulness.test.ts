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
import { hasPetboxMarker } from "./origin-marker.ts";
import { HARNESS_IDS, harnessCapabilities, hasCapability } from "./harness-capabilities.ts";
import { allowedModels, isResolvableModel } from "./harness-models.ts";
import {
  checkTruthfulness,
  formatViolations,
  isModelViolation,
  type ModelViolation,
} from "./truthfulness.ts";
import { classifyApplyExit, WIRE_EXIT } from "./wire-exit.ts";

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
  assert.ok(cc.files.some((f) => f.relativePath === ".claude/agents/petbox-worker.md"));
  // Namespacing rename: the pre-prefix name is carried alongside so the writer can clean up
  // an OWNED leftover from before the rename (never a foreign file — see apply-write.ts).
  assert.ok(
    cc.files.some((f) => f.legacyRelativePath === ".claude/agents/worker.md"),
    "legacyRelativePath must point at the bare pre-namespacing name",
  );

  const oc = planApply(portable, "opencode", {});
  assert.equal(oc.violations.length, 0);
  assert.ok(oc.files.every((f) => f.relativePath.startsWith(".opencode/agent/")));
  assert.ok(oc.files.some((f) => f.relativePath === ".opencode/agent/petbox-worker.md"));

  const dr = planApply(portable, "droid", {});
  assert.equal(dr.violations.length, 0);
  assert.ok(dr.files.every((f) => f.relativePath.startsWith(".factory/droids/")));
  assert.ok(dr.files.some((f) => f.relativePath === ".factory/droids/petbox-worker.md"));
  assert.ok(dr.files.some((f) => f.legacyRelativePath === ".factory/droids/worker.md"));
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
  assert.match(orch!.content, /^---\nname: petbox-orchestrator\n/m);
  assert.match(orch!.content, /model: custom:deepseek-v4-pro/);
  assert.match(orch!.content, /mcpServers: \["petbox"\]/);
  assert.ok(hasPetboxMarker(orch!.content), "every generated file carries the origin marker");
});

test("renderDroidMarkdown: unbound model is inherit; bound uses roles.json value", () => {
  const role = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === "worker")!;
  const inherit = renderDroidMarkdown(role);
  assert.match(inherit, /name: petbox-worker/);
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

test("planApply: bound model in claude-code frontmatter; unbound omits model (+warns)", () => {
  const portable: AgentDefinition = {
    name: "p",
    roles: [{ slug: "worker", tier: "worker", requiredCapabilities: [] }],
  };
  // Must be an id Claude Code actually resolves — the old fixture used a provider-prefixed
  // "anthropic/claude-sonnet-4", which is exactly the shape the model gate now rejects.
  const withModel = planApply(portable, "claude-code", { worker: "sonnet" });
  assert.equal(withModel.violations.length, 0);
  const worker = withModel.files.find((f) => f.relativePath.endsWith("worker.md"));
  assert.ok(worker);
  assert.match(worker!.content, /^---\nname: petbox-worker\nmodel: sonnet\n/m);
  assert.deepEqual(withModel.warnings, []);

  const unbound = planApply(portable, "claude-code", {});
  const body = unbound.files[0]!.content;
  assert.match(body, /^---\nname: petbox-worker\n/m, "name: is always emitted");
  assert.ok(!/^model:/m.test(body.split("---")[1] ?? ""), "no invented model");
  // Unbound is legal (inherit) but must not be silent.
  assert.equal(unbound.violations.length, 0);
  assert.equal(unbound.warnings.length, 1);
  assert.match(unbound.warnings[0]!, /inherit the session\/parent model/);
});

test("model gate: claude-code role bound to a droid id is BLOCKED (not written)", () => {
  const portable: AgentDefinition = {
    name: "p",
    roles: [
      { slug: "worker", tier: "worker", requiredCapabilities: [] },
      { slug: "utility", tier: "utility", requiredCapabilities: [] },
    ],
  };
  // The 2026-07-12 incident: droid ids copied into the claude-code block of roles.json.
  const plan = planApply(portable, "claude-code", {
    worker: "custom:DeepSeek-V4-Pro-0",
    utility: "haiku",
  });

  assert.deepEqual(plan.skippedRoles, ["worker"]);
  assert.ok(
    !plan.files.some((f) => f.relativePath.endsWith("worker.md")),
    "an unresolvable model must never reach .claude/agents/*.md",
  );
  assert.ok(plan.files.some((f) => f.relativePath.endsWith("utility.md")));

  assert.equal(plan.violations.length, 1);
  const v = plan.violations[0]!;
  assert.ok(isModelViolation(v));
  assert.equal(v.role, "worker");
  assert.equal(v.harness, "claude-code");
  assert.equal(v.model, "custom:DeepSeek-V4-Pro-0");
  assert.ok(v.allowedModels.includes("sonnet"));

  const msg = formatApplyBlocked(plan.violations, plan.harness, plan.skippedRoles);
  assert.match(msg, /worker/);
  assert.match(msg, /custom:DeepSeek-V4-Pro-0/);
  assert.match(msg, /claude-code/);
  assert.match(msg, /SILENTLY inherit the session model/);
  assert.match(msg, /Allowed: .*sonnet/);

  // apply's exit contract: a blocked role ⇒ non-zero (3), same as a capability violation.
  const hadTruthfulnessBlock = plan.violations.length > 0;
  assert.equal(classifyApplyExit({ hadTruthfulnessBlock }), WIRE_EXIT.truthfulness);
  assert.notEqual(WIRE_EXIT.truthfulness, 0);
});

test("model gate: every claude-code alias in the live roster resolves; junk does not", () => {
  // .claude/agents/*.md of this repo (verified 2026-07-12): opus/sonnet/haiku/fable.
  for (const m of ["opus", "sonnet", "haiku", "fable", "inherit", "OPUS"]) {
    assert.equal(isResolvableModel("claude-code", m), true, `${m} must resolve`);
  }
  // Concrete Anthropic ids (claude-api canon), incl. the 1M-context suffix form.
  for (const m of ["claude-opus-4-8", "claude-sonnet-5", "claude-opus-4-8[1m]"]) {
    assert.equal(isResolvableModel("claude-code", m), true, `${m} must resolve`);
  }
  for (const m of [
    "custom:DeepSeek-V4-Pro-0",
    "custom:Qwen3.7-Max-[1M-ctx-·-orchestrator]-0",
    "deepseek/deepseek-v4-pro",
    "anthropic/claude-sonnet-4",
    "claude-sonnet-4.6", // typo'd id
    "gpt-5",
  ]) {
    assert.equal(isResolvableModel("claude-code", m), false, `${m} must NOT resolve`);
  }
  assert.ok(allowedModels("claude-code")!.length > 0);
});

test("model gate: droid/opencode id spaces stay open (no invented allow-list)", () => {
  // Both resolve ids against a local/provider registry — the kit makes no claim, so a real
  // binding like custom:DeepSeek-V4-Pro-0 or deepseek/deepseek-v4-pro must NOT be blocked.
  assert.equal(allowedModels("droid"), null);
  assert.equal(allowedModels("opencode"), null);
  assert.equal(isResolvableModel("droid", "custom:DeepSeek-V4-Pro-0"), true);
  assert.equal(isResolvableModel("opencode", "deepseek/deepseek-v4-pro"), true);

  const droid = planApply(DEFAULT_AGENT_DEFINITION, "droid", {
    worker: "custom:DeepSeek-V4-Pro-0",
  });
  assert.equal(droid.violations.length, 0);
  const oc = planApply(DEFAULT_AGENT_DEFINITION, "opencode", {
    worker: "deepseek/deepseek-v4-pro",
  });
  assert.equal(oc.violations.length, 0);
});

test("checkTruthfulness (doctor path) also gates the local model binding", () => {
  const clean = checkTruthfulness(DEFAULT_AGENT_DEFINITION, "claude-code", {
    orchestrator: "opus",
    worker: "sonnet",
    utility: "haiku",
    reserve: "fable",
    explore: "haiku",
  });
  assert.deepEqual(clean, [], formatViolations(clean));

  const dirty = checkTruthfulness(DEFAULT_AGENT_DEFINITION, "claude-code", {
    worker: "custom:DeepSeek-V4-Pro-0",
  });
  assert.equal(dirty.length, 1);
  assert.ok(isModelViolation(dirty[0]!));
  assert.equal((dirty[0] as ModelViolation).role, "worker");
});

test("planOpencodeApply: bound model in frontmatter; unbound omits model", () => {
  const withModel = planOpencodeApply(DEFAULT_AGENT_DEFINITION, {
    worker: "deepseek/deepseek-v4-pro",
  });
  assert.equal(withModel.violations.length, 0);
  const worker = withModel.files.find((f) => f.relativePath.endsWith("worker.md"));
  assert.ok(worker);
  assert.match(worker!.content, /^---\nname: petbox-worker\nmodel: deepseek\/deepseek-v4-pro\n/m);
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
  assert.match(md, /^---\nname: petbox-worker\n/, "name: must be the first frontmatter key, namespaced");
  // No tools: key — omission means the file inherits the harness's full tool set,
  // including MCP (that is the intended policy; see harness-capabilities.ts).
  assert.ok(!/^tools:/m.test(md), "must not emit a tools: key");
  assert.ok(hasPetboxMarker(md), "generated file must carry the origin marker");
});

test("emitted agent names are namespaced petbox-<slug> across the whole default roster, every harness", () => {
  // chore: petbox-namespaced-agent-names — role.slug (internal) stays bare; only the render
  // is prefixed. Assert it holds for every role x harness, not just worker.
  for (const harness of HARNESS_IDS) {
    const plan = planApply(DEFAULT_AGENT_DEFINITION, harness, {});
    assert.equal(plan.violations.length, 0, formatViolations(plan.violations));
    for (const role of DEFAULT_AGENT_DEFINITION.roles) {
      const file = plan.files.find((f) => f.relativePath.includes(`petbox-${role.slug}`));
      assert.ok(file, `${harness}: expected a petbox-${role.slug} file`);
      assert.match(file!.content, new RegExp(`name: petbox-${role.slug}\\b`));
      assert.ok(hasPetboxMarker(file!.content));
      // Never emit the bare, unprefixed name as the CURRENT (non-legacy) path.
      assert.ok(
        !plan.files.some((f) => f.relativePath.endsWith(`/${role.slug}.md`)),
        `${harness}: must not also emit an unprefixed ${role.slug}.md as a current artifact`,
      );
    }
  }
});

test("orchestrator body's spawn/escalation prose names the NAMESPACED target roles, not bare slugs", () => {
  // protocol.ts:62-style bug, generalized: any prose naming a spawn/escalation target must
  // render the computed identity — never role.slug directly — or a generated file points at
  // a subagent_type that does not exist on disk.
  const orchestrator = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === "orchestrator")!;
  const md = renderAgentMarkdown(orchestrator);
  assert.match(md, /Target roles:.*`petbox-worker`/);
  assert.match(md, /Target roles:.*`petbox-utility`/);
  assert.ok(!/Target roles:.*`worker`[,.]/.test(md), "must not list the bare slug");

  const worker = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === "worker")!;
  const workerMd = renderAgentMarkdown(worker);
  assert.match(workerMd, /Escalation[\s\S]*`petbox-orchestrator`/);
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

test("DEFAULT reserve notes: offline fallback does not invert the live semantics (bug @80 item 5)", () => {
  // The offline fallback once taught the OPPOSITE rule: "Heavy reasoning / architecture
  // review / stuck points only" reads as "call reserve when the work is heavy" — the live
  // server definition says the exact reverse (hard work is a model escalation on a worker;
  // reserve is for being STUCK, never merely for difficulty). Guard both directions so a
  // hand-copied fallback cannot silently drift back into the inverted phrasing.
  const reserve = DEFAULT_AGENT_DEFINITION.roles.find((r) => r.slug === "reserve")!;
  const notes = reserve.notes ?? "";
  assert.match(notes, /STUCK/, "reserve notes must state the stuck-trigger explicitly");
  assert.ok(
    !/heavy reasoning/i.test(notes),
    "must not reintroduce 'heavy reasoning ... only' framing (the inverted rule)",
  );
  assert.ok(
    /not merely when the work is hard/i.test(notes),
    "must explicitly rule out 'hard work' as a trigger for reserve",
  );
  // requiredCapabilities must target the subagent surface, not mcp_main_session — reserve
  // exists only as a subagent (finding 4 of the same node, closed alongside this fallback).
  assert.deepEqual([...reserve.requiredCapabilities], ["mcp_subagent"]);
});
