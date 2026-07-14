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
 * The namespaced identity used for every rendered agent artifact: frontmatter `name:`, the
 * emitted file's basename, and any prose that names a role as a spawn/escalation target
 * (chore: petbox-namespaced-agent-names). `role.slug` stays the INTERNAL, unprefixed identity
 * — the definition and `~/.petbox/roles.json` never change — only what apply RENDERS is
 * namespaced. This is the single computation point: every renderer and prose injector must
 * call this (or pass a bare slug through it) instead of interpolating role.slug/a slug string
 * directly into anything user- or harness-facing, or the prefix drifts between call sites.
 *
 * Why: generated agents were occupying the most common user-agent names (`worker`, `explore`,
 * ...) — colliding with a user's own agents, and shadowing Claude Code's built-in `Explore`
 * agent under `.claude/agents/explore.md`. `petbox-<slug>` moves us into our own namespace.
 */
export function emittedRoleName(roleOrSlug: { readonly slug: string } | string): string {
  const slug = typeof roleOrSlug === "string" ? roleOrSlug : roleOrSlug.slug;
  return `petbox-${slug}`;
}

/**
 * Built-in portable roster for offline compile (petbox-wire doctor / apply).
 * Includes `explore` so the roster matches harnesses that ship a built-in explore
 * agent — with an explicit inheritance note (not a global "inheritance forbidden").
 *
 * Caps are honest for the roles: orchestrator needs mcp_main_session + spawn_subagents.
 * Per harness-capabilities.ts, all three known harnesses (claude-code, opencode, droid)
 * declare both, so DEFAULT passes truthfulness on every known harness today — droid in
 * particular declares mcp_main_session, mcp_subagent, spawn_subagents, role_files,
 * dynamic_model_at_spawn and hooks per Factory's docs. This is not guaranteed to hold for
 * future/unknown harnesses; the gate (checkRoleTruthfulness) still blocks any role that
 * claims a capability its target harness does not declare.
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
      // requiredCapabilities stays empty by design: worker is meant to stay portable
      // even to a future/unknown harness without mcp_subagent. This is NOT a claim
      // that MCP is unavailable to worker subagents on today's known harnesses —
      // claude-code, opencode and droid all declare mcp_subagent (harness-capabilities.ts).
      requiredCapabilities: [],
      spawn: { allowed: false },
      escalation: { available: true, targets: ["orchestrator"] },
      notes:
        "Scoped implementation / research leaf. Brief carries any PetBox data the orchestrator already fetched.",
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

/**
 * Recursively reject any property named `model` (portable roster — binding is local).
 * Mirrors C# AgentDefinitionJson.RejectModelField (root, roles[], nested spawn/escalation).
 */
export function rejectModelFields(value: unknown, path = "$"): void {
  if (value === null || value === undefined) return;
  if (Array.isArray(value)) {
    value.forEach((item, i) => rejectModelFields(item, `${path}[${i}]`));
    return;
  }
  if (typeof value !== "object") return;
  for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
    if (k === "model") {
      throw new Error(
        `${path}.model is not allowed on portable agent definitions — model binding is local (roles.json)`,
      );
    }
    rejectModelFields(v, `${path}.${k}`);
  }
}

/** Light structural check; throws on invalid shape (loud, never silent). */
export function validateAgentDefinition(def: AgentDefinition): void {
  if (!def || typeof def !== "object") throw new Error("agent definition is required");
  // Recursive model ban before field checks (symmetry with server RejectModelField).
  rejectModelFields(def, "definition");
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
  }
}
