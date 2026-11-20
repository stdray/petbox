/**
 * Derive an opencode config from a pi role file.
 *
 * The point of this module: a delegated child should keep the ROLE's identity and exactly
 * the capabilities the role declares, instead of the fixed read-only policy of a roleless
 * call. Everything it emits is measured against opencode 2.3.2's published schema
 * (https://opencode.ai/config.json) and verified live:
 *
 * - `agents.<name>.system` REPLACES the provider base prompt. Measured: a role body whose
 *   first line demands `SCOUT-INTRO-OK` produces exactly that first line, with none of
 *   opencode's own "I am a coding agent" preamble. Instruction layers still stack — which
 *   is why `instructions` is emitted empty below.
 * - `agents.<name>.permissions` is an ARRAY of `{action, resource, effect}` rules. The
 *   object form (`{"*": "deny"}`) is the deprecated top-level `permission` shape and is
 *   IGNORED when placed on an agent — measured: an agent carrying the object form still got
 *   `write` and `shell` and wrote a file. The catch-all rule must come FIRST; later rules
 *   override it (that ordering is what makes the default safe).
 * - `providers.<provider>.models.<id>.variants[].id` is what `-m provider/model#id` selects.
 *
 * Nothing here reads or writes a secret: the PetBox MCP server (url + headers) is taken
 * from the caller, written into a temp file that is deleted after the run, and never logged.
 */

import { existsSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import * as path from "node:path";

/** A parsed role file: `~/.pi/agent/agents/<name>.md`. */
export interface RoleDefinition {
	name: string;
	description?: string;
	model?: string;
	thinking?: string;
	tools: string[];
	disallowedTools: string[];
	skills: string[];
	/** The markdown body — the role's system prompt. */
	system: string;
	sourcePath: string;
}

/** Where role files are looked up, in order. */
export function roleDirectories(cwd: string = process.cwd()): string[] {
	const agentHome = process.env.PI_AGENT_DIR || path.join(homedir(), ".pi", "agent");
	return [path.join(agentHome, "agents"), path.join(cwd, ".pi", "agents")];
}

/** A role name becomes a config key and an argv value, so it has to be boring. */
const ROLE_NAME_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._-]*$/;

/** Keys whose value is a list even when written without brackets. */
const LIST_KEYS = new Set(["tools", "skills", "disallowed_tools", "allowed_subagents"]);

function parseScalar(raw: string): string {
	const value = raw.trim();
	if (value.length >= 2) {
		const first = value[0];
		const last = value[value.length - 1];
		if ((first === '"' && last === '"') || (first === "'" && last === "'")) return value.slice(1, -1);
	}
	return value;
}

function parseList(raw: string): string[] {
	const value = raw.trim();
	const inner = value.startsWith("[") && value.endsWith("]") ? value.slice(1, -1) : value;
	return inner
		.split(",")
		.map((entry) => parseScalar(entry))
		.filter((entry) => entry.length > 0);
}

/**
 * Parse a role file. Frontmatter is a flat `key: value` map — the shape every role in
 * `tools/pi/agents/` uses; unknown keys are ignored rather than fatal so a new pi key does
 * not break delegation.
 */
