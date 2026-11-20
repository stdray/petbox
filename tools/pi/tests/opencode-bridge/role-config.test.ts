/**
 * Role → opencode config.
 *
 * Two kinds of fixture here, deliberately:
 * - the real installed role files (`~/.pi/agent/agents/*.md`) prove the parser reads what pi
 *   actually ships, and are skipped (loudly) when this machine has none;
 * - the repo's own copies (`tools/pi/agents/*.md`) are deterministic, so the tool→permission
 *   mapping is tested without depending on a deploy having happened.
 *
 * Measured against opencode 2.3.2, which is why the shapes asserted here are the ones they
 * are: `agents.<name>.system` replaces the base prompt, `agents.<name>.permissions` is an
 * ARRAY of rules (the object form is silently ignored on an agent), and the catch-all rule
 * has to come first.
 */

import assert from "node:assert/strict";
import { existsSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import * as path from "node:path";
import test from "node:test";

import {
	assertAgentParameters,
	buildRoleConfig,
	loadPetboxMcpServer,
	loadRole,
	parseRoleFile,
	permissionBackstop,
	planRoleRun,
	resolveRoleModel,
	roleDirectories,
	rulesForRole,
	THINKING_TO_EFFORT,
	variantFor,
	type RoleDefinition,
} from "../../extensions/opencode-bridge/role-config.ts";
import { buildChildConfig, buildInvocation } from "../../extensions/opencode-bridge/delegate.ts";

const REPO_AGENTS = path.resolve(import.meta.dirname, "../../agents");

function repoRole(name: string): RoleDefinition {
	const file = path.join(REPO_AGENTS, `${name}.md`);
	return parseRoleFile(readFileSync(file, "utf8"), file);
}

function installedRole(name: string): RoleDefinition | undefined {
	try {
		return loadRole(name);
	} catch {
		return undefined;
	}
}

interface RoleConfig {
	model: string;
	permission?: Record<string, string>;
	instructions: string[];
	agents: Record<string, { system: string; description?: string; permissions: Array<{ action: string; resource: string; effect: string }> }>;
	providers?: Record<string, { models: Record<string, { variants: Array<{ id: string; body?: Record<string, unknown> }> }> }>;
	websearch?: { provider: string };
	mcp?: { servers: Record<string, { type: string; url: string; headers?: Record<string, string> }> };
}

function asConfig(value: Record<string, unknown>): RoleConfig {
	return value as unknown as RoleConfig;
}

function effectOf(config: RoleConfig, agent: string, action: string): string | undefined {
	return config.agents[agent]?.permissions?.find((rule) => rule.action === action)?.effect;
}

test("parses a real installed role file into a config", () => {
	const role = installedRole("scout");
	if (!role) {
		console.log(`[role] no installed scout.md; searched ${roleDirectories().join(", ")}`);
		return;
	}
	assert.equal(role.name, "scout");
	assert.match(role.system, /^You are `scout`/, "the body is the system prompt");
	assert.ok(role.tools.includes("read"));
	assert.ok(role.tools.includes("bash"));
	assert.ok(role.tools.length > 5, "comma-separated tool list parsed");
	assert.ok(role.thinking, "thinking level parsed");
	assert.equal(role.skills.length > 0, true);
	assert.equal("prompt_mode" in role, false, "unknown frontmatter keys are ignored");

	const config = asConfig(buildRoleConfig(role, { model: "opencode/hy4-flash-free", thinking: role.thinking }));
	assert.equal(config.agents.scout.system, role.system);
	assert.equal(config.model, "opencode/hy4-flash-free#low", "thinking low becomes the low variant");
	assert.deepEqual(config.instructions, [], "instructions stay empty: the role body replaces the prompt");
});

test("a read-only role gets read tools and nothing else", () => {
	const scout = repoRole("scout");
	const config = asConfig(buildRoleConfig(scout, { model: "opencode/hy4-flash-free" }));
	const rules = config.agents.scout.permissions;

	assert.deepEqual(rules[0], { action: "*", resource: "*", effect: "deny" }, "the catch-all must come first");
	for (const action of ["read", "glob", "grep"]) assert.equal(effectOf(config, "scout", action), "allow");
	for (const action of ["edit", "shell", "subagent", "websearch"]) {
		assert.equal(effectOf(config, "scout", action), "deny", `${action} must stay denied`);
	}
	// scout declares bash/powershell, but the caller did not pass allowShell.
	assert.equal(scout.tools.includes("bash"), true);
});

test("a writer role only writes when the caller allows it", () => {
	const worker = repoRole("worker");
	assert.ok(worker.tools.includes("edit") && worker.tools.includes("write"));

	const denied = asConfig(buildRoleConfig(worker, { model: "opencode/hy4-flash-free" }));
	assert.equal(effectOf(denied, "worker", "edit"), "deny", "the role is a ceiling, not a grant");
	assert.equal(effectOf(denied, "worker", "shell"), "deny");

	const allowed = asConfig(buildRoleConfig(worker, { model: "opencode/hy4-flash-free", allowWrite: true }));
	assert.equal(effectOf(allowed, "worker", "edit"), "allow");
	assert.equal(effectOf(allowed, "worker", "shell"), "deny", "allowing writes must not allow shell");

	const shell = asConfig(buildRoleConfig(worker, { model: "opencode/hy4-flash-free", allowShell: true }));
	assert.equal(effectOf(shell, "worker", "shell"), "allow");
	assert.equal(effectOf(shell, "worker", "edit"), "deny");

	assert.equal(effectOf(allowed, "worker", "subagent"), "deny", "subagents need their own flag");
	const withSub = asConfig(buildRoleConfig(worker, { model: "m", allowSubagent: true }));
	assert.equal(effectOf(withSub, "worker", "subagent"), "allow");
});

test("thinking maps onto a single opencode variant", () => {
	assert.deepEqual(Object.keys(THINKING_TO_EFFORT).sort(), ["high", "low", "max", "medium", "minimal", "off", "xhigh"]);
	assert.equal(variantFor(undefined), undefined);
	assert.deepEqual(variantFor("low"), { id: "low", effort: "low" });
	assert.deepEqual(variantFor("off"), { id: "low", effort: "low" });
	assert.deepEqual(variantFor("minimal"), { id: "low", effort: "low" });
	assert.deepEqual(variantFor("medium"), { id: "medium", effort: "medium" });
	assert.deepEqual(variantFor("xhigh"), { id: "xhigh", effort: "xhigh" });
	assert.deepEqual(variantFor("max"), { id: "max", effort: "max" });
	assert.throws(() => variantFor("turbo"), /unknown thinking level/);

	const scout = repoRole("scout");
	const config = asConfig(buildRoleConfig(scout, { model: "opencode/hy4-flash-free", thinking: "high" }));
	const variants = config.providers?.opencode?.models["hy4-flash-free"]?.variants ?? [];
	assert.equal(variants.length, 1, "only the variant in use is declared — no catalogue");
	assert.equal(variants[0].id, "high");
	assert.equal(variants[0].body?.reasoningEffort, "high");
	assert.equal(config.model, "opencode/hy4-flash-free#high");

	const noThinking = asConfig(buildRoleConfig(scout, { model: "opencode/hy4-flash-free" }));
	assert.equal(noThinking.providers, undefined, "no level, no variant block");
	assert.equal(noThinking.model, "opencode/hy4-flash-free");
});

test("the production MCP server is off unless asked for", () => {
	const scout = repoRole("scout");
	const off = asConfig(buildRoleConfig(scout, { model: "opencode/hy4-flash-free" }));
	assert.equal(off.mcp, undefined, "the default child must not reach the live boards");

	const on = asConfig(
		buildRoleConfig(scout, {
			model: "opencode/hy4-flash-free",
			mcpServer: { url: "https://petbox.invalid/mcp", headers: { Authorization: "Bearer x" } },
		}),
	);
	assert.equal(on.mcp?.servers.petbox.type, "remote");
	assert.equal(on.mcp?.servers.petbox.url, "https://petbox.invalid/mcp");
	assert.equal(on.mcp?.servers.petbox.headers?.Authorization, "Bearer x");
});

test("websearch is configured only for a role that declares a web tool", () => {
	const scout = repoRole("scout");
	assert.equal(asConfig(buildRoleConfig(scout, { model: "m" })).websearch, undefined);

	const web: RoleDefinition = {
		...scout,
		name: "researcher",
		tools: [...scout.tools, "web_search"],
	};
	const config = asConfig(buildRoleConfig(web, { model: "m" }));
	assert.deepEqual(config.websearch, { provider: "exa" });
	assert.equal(effectOf(config, "researcher", "websearch"), "allow");
});

test("disallowed_tools removes exactly the capability it names", () => {
	const scout = repoRole("scout");
	const restricted: RoleDefinition = { ...scout, disallowedTools: ["read"] };
	const config = asConfig(buildRoleConfig(restricted, { model: "m" }));
	assert.equal(effectOf(config, "scout", "read"), "deny", "a veto beats the declared tool list");
	assert.equal(effectOf(config, "scout", "grep"), "allow", "the other read tools are untouched");

	const blind: RoleDefinition = { ...scout, tools: ["bash"], disallowedTools: [] };
	const blindConfig = asConfig(buildRoleConfig(blind, { model: "m", allowShell: true }));
	assert.equal(effectOf(blindConfig, "scout", "read"), "deny", "no read tool declared, no read action");
	assert.equal(effectOf(blindConfig, "scout", "glob"), "deny");
	assert.equal(effectOf(blindConfig, "scout", "shell"), "allow");
});

test("a missing role names every directory searched", () => {
	const dirs = roleDirectories("/nonexistent-project");
	assert.throws(
		() => loadRole("definitely-not-a-role", { cwd: "/nonexistent-project" }),
		(err: Error) => {
			assert.match(err.message, /not found/);
			for (const dir of dirs) assert.ok(err.message.includes(dir), `message must name ${dir}`);
			return true;
		},
	);
});

test("a role name cannot escape the agents directory", () => {
	for (const bad of ["../secrets", "a/b", "..", "x y"]) {
		assert.throws(() => loadRole(bad), /invalid role name/, `${bad} must be rejected`);
	}
});

test("mcp: true without a .mcp.json fails with the path it wanted", () => {
	const empty = mkdtempSync(path.join(tmpdir(), "oc-bridge-nomcp-"));
	assert.throws(() => loadPetboxMcpServer(empty), /\.mcp\.json, which does not exist/);

	writeFileSync(path.join(empty, ".mcp.json"), JSON.stringify({ mcpServers: {} }));
	assert.throws(() => loadPetboxMcpServer(empty), /no mcpServers\.petbox\.url/);

	writeFileSync(
		path.join(empty, ".mcp.json"),
		JSON.stringify({ mcpServers: { petbox: { type: "http", url: "https://petbox.invalid/mcp", headers: { A: "b" } } } }),
	);
	const server = loadPetboxMcpServer(empty);
	assert.equal(server.url, "https://petbox.invalid/mcp");
	assert.deepEqual(server.headers, { A: "b" });
});

test("rulesForRole is a pure function of role + options", () => {
	const scout = repoRole("scout");
	const options = { model: "m", allowShell: true };
	assert.deepEqual(rulesForRole(scout, options), rulesForRole(scout, options));
	assert.equal(existsSync(path.join(REPO_AGENTS, "scout.md")), true);
});

test("role mode carries a top-level deny-all backstop", () => {
	const scout = repoRole("scout");
	const worker = repoRole("worker");

	for (const role of [scout, worker]) {
		const config = asConfig(buildRoleConfig(role, { model: "opencode/hy4-flash-free", allowWrite: true }));
		// Without `--agent <role>` the child falls back to opencode's allow-all `build` agent.
		// The top-level object-form ruleset is the only thing standing in that gap.
		assert.ok(config.permission, "role mode must emit the top-level backstop");
		assert.deepEqual(Object.keys(config.permission!)[0], "*", "the catch-all must be first");
		assert.equal(config.permission!["*"], "deny");
		assert.equal(config.permission!.shell, "deny", "shell stays denied even with allowWrite");
		assert.equal(config.permission!.subagent, "deny");
		for (const action of ["read", "glob", "grep"]) {
			assert.equal(config.permission![action], "allow", `${action} mirrors the agent allow list`);
		}
		// The per-agent array is still emitted alongside it.
		assert.ok(Array.isArray(config.agents[role.name].permissions));
	}

	const capped = asConfig(buildRoleConfig(worker, { model: "opencode/hy4-flash-free" }));
	assert.equal(capped.permission!.edit, "deny", "no allowWrite, no edit — in both shapes");
	assert.deepEqual(permissionBackstop(rulesForRole(worker, { model: "m", allowWrite: true })).edit, "allow");
});

test("a role run always hands runOpencode the role name as agent", () => {
	const scout = repoRole("scout");
	const plan = planRoleRun(scout, { model: "opencode/hy4-flash-free", defaultModel: "opencode/hy4-flash-free" });
	assert.equal(plan.agent, "scout", "the per-agent ruleset is only reachable through --agent");

	// The seam that matters: what buildInvocation does with that plan.
	const invocation = buildInvocation({
		prompt: "p",
		agent: plan.agent,
		model: plan.model,
		variant: plan.variant,
		baseEnv: {},
	});
	assert.equal(invocation.args[invocation.args.indexOf("--agent") + 1], "scout");
	assert.equal(invocation.args[invocation.args.indexOf("-m") + 1], `opencode/hy4-flash-free#${plan.variant}`);
	assert.equal(plan.config.agents[plan.agent].system, scout.system);
});

test("a role model the CLI cannot authenticate fails with the fix, not a header error", () => {
	const scout = repoRole("scout");
	assert.match(scout.model ?? "", /^opencode-go\//, "the repo fixture pins the subscription provider");

	assert.throws(
		() => planRoleRun(scout, { defaultModel: "opencode/hy4-flash-free" }),
		(err: Error) => {
			assert.match(err.message, /cannot be authenticated by the standalone opencode CLI/);
			assert.match(err.message, /Header 'Authorization' has invalid value/);
			assert.match(err.message, /opencode\/hy4-flash-free/, "the message names the fix");
			return true;
		},
	);

	// An explicit model wins: no throw, no silent substitution.
	assert.equal(resolveRoleModel(scout, "opencode/hy4-flash-free"), "opencode/hy4-flash-free");
	assert.equal(planRoleRun(scout, { model: "opencode/hy4-flash-free" }).model, "opencode/hy4-flash-free");
	// A role with no pin falls back to the default, untouched.
	assert.equal(resolveRoleModel({ ...scout, model: undefined }, undefined, "x/y"), "x/y");
});

test("agent without a definition is rejected before the child starts", () => {
	// The trap: `agent: "scout"` instead of `role: "scout"`. A no-role config defines no agents,
	// so the child would die with `Agent not found` and the caller would explain it wrongly.
	const noRoleConfig = buildChildConfig("opencode/hy4-flash-free") as Record<string, unknown>;
	assert.throws(
		() => assertAgentParameters(undefined, "scout", noRoleConfig),
		(err: Error) => {
			assert.match(err.message, /agent "scout" is not defined in the generated child config/);
			assert.match(err.message, /Agent not found/);
			assert.match(err.message, /role: "scout"/, "the message names the fix");
			assert.match(err.message, /~\/\.pi\/agent\/agents\/scout\.md/, "and where the role file comes from");
			return true;
		},
	);

	// Even when ~/.pi/agent/agents/scout.md exists: no silent upgrade to a role.
	assert.equal(existsSync(path.join(roleDirectories()[0], "scout.md")), installedRole("scout") !== undefined);
	assert.throws(() => assertAgentParameters(undefined, "scout", noRoleConfig), /is not defined/);

	// No agent parameter at all is always fine.
	assert.doesNotThrow(() => assertAgentParameters(undefined, undefined, noRoleConfig));
});

test("the role path defines and selects the role agent, and rejects a disagreeing agent", () => {
	const scout = repoRole("scout");
	const plan = planRoleRun(scout, { model: "opencode/hy4-flash-free" });
	assert.ok(plan.config.agents && (plan.config.agents as Record<string, unknown>)[scout.name]);
	assert.doesNotThrow(() => assertAgentParameters(plan.agent, scout.name, plan.config), "agent === role is fine");
	assert.doesNotThrow(() => assertAgentParameters(plan.agent, undefined, plan.config));

	assert.throws(
		() => assertAgentParameters(plan.agent, "worker", plan.config),
		/role "scout" and agent "worker" disagree/,
	);
});
