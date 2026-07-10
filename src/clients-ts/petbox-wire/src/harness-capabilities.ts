// Per-harness capability matrix for the agent-artifact compiler.
//
// Spec (definition-truthfulness, per-harness-artifact): a role may only require
// capabilities the target harness declares. This matrix is kit data (versioned with
// the npm package) — capabilities change with harness versions, not with the
// portable agent definition.
//
// Facts verified 2026-07-10 (work: harness-artifact-compiler + per-harness-truthfulness):
//   claude-code — MCP only on the main session (spawned workers lack mcp__petbox__*);
//                 built-in Explore inherits parent model; model can be set at spawn;
//                 role files under .claude/agents/; can spawn subagents with explicit model.
//   opencode    — model is NOT dynamic at spawn (PR #18588); roles live as
//                 .opencode/agent/*.md files; built-in explore inherits model;
//                 plugin injects system text with no main/subagent branching (so
//                 MCP surface is available to subagents the same way as main);
//                 can spawn subagents.
//   droid       — hooks only unconditionally. MCP is gated by enableHooks and is
//                 NOT declared as mcp_main_session here (do not claim MCP always
//                 present). No verified spawn_subagents / explore-inherit / dynamic-
//                 spawn claims yet.
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

const MATRIX: Readonly<Record<HarnessId, readonly Capability[]>> = {
  "claude-code": [
    "mcp_main_session",
    "dynamic_model_at_spawn",
    "role_files",
    "builtin_explore_inherits_model",
    "hooks",
    "spawn_subagents",
  ],
  opencode: [
    "mcp_main_session",
    "mcp_subagent",
    "role_files",
    "builtin_explore_inherits_model",
    "spawn_subagents",
  ],
  // MCP is enableHooks-gated on droid — do not declare mcp_main_session unconditionally.
  droid: ["hooks"],
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