export function parseRoleFile(text: string, sourcePath: string): RoleDefinition {
	const lines = text.split(/\r?\n/);
	if (lines[0]?.trim() !== "---") {
		throw new Error(`${sourcePath}: not a role file (no YAML frontmatter)`);
	}
	const end = lines.findIndex((line, index) => index > 0 && line.trim() === "---");
	if (end < 0) throw new Error(`${sourcePath}: frontmatter is not closed with '---'`);

	const fields = new Map<string, string>();
	for (const line of lines.slice(1, end)) {
		const match = /^([A-Za-z_][A-Za-z0-9_-]*):\s*(.*)$/.exec(line);
		if (match) fields.set(match[1], match[2]);
	}

	const name = parseScalar(fields.get("name") ?? "");
	if (!name) throw new Error(`${sourcePath}: frontmatter has no 'name'`);

	const body = lines
		.slice(end + 1)
		.join("\n")
		.replace(/^\s*\n+/, "")
		.trim();

	const role: RoleDefinition = {
		name,
		tools: parseList(fields.get("tools") ?? ""),
		disallowedTools: parseList(fields.get("disallowed_tools") ?? ""),
		skills: parseList(fields.get("skills") ?? ""),
		system: body,
		sourcePath,
	};
	const description = parseScalar(fields.get("description") ?? "");
	if (description) role.description = description;
	const model = parseScalar(fields.get("model") ?? "");
	if (model) role.model = model;
	const thinking = parseScalar(fields.get("thinking") ?? "");
	if (thinking) role.thinking = thinking;
	return role;
}

/** Read `<agents dir>/<name>.md`; a miss names every directory that was searched. */
export function loadRole(name: string, options: { cwd?: string } = {}): RoleDefinition {
	if (!ROLE_NAME_PATTERN.test(name)) {
		throw new Error(`opencode_delegate: invalid role name ${JSON.stringify(name)}`);
	}
	const dirs = roleDirectories(options.cwd);
	const tried: string[] = [];
	for (const dir of dirs) {
		const candidate = path.join(dir, `${name}.md`);
		tried.push(candidate);
		if (!existsSync(candidate)) continue;
		return parseRoleFile(readFileSync(candidate, "utf8"), candidate);
	}
	throw new Error(
		`opencode_delegate: role ${JSON.stringify(name)} not found. Searched:\n` +
			tried.map((candidate) => `  - ${candidate}`).join("\n"),
	);
}

/** pi thinking levels → opencode variant ids / `reasoningEffort` values. */
export const THINKING_TO_EFFORT: Readonly<Record<string, string>> = {
	off: "low",
	minimal: "low",
	low: "low",
	medium: "medium",
	high: "high",
	xhigh: "xhigh",
	max: "max",
};

/** The single variant a run will actually use, or undefined when no level was chosen. */
export function variantFor(thinking?: string): { id: string; effort: string } | undefined {
	if (!thinking) return undefined;
	const key = thinking.trim().toLowerCase();
	if (!key) return undefined;
	const effort = THINKING_TO_EFFORT[key];
	if (!effort) {
		throw new Error(
			`opencode_delegate: unknown thinking level ${JSON.stringify(thinking)}; ` +
				`expected one of ${Object.keys(THINKING_TO_EFFORT).join(", ")}`,
		);
	}
	return { id: effort, effort };
}

/** Permission rule as opencode's `Permission.Rule` expects it. */
export interface PermissionRule {
	action: string;
	resource: string;
	effect: "allow" | "deny";
}

/** pi tool names that write files. */
const WRITE_TOOLS = new Set(["edit", "write", "notebook_edit"]);
/** pi tool names that run shell commands. */
const SHELL_TOOLS = new Set(["bash", "powershell", "pwsh", "shell"]);
/** pi tool names that reach the web. */
const WEB_TOOLS = new Set(["websearch", "web_search", "webfetch", "web_fetch", "fetch", "fetch_content", "web_enable"]);

export interface RoleConfigOptions {
	/** Resolved model (`provider/model`); a caller's override already won over the role's. */
	model: string;
	/** Resolved pi thinking level; the role's value unless the caller overrode it. */
	thinking?: string;
	allowWrite?: boolean;
	allowShell?: boolean;
	/** Lift the subagent deny. Off by default: a delegated child must not spawn its own. */
	allowSubagent?: boolean;
	/** The production PetBox MCP server — included ONLY when the caller opted in. */
	mcpServer?: { url: string; headers?: Record<string, string> };
}

/**
 * The permission ruleset for a role: catch-all deny first, then exactly the role's declared
 * capabilities. A capability the role declares but the caller forbids (write/shell) stays
 * denied — the role is a ceiling, the flags are the caller's consent.
 */
