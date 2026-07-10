// Portable agent definition (roster only — no model binding).
//
// Spec (agent-definition-as-data, agent-definition-locality):
//   - Roles carry slug, tier, requiredCapabilities, spawn, escalation.
//   - model is NEVER part of this document (local binding lives in roles.json).
//   - Built-in DEFAULT_AGENT_DEFINITION ships with the kit for offline compile;
//     apply tries server fetch first (agent-def-fetch.ts) and falls back here.
//
// Plain TS for native node type-stripping: zero deps.

import type { Capability } from "./harness-capabilities.ts";

export type RoleSpawn = {
  readonly allowed: boolean;
  readonly allowedRoles?: ReadonlyArray<string>;
};

export type RoleEscalation = {
  readonly available: boolean;
  readonly targets?: ReadonlyArray<string>;
};

export type AgentRole = {
  readonly slug: string;
  readonly tier: string;
  /** Harness capabilities this role needs; empty = no harness-specific needs. */
  readonly requiredCapabilities: ReadonlyArray<Capability | string>;
  readonly spawn?: RoleSpawn;
  readonly escalation?: RoleEscalation;
  /**
   * Optional free-text notes rendered into per-role artifacts.
   * Used for harness-aware caveats (e.g. explore model inheritance) without
   * putting lies into the shared protocol block.
   */
  readonly notes?: string;
};

export type AgentDefinition = {
  readonly name: string;
  readonly roles: ReadonlyArray<AgentRole>;
};

/**
 * Built-in portable roster for offline compile (petbox-wire doctor / apply).
 * Includes `explore` so the roster matches harnesses that ship a built-in explore
 * agent — with an explicit inheritance note (not a global "inheritance forbidden").
 *
 * Caps are honest for the roles: orchestrator needs MCP + spawn_subagents.
 * DEFAULT therefore fails truthfulness on harnesses that lack those (e.g. droid
 * has neither mcp_main_session nor spawn_subagents) — intentional honesty, not a bug.
 */
export const DEFAULT_AGENT_DEFINITION: AgentDefinition = {
  name: "default",
  roles: [
    {
      slug: "orchestrator",
      tier: "orchestrator",
      requiredCapabilities: ["mcp_main_session", "spawn_subagents"],
      spawn: {
        allowed: true,
        allowedRoles: ["worker", "utility", "explore", "reserve"],
      },
      escalation: { available: true, targets: ["reserve"] },
      notes:
        "Main-loop role: plan, decompose, delegate, review, triage. Prefer spawning workers over solo implementation.",
    },
    {
      slug: "worker",
      tier: "worker",
      // No mcp_subagent: Claude Code workers do not receive petbox MCP (verified).
      requiredCapabilities: [],
      spawn: { allowed: false },
      escalation: { available: true, targets: ["orchestrator"] },
      notes:
        "Scoped implementation / research leaf. Brief carries any PetBox data the orchestrator already fetched — do not assume MCP in the subagent session.",
    },
    {
      slug: "utility",
      tier: "utility",
      requiredCapabilities: [],
      spawn: { allowed: false },
      escalation: { available: true, targets: ["worker", "orchestrator"] },
      notes: "Fast simple work: search, summarize, mechanical edits.",
    },
    {
      slug: "reserve",
      tier: "reserve",
      requiredCapabilities: ["mcp_main_session"],
      spawn: { allowed: false },
      escalation: { available: false },
      notes:
        "Heavy reasoning / architecture review / stuck points only. Explicit spawn + justification; never edits files.",
    },
    {
      slug: "explore",
      tier: "utility",
      // Declared so harnesses that ship built-in explore are roster-honest.
      // Model inheritance is harness-default where builtin_explore_inherits_model —
      // do NOT claim "inheritance forbidden" for this role.
      requiredCapabilities: [],
      spawn: { allowed: false },
      escalation: { available: false },
      notes:
        "Built-in research/explore agent on harnesses that ship it (Claude Code Explore, opencode explore). " +
        "Where the harness declares builtin_explore_inherits_model, model inheritance is the harness default — " +
        "not a protocol violation. Prefer an explicit bound model when the harness supports role_files or dynamic_model_at_spawn.",
    },
  ],
};

/** Light structural check; throws on invalid shape (loud, never silent). */
export function validateAgentDefinition(def: AgentDefinition): void {
  if (!def || typeof def !== "object") throw new Error("agent definition is required");
  if (!def.name || !String(def.name).trim()) throw new Error("definition.name is required");
  if (!Array.isArray(def.roles) || def.roles.length === 0) {
    throw new Error("definition.roles must contain at least one role");
  }
  for (const role of def.roles) {
    if (!role.slug || !String(role.slug).trim()) throw new Error("each role.slug is required");
    if (!role.tier || !String(role.tier).trim()) {
      throw new Error(`role '${role.slug}': tier is required`);
    }
    if (!Array.isArray(role.requiredCapabilities)) {
      throw new Error(`role '${role.slug}': requiredCapabilities is required (may be empty)`);
    }
    if ("model" in (role as object)) {
      throw new Error(
        `role '${role.slug}': model is not allowed on portable agent definitions — binding is local (roles.json)`,
      );
    }
  }
}
