// Portable agent definition (roster only — no model binding).
//
// Spec (agent-definition-as-data, agent-definition-locality):
//   - Roles carry slug, tier, requiredCapabilities, spawn, escalation.
//   - model is NEVER part of this document (local binding lives in roles.json).
//   - Built-in DEFAULT_AGENT_DEFINITION ships with the kit for offline compile;
//     apply tries server fetch first (agent-def-fetch.ts) and falls back here.
//
// Plain TS for native node type-stripping: zero deps.

import { readFileSync } from "node:fs";
import { join } from "node:path";
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

/** Resolved next to this module, and therefore inside the published npm package. */
const DEFAULT_AGENT_DEFINITION_PATH = join(import.meta.dirname, "default-agents.json");

/**
 * Built-in portable roster for offline compile (petbox-wire doctor / apply), read from the ONE
 * canonical copy of the document: the repo's `src/common/default-agents.json`.
 *
 * It is NOT declared here as a literal, and that is the whole point. The PetBox server seeds the
 * very same document into every project it creates (PetBox.Core.Contract.DefaultAgentDefinition
 * embeds the identical file), so a second, hand-maintained transcription in TypeScript would be a
 * copy that drifts — and a test comparing the two would only be a ratchet against a problem the
 * copy itself created. One file, two readers, nothing to keep in sync.
 *
 * It ships INSIDE the package: `scripts/sync-default-agents.mjs` copies the canonical file into
 * this directory before test / typecheck / pack, and `package.json`'s `files` allowlist puts it in
 * the tarball. That is load-bearing for the kit's contract — the baseline is the OFFLINE fallback,
 * used exactly when PetBox is unreachable and no LKG cache exists, so it must be physically
 * present on disk and can never be fetched. Missing file = a loud throw at import, never a silent
 * empty roster.
 *
 * Validated on load with this module's own validateAgentDefinition (no second validator), so a
 * malformed canonical document fails here rather than producing broken role artifacts downstream.
 *
 * Includes `explore` so the roster matches harnesses that ship a built-in explore agent — with an
 * explicit inheritance note (not a global "inheritance forbidden").
 *
 * Caps are honest for the roles: orchestrator needs mcp_main_session + spawn_subagents.
 * Per harness-capabilities.ts, all three known harnesses (claude-code, opencode, droid) declare
 * both, so DEFAULT passes truthfulness on every known harness today — droid in particular declares
 * mcp_main_session, mcp_subagent, spawn_subagents, role_files, dynamic_model_at_spawn and hooks per
 * Factory's docs. This is not guaranteed to hold for future/unknown harnesses; the gate
 * (checkRoleTruthfulness) still blocks any role that claims a capability its target harness does
 * not declare.
 */
export const DEFAULT_AGENT_DEFINITION: AgentDefinition = loadDefaultAgentDefinition();

function loadDefaultAgentDefinition(): AgentDefinition {
  let raw: string;
  try {
    raw = readFileSync(DEFAULT_AGENT_DEFINITION_PATH, "utf8");
  } catch (err) {
    throw new Error(
      `petbox-wire: the built-in agent roster is missing at ${DEFAULT_AGENT_DEFINITION_PATH}. ` +
        `In a checkout run \`npm run sync-default-agents\` (it copies src/common/default-agents.json ` +
        `into this package); in an installed package this file is part of the published tarball, so ` +
        `its absence means a broken install. Cause: ${err instanceof Error ? err.message : String(err)}`,
    );
  }

  const parsed = JSON.parse(raw) as AgentDefinition;
  validateAgentDefinition(parsed);
  return parsed;
}

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

/** Count top-level numbered rules in a role's notes (lines like "1. **...**"). A proxy for
 * "how many protocol rules does this role carry" without depending on prose wording. */
function countRules(notes: string | undefined): number {
  if (!notes) return 0;
  const matches = notes.match(/^\d+\.\s/gm);
  return matches ? matches.length : 0;
}

/**
 * By-SUBSTANCE diff between the built-in offline default (DEFAULT_AGENT_DEFINITION) and a live
 * server definition — used by `doctor` (bug: builtin-definition-drifts-no-catchup /
 * doctor-drift-conflates-degradation-and-divergence) to name what changed in terms an operator can
 * act on (which role, what disagrees) rather than dumping a raw text/byte diff. Deliberately
 * coarse: rule COUNT and exact notes-text equality, not a line-level diff — good enough to say
 * "the orchestrator has 7 rules vs 8 live" without becoming its own maintenance burden every time
 * prose is reworded without changing meaning.
 *
 * Two diagnoses, split HERE (in the data) so no caller can flatten them back into one shout:
 *   - degradations: a role exists live but not in the built-in default. This is NORM — the
 *     built-in is an emergency bootstrap minimum for offline compile, not a mirror of the live
 *     document; a role added server-side is expected to be missing from the kit until its next
 *     release, and an offline compile will simply ship without that role.
 *   - divergences: a role exists in BOTH but disagrees (tier / rule count / notes text), or a role
 *     exists in the built-in but not live (the kit promises a role the project doesn't have). Both
 *     are real drift and worth shouting about.
 */
export type AgentDefinitionDiff = {
  /** Role present live, absent from built-in — expected; not drift. */
  readonly degradations: ReadonlyArray<string>;
  /** Built-in and live disagree on a shared role, or built-in promises a role live doesn't have. */
  readonly divergences: ReadonlyArray<string>;
};

export function diffAgentDefinitions(builtin: AgentDefinition, live: AgentDefinition): AgentDefinitionDiff {
  const degradations: string[] = [];
  const divergences: string[] = [];
  const builtinBySlug = new Map(builtin.roles.map((r) => [r.slug, r] as const));
  const liveBySlug = new Map(live.roles.map((r) => [r.slug, r] as const));

  for (const slug of liveBySlug.keys()) {
    if (!builtinBySlug.has(slug)) {
      degradations.push(
        `role '${slug}' exists in the live definition but not in the built-in default — expected: the ` +
          `built-in is an offline bootstrap minimum, not a mirror of the server; an offline compile will ` +
          `simply ship without this role`,
      );
    }
  }
  for (const slug of builtinBySlug.keys()) {
    if (!liveBySlug.has(slug)) {
      divergences.push(`role '${slug}' exists in the built-in default but not in the live definition`);
    }
  }

  for (const [slug, builtinRole] of builtinBySlug) {
    const liveRole = liveBySlug.get(slug);
    if (!liveRole) continue;
    if (builtinRole.tier !== liveRole.tier) {
      divergences.push(`role '${slug}': tier "${builtinRole.tier}" (built-in) vs "${liveRole.tier}" (live)`);
    }
    const builtinRuleCount = countRules(builtinRole.notes);
    const liveRuleCount = countRules(liveRole.notes);
    if (builtinRuleCount !== liveRuleCount) {
      divergences.push(
        `role '${slug}': built-in default has ${builtinRuleCount} rule(s), live definition has ${liveRuleCount}`,
      );
    } else if ((builtinRole.notes ?? "") !== (liveRole.notes ?? "")) {
      divergences.push(
        `role '${slug}': notes text differs from the live definition (same rule count: ${builtinRuleCount})`,
      );
    }
  }

  return { degradations, divergences };
}