export function rulesForRole(role: RoleDefinition, options: RoleConfigOptions): PermissionRule[] {
	const denied = new Set(role.disallowedTools.map((tool) => tool.trim().toLowerCase()));
	const tools = new Set(role.tools.map((tool) => tool.trim().toLowerCase()));
	const allowed = (tool: string) => tools.has(tool) && !denied.has(tool);

	const rules: PermissionRule[] = [{ action: "*", resource: "*", effect: "deny" }];
	const allow = (action: string) => rules.push({ action, resource: "*", effect: "allow" });
	const deny = (action: string) => rules.push({ action, resource: "*", effect: "deny" });

	// Each opencode action is derived from the pi tools that need it, so a `disallowed_tools`
	// veto removes exactly the capability it names instead of the whole read surface.
	const readSurface: Array<[string, boolean]> = [
		["read", allowed("read")],
		["glob", ["find", "ls", "glob"].some(allowed)],
		["grep", allowed("grep")],
	];
	for (const [action, can] of readSurface) {
		if (can) allow(action);
		else deny(action);
	}

	// `edit` covers writing too: measured, an agent denied `edit` reports no write tool.
	if ([...WRITE_TOOLS].some(allowed) && options.allowWrite) allow("edit");
	else deny("edit");

	if ([...SHELL_TOOLS].some(allowed) && options.allowShell) allow("shell");
	else deny("shell");

	if (options.allowSubagent) allow("subagent");
	else deny("subagent");

	if (isWebRole(role)) {
		allow("websearch");
		allow("webfetch");
	} else {
		deny("websearch");
		deny("webfetch");
	}
	return rules;
}

/** True when the role declares any tool that reaches the web. */
export function isWebRole(role: RoleDefinition): boolean {
	const denied = new Set(role.disallowedTools.map((tool) => tool.trim().toLowerCase()));
	return role.tools.some((tool) => WEB_TOOLS.has(tool.trim().toLowerCase()) && !denied.has(tool.trim().toLowerCase()));
}

/**
 * Providers the standalone CLI cannot authenticate.
 *
 * Measured: `opencode-go/hy4-flash-free` is a pi-side subscription pin; the CLI's own credential
 * store does not hold it, so a child started with that model dies during the handshake with
 * `Header 'Authorization' has invalid value` — 0 tokens, exit 1, no useful explanation. A role
 * file pinning such a provider is therefore a trap for a caller who passed no `model`.
 */
export const UNAUTHENTICATED_PROVIDERS: ReadonlySet<string> = new Set(["opencode-go"]);

/** The model used when neither the caller nor the role names one. */
const FALLBACK_MODEL = "opencode/deepseek-v4.1-flash-free";

/**
 * Which model a role run uses, and a clear failure when the role's own pin is unusable.
 *
 * A caller's `model` always wins — it is the escape hatch and it is honest: nothing here tries
 * to authenticate or to substitute a provider silently.
 */
export function resolveRoleModel(
	role: RoleDefinition,
	explicitModel?: string,
	defaultModel: string = FALLBACK_MODEL,
): string {
	const explicit = explicitModel?.trim();
	if (explicit) return explicit;
	const fromRole = role.model?.trim();
	if (!fromRole) return defaultModel;
	const provider = fromRole.split("/")[0];
	if (UNAUTHENTICATED_PROVIDERS.has(provider)) {
		throw new Error(
			`opencode_delegate: role ${JSON.stringify(role.name)} pins model ${JSON.stringify(fromRole)}, but that ` +
				`provider (${provider}) cannot be authenticated by the standalone opencode CLI — its credential ` +
				"store does not hold that key, so the run dies with `Header 'Authorization' has invalid value` " +
				"and 0 tokens. Pass an explicit model the CLI can use, e.g. " +
				'model: "opencode/hy4-flash-free".',
		);
	}
	return fromRole;
}

