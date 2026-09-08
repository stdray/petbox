// Per-harness capability matrix for the agent-artifact compiler.
//
// Spec (definition-truthfulness, per-harness-artifact): a role may only require
// capabilities the target harness declares. This matrix is kit data (versioned with
// the npm package) — capabilities change with harness versions, not with the
// portable agent definition.
//
// EVERY cell is a factual claim about a harness, taken from that harness's docs
// (or a verified live observation). Do not invent; do not copy another harness's row.
//
// Plain TS for native node type-stripping: zero deps.

export type HarnessId = "claude-code" | "opencode" | "droid" | "codex" | "qwen";

/** Capability ids a role may list in requiredCapabilities. */
export type Capability =
  | "mcp_main_session"
  | "mcp_subagent"
  | "dynamic_model_at_spawn"
  | "role_files"
  | "builtin_explore_inherits_model"
  | "hooks"
  | "spawn_subagents";

export const HARNESS_IDS: readonly HarnessId[] = ["claude-code", "opencode", "droid", "codex", "qwen"] as const;

export const CAPABILITIES: readonly Capability[] = [
  "mcp_main_session",
  "mcp_subagent",
  "dynamic_model_at_spawn",
  "role_files",
  "builtin_explore_inherits_model",
  "hooks",
  "spawn_subagents",
] as const;

