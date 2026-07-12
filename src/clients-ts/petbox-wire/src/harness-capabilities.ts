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

export type HarnessId = "claude-code" | "opencode" | "droid";

/** Capability ids a role may list in requiredCapabilities. */
export type Capability =
  | "mcp_main_session"
  | "mcp_subagent"
  | "dynamic_model_at_spawn"
  | "role_files"
  | "builtin_explore_inherits_model"
  | "hooks"
  | "spawn_subagents";

export const HARNESS_IDS: readonly HarnessId[] = ["claude-code", "opencode", "droid"] as const;

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