/** What a role run resolves to: the child agent name, model, variant and config. */
export interface RoleRunPlan {
	/** ALWAYS the role name — `index.ts` passes this as `runOpencode`'s `agent` (`--agent <name>`). */
	agent: string;
	model: string;
	thinking?: string;
	variant?: string;
	config: Record<string, unknown>;
}

export interface RoleRunOptions {
	model?: string;
	thinking?: string;
	allowWrite?: boolean;
	allowShell?: boolean;
	allowSubagent?: boolean;
	mcpServer?: { url: string; headers?: Record<string, string> };
	defaultModel?: string;
}

/**
 * Resolve a role run in one place, so the config, the model and `--agent` cannot disagree.
 *
 * The per-agent permissions are only reachable through `--agent <role>`; without that flag the
 * child would fall back to opencode's default `build` agent, which is allow-all. The top-level
 * backstop in `buildRoleConfig` is what makes that fallback refuse writes and shell instead of
 * silently granting everything.
 */
export function planRoleRun(role: RoleDefinition, options: RoleRunOptions = {}): RoleRunPlan {
	const model = resolveRoleModel(role, options.model, options.defaultModel);
	const thinking = options.thinking ?? role.thinking;
	const variant = variantFor(thinking)?.id;
	const config = buildRoleConfig(role, {
		model,
		thinking,
		allowWrite: options.allowWrite,
		allowShell: options.allowShell,
		allowSubagent: options.allowSubagent,
		mcpServer: options.mcpServer,
	});
	const plan: RoleRunPlan = { agent: role.name, model, config };
	if (thinking !== undefined) plan.thinking = thinking;
	if (variant !== undefined) plan.variant = variant;
	return plan;
}

/**
 * The object-form mirror of a ruleset, for the TOP-LEVEL `permission` key.
 *
 * Same rules, different shape: opencode's top-level `permission` is the deprecated-but-working
 * object form (measured: global `{"*":"deny", read/glob/grep:"allow"}` with an agent allow list
 * still read `ALPHA-42` and still refused a write), while `agents.<name>.permissions` is the
 * ruleset array. Key order is preserved, so the catch-all deny stays first.
 */
export function permissionBackstop(rules: PermissionRule[]): Record<string, "allow" | "deny"> {
	const backstop: Record<string, "allow" | "deny"> = {};
	for (const rule of rules) backstop[rule.action] = rule.effect;
	return backstop;
}

/**
 * Guard the `role` / `agent` parameter pair before anything is spawned.
 *
 * The trap this exists for: `agent: "scout"` instead of `role: "scout"`. The no-role config
 * defines no `agents` block, so `--agent scout` reaches a CLI that has never heard of it and
 * the run dies with `Agent not found: "scout"` — 0 tokens, exit 1 — and the calling model
 * then INVENTED an explanation ("opencode's registry has no scout role") because nothing in
 * the message pointed at the parameter it got wrong.
 *
 * Two rules, both explicit:
 * - with `role`, `agent` must be absent or equal to the role name (the role IS the agent);
 * - without `role`, `agent` must name an agent the generated config actually defines — which
 *   it never does, so in practice: use `role`.
 * An installed role file is deliberately NOT auto-upgraded to a role: guessing what the caller
 * meant is how a read-only role call becomes a silent write-capable one.
 */
export function assertAgentParameters(
	roleName: string | undefined,
	agent: string | undefined,
	config: Record<string, unknown>,
): void {
	if (roleName !== undefined && agent !== undefined && agent !== roleName) {
		throw new Error(
			`opencode_delegate: role ${JSON.stringify(roleName)} and agent ${JSON.stringify(agent)} disagree. ` +
				"A role run selects --agent <role> and derives that agent's system prompt and permissions from " +
				"the role file, so the two must match. Pass only role (the usual case) or make agent equal it.",
		);
	}
	if (agent === undefined) return;

	const agents = config.agents;
	const defined = agents && typeof agents === "object" && !Array.isArray(agents) && Object.prototype.hasOwnProperty.call(agents, agent);
	if (defined) return;

	throw new Error(
		`opencode_delegate: agent ${JSON.stringify(agent)} is not defined in the generated child config, so the ` +
			`run would fail with \`Agent not found: "${agent}"\` (0 tokens, exit 1). The \`agent\` parameter ` +
			"selects an agent the config defines; to delegate as a pi role, use " +
			`role: "${agent}" — it is derived from ~/.pi/agent/agents/${agent}.md — instead of agent.`,
	);
}