/**
 * Capability matrix — sources cited per cell.
 *
 * claude-code (verified live 2026-07-10 + 2026-07-12 + Anthropic Claude Code agent docs):
 *   mcp_main_session — main session has MCP.
 *   mcp_subagent — declared (verified live 2026-07-12: a subagent with an unrestricted
 *     tool set called an MCP tool, mcp__petbox__whoami, successfully). The earlier
 *     "subagents lack MCP" note (2026-07-10) was an artifact of a hand-written
 *     ~/.claude/agents/worker.md `tools:` whitelist that hid MCP — not a harness limit.
 *   dynamic_model_at_spawn — model passed dynamically at Agent/Task spawn.
 *   role_files — project/user agents under .claude/agents/*.md.
 *   builtin_explore_inherits_model — built-in Explore inherits parent model by default.
 *   hooks — SessionStart/Stop/UserPromptSubmit hook surface.
 *   spawn_subagents — main session spawns via Agent/Task tool; subagents do not re-spawn.
 *
 * opencode:
 *   mcp_main_session / mcp_subagent — plugin injects system text with no main/subagent
 *     branching; MCP tools available the same way in both (kit observation).
 *   dynamic_model_at_spawn — NOT declared (opencode PR #18588: model not dynamic at spawn;
 *     roles are files only).
 *   role_files — .opencode/agent/*.md.
 *   builtin_explore_inherits_model — built-in explore inherits parent model.
 *   hooks — not a first-class CC-hook surface; leave undeclared.
 *   spawn_subagents — can spawn subagents (agent types / Task-equivalent).
 *
 * droid (Factory official docs — do not reintroduce enableHooks-MCP assumptions):
 *   role_files — custom droids in .factory/droids/*.md (project) and ~/.factory/droids/
 *     (user); project overrides user on name clash.
 *     https://docs.factory.ai/cli/configuration/custom-droids
 *   spawn_subagents — main session spawns via Task tool (subagent_type = built-in or
 *     custom droid name); subagents cannot spawn further (Task unavailable to them).
 *     https://docs.factory.ai/cli/configuration/custom-droids
 *   dynamic_model_at_spawn — droid frontmatter model: inherit | explicit id; inherit
 *     follows parent / complexity routing.
 *     https://docs.factory.ai/cli/configuration/custom-droids § Controlling the model
 *   mcp_main_session — MCP via .factory/mcp.json (user/folder/project).
 *     https://docs.factory.ai/cli/configuration/mcp
 *   mcp_subagent — custom droid mcpServers frontmatter scopes which configured MCP
 *     servers the subagent receives.
 *     https://docs.factory.ai/cli/configuration/custom-droids § Selecting MCP servers
 *   hooks — Factory settings hooks (Stop/SessionStart/UserPromptSubmit) used by the kit.
 *   builtin_explore_inherits_model — NOT declared: Factory ships built-in `explorer` with
 *     model inherit, but we do not equate it to CC/opencode "Explore" without a separate
 *     verification pass (leave honestly unspecified).
 *   Hierarchy note: org/project/folder/user settings; write project-level .factory/droids/
 *     only (additive). https://docs.factory.ai/enterprise/hierarchical-settings-and-org-control
 *
 * codex (Codex CLI 0.153.4, source-anchored — see codex-spec.md, task wire-support-codex-qwen):
 *   hooks — feature `hooks` is Stage::Stable, default_enabled:true; 12 events incl.
 *     SessionStart/SessionEnd/PreToolUse (features/src/lib.rs:1166; config/src/hook_config.rs:36-61).
 *   role_files — config.toml `[agents.<name>]` + a role body file (`developer_instructions`
 *     required); auto-discovered recursively from every `.toml` file under `<config
 *     folder>/agents/` (agent_role_config.rs:22-71; loader.rs:76-78, discovery.rs:11-39 in
 *     codex-rs/agent-roles).
 *   spawn_subagents — `spawn_agent` tool, `SpawnAgentArgs` (multi_agents_v2/spawn.rs:280-288).
 *   mcp_main_session — `[mcp_servers.<name>]`, verified round-trip via `codex mcp list --json`
 *     (config_toml.rs:276; mcp_types.rs:323-382).
 *   dynamic_model_at_spawn — `SpawnAgentArgs.model: Option<String>`
 *     (multi_agents_v2/spawn.rs:284; hook_runtime.rs:203 confirms it reaches PreToolUse's
 *     `tool_input.model`, not the payload's top-level `model`).
 *   mcp_subagent — a spawned subagent's Config is a full clone of the parent turn's Config
 *     (`build_agent_shared_config`, multi_agents_common.rs:195-197: `let mut config =
 *     (*base_config).clone();`), which carries `mcp_servers` — no separate per-subagent MCP
 *     scoping mechanism was found. Inferred from this clone, not a live probe.
 *   NOT builtin_explore_inherits_model — no built-in "explore"-equivalent role was found in the
 *     source (only user-declared example role names like "researcher"/"reviewer" in tests);
 *     omitted rather than guessed, per this module's own rule.
 *
 * qwen (Qwen Code 0.23.0, source-anchored — see qwen-spec.md, task wire-support-codex-qwen):
 *   hooks — 17 events declared in the settings schema incl. SessionStart/Stop/StopFailure/
 *     PreToolUse (settingsSchema.ts:3608-3817); the kit hooks Stop+StopFailure for the
 *     transcript push (SessionEnd is dead in headless — never dispatched from
 *     cli/src/nonInteractiveCli.ts, only from UI/ACP call sites — qwen-spec.md section 3).
 *   role_files — `.qwen/agents/<name>.md`, the SAME Claude-Code subagent frontmatter schema
 *     (subagent-manager.ts:1411-1412/1449-1450; agent-frontmatter-schema.ts:9-12 "mirrors
 *     Claude Code's .claude/agents/<name>.md schema verbatim"). Reuses renderAgentMarkdown —
 *     no Qwen-specific renderer exists.
 *   spawn_subagents — the built-in Agent tool (`ToolNames.AGENT = 'agent'`,
 *     packages/core/src/tools/agent/agent.ts:679-680), `subagent_type` + optional `model`
 *     params — byte-identical field names to Claude Code's Task/Agent tool (verified live in
 *     the installed clone, packages/core/src/tools/agent/agent.ts:225/718).
 *   mcp_main_session — `mcpServers` in settings.json at any scope (mcp-server-config.ts:104-160);
 *     the kit writes it to USER scope only (scope unset -> never gated — qwen-spec.md section 2).
 *   dynamic_model_at_spawn — the Agent tool's `model` param, gated against `agents.modelGrades`
 *     ONLY when passed (agent.ts:1053-1073: `if (params.model !== undefined) { ... }` — omitted
 *     entirely, the role file's own `model:` frontmatter applies instead, same as Claude Code).
 *   mcp_subagent — a spawned Agent shares the parent Config's `mcpServers` (same schema/scope as
 *     claude-code and codex; no separate per-subagent MCP scoping mechanism found beyond an
 *     optional per-role `mcpServers` override the kit does not currently set — inherits by
 *     default).
 *   NOT builtin_explore_inherits_model — no built-in "explore"-equivalent subagent type was
 *     found in the source; omitted rather than guessed, per this module's own rule.
 */