/**
 * The role config for one delegated role.
 *
 * `instructions` is deliberately empty: the role body replaces the prompt (measured), and
 * ambient instruction files would re-introduce exactly the project context the role was
 * written to narrow down.
 */
export function buildRoleConfig(role: RoleDefinition, options: RoleConfigOptions): Record<string, unknown> {
	const variant = variantFor(options.thinking);
	const [providerId, ...rest] = options.model.split("/");
	const modelId = rest.join("/");
	const permissions = rulesForRole(role, options);
	const config: Record<string, unknown> = {
		$schema: "https://opencode.ai/config.json",
		model: variant ? `${options.model}#${variant.id}` : options.model,
		// Backstop for the one thing the per-agent ruleset cannot cover: if `--agent <role>` is
		// ever missing from the argv, the child falls back to opencode's default `build` agent,
		// which is allow-all. This top-level deny-all is what keeps that fallback read-only.
		permission: permissionBackstop(permissions),
		instructions: [],
		agents: {
			[role.name]: {
				description: role.description ?? `pi role ${role.name}`,
				system: role.system,
				permissions,
			},
		},
	};

	if (variant && providerId && modelId) {
		// Only the level actually used is declared — a catalogue of unused levels is config
		// nobody reads, and opencode resolves `#id` against this list.
		config.providers = {
			[providerId]: {
				models: {
					[modelId]: {
						variants: [{ id: variant.id, body: { reasoningEffort: variant.effort } }],
					},
				},
			},
		};
	}

	if (isWebRole(role)) config.websearch = { provider: "exa" };

	if (options.mcpServer) {
		const server: Record<string, unknown> = { type: "remote", url: options.mcpServer.url };
		if (options.mcpServer.headers && Object.keys(options.mcpServer.headers).length > 0) {
			server.headers = options.mcpServer.headers;
		}
		config.mcp = { servers: { petbox: server } };
	}

	return config;
}

/**
 * The production PetBox MCP server, read from the project's `.mcp.json`.
 *
 * Only a caller that asks for it (`mcp: true`) gets this; the default stays disconnected so
 * a delegated child cannot touch the live task boards. The headers usually carry a token and
 * are never logged.
 */
export function loadPetboxMcpServer(cwd: string): { url: string; headers?: Record<string, string> } {
	const configPath = path.join(cwd, ".mcp.json");
	if (!existsSync(configPath)) {
		throw new Error(`opencode_delegate: mcp: true needs ${configPath}, which does not exist`);
	}
	let parsed: unknown;
	try {
		parsed = JSON.parse(readFileSync(configPath, "utf8"));
	} catch (err) {
		throw new Error(`opencode_delegate: ${configPath} is not valid JSON: ${(err as Error).message}`);
	}
	const servers = (parsed as { mcpServers?: Record<string, unknown> } | null)?.mcpServers;
	const petbox = servers?.petbox as { url?: unknown; headers?: unknown } | undefined;
	if (typeof petbox?.url !== "string") {
		throw new Error(`opencode_delegate: ${configPath} has no mcpServers.petbox.url`);
	}
	const headers: Record<string, string> = {};
	if (petbox.headers && typeof petbox.headers === "object") {
		for (const [key, value] of Object.entries(petbox.headers as Record<string, unknown>)) {
			if (typeof value === "string") headers[key] = value;
		}
	}
	return Object.keys(headers).length > 0 ? { url: petbox.url, headers } : { url: petbox.url };
}