const MATRIX: Readonly<Record<HarnessId, readonly Capability[]>> = {
  "claude-code": [
    "mcp_main_session", // live: main has MCP
    "mcp_subagent", // live 2026-07-12: unrestricted subagent called mcp__petbox__whoami
    "dynamic_model_at_spawn", // model at Agent/Task spawn
    "role_files", // .claude/agents/*.md
    "builtin_explore_inherits_model", // built-in Explore inherits parent
    "hooks", // SessionStart/Stop/UserPromptSubmit
    "spawn_subagents", // main spawns; subagents do not re-spawn
  ],
  opencode: [
    "mcp_main_session", // same MCP surface main/sub (no branching inject)
    "mcp_subagent", // subagents see MCP like main
    "role_files", // .opencode/agent/*.md
    "builtin_explore_inherits_model", // built-in explore inherits
    "spawn_subagents", // can spawn agent types
    // no dynamic_model_at_spawn — PR #18588
    // no hooks — not CC-hook surface
  ],
  droid: [
    "mcp_main_session", // docs: .factory/mcp.json — https://docs.factory.ai/cli/configuration/mcp
    "mcp_subagent", // docs: mcpServers on custom droid — custom-droids § Selecting MCP servers
    "spawn_subagents", // docs: Task tool from main — custom-droids
    "role_files", // docs: .factory/droids/*.md — custom-droids
    "dynamic_model_at_spawn", // docs: model inherit | explicit — custom-droids § model
    "hooks", // kit uses Factory settings hooks
    // no builtin_explore_inherits_model — explorer exists but not equated to CC Explore here
  ],
  codex: [
    "hooks", // Stage::Stable, default_enabled:true — features/src/lib.rs:1166
    "role_files", // [agents.<name>] + agent_role_config.rs; auto-discovered agents/**/*.toml
    "spawn_subagents", // spawn_agent tool — multi_agents_v2/spawn.rs
    "mcp_main_session", // [mcp_servers.<name>] — config_toml.rs:276
    "dynamic_model_at_spawn", // SpawnAgentArgs.model: Option<String> — spawn.rs:284
    "mcp_subagent", // subagent Config clones the parent's (incl. mcp_servers) — multi_agents_common.rs:196-197
    // no builtin_explore_inherits_model — no built-in "explore" role found in source; omitted
  ],
  qwen: [
    "hooks", // 17-event schema incl. Stop/StopFailure/PreToolUse — settingsSchema.ts:3608-3817
    "role_files", // .qwen/agents/*.md — Claude-Code subagent schema, reuses renderAgentMarkdown
    "spawn_subagents", // built-in Agent tool, subagent_type param — agent.ts:679-680/225
    "mcp_main_session", // mcpServers in settings.json (user scope, never gated) — mcp-server-config.ts:104-160
    "dynamic_model_at_spawn", // Agent tool's model param, gated by agents.modelGrades — agent.ts:1053-1073
    "mcp_subagent", // spawned Agent shares parent Config's mcpServers — same shape as claude-code/codex
    // no builtin_explore_inherits_model — no built-in "explore" subagent type found in source
  ],
};

/** Known capability set for a harness id. Unknown id → empty set (never invent). */
export function harnessCapabilities(harness: string): ReadonlySet<string> {
  const list = (MATRIX as Record<string, readonly Capability[] | undefined>)[harness];
  return new Set(list ?? []);
}

export function isKnownHarness(id: string): id is HarnessId {
  return (HARNESS_IDS as readonly string[]).includes(id);
}

export function hasCapability(harness: string, capability: string): boolean {
  return harnessCapabilities(harness).has(capability);
}
