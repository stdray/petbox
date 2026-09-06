// Portable agent definition (roster only — no model binding).
//
// Spec (agent-definition-as-data, agent-definition-locality, definition-layer-cascade):
//   - Roles carry slug, tier, requiredCapabilities, spawn, escalation.
//   - model is NEVER part of this document (local binding lives in roles.json).
//   - DEFAULT_AGENT_DEFINITION is the kit's shipped BASE LAYER — the bottom of the file cascade
//     base < user < project (definition-source.ts). It is not a fallback for a failed fetch:
//     nothing in this kit fetches a definition any more. It is the floor every resolve starts
//     from, always present, and its absence throws at import (below).
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

/**
 * Resolved next to this module, and therefore inside the published npm package. Exported so the
 * cascade can name it as the `base` layer's provenance path — a reader who sees `tier=base` must
 * be able to find the file that said so.
 */
export const DEFAULT_AGENT_DEFINITION_PATH = join(import.meta.dirname, "default-agents.json");

/**
 * The kit's shipped BASE LAYER — the bottom of the definition cascade base < user < project
 * (definition-source.ts) — read from the ONE canonical copy of the document: the repo's
 * `src/common/default-agents.json`.
 *
 * It is NOT declared here as a literal, and that is the whole point. The PetBox server seeds the
 * very same document into every project it creates (PetBox.Core.Contract.DefaultAgentDefinition
 * embeds the identical file), so a second, hand-maintained transcription in TypeScript would be a
 * copy that drifts — and a test comparing the two would only be a ratchet against a problem the
 * copy itself created. One file, two readers, nothing to keep in sync.
 *
 * It ships INSIDE the package: `scripts/sync-default-agents.mjs` copies the canonical file into
 * this directory before test / typecheck / pack, and `package.json`'s `files` allowlist puts it in
 * the tarball. That is load-bearing for the kit's contract — this document is the FLOOR of every
 * resolve, on every machine, online or not, so it must be physically present on disk and can
 * never be fetched. Missing file = a loud throw at import, never a silent empty roster. That
 * throw is also why the base is modelled as a parsed document rather than as an optional layer
 * directory: an optional directory's absence means "no opinion", and the floor may never be
 * allowed to mean that.
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

/**
 * This package's delivery identifier (e.g. "0.1.0-ci.2197", or "0.0.0+5991098e2824" — see
 * below) — a LABEL for DEFAULT_AGENT_DEFINITION in apply/status/SessionStart output ("kit
 * baseline v0.1.0-ci.2197"), never a definition version number (the JSON document above carries
 * no `version` field of its own; that concept belonged to the server envelope this kit no longer
 * fetches for user-scope roles — card user-scope-roles-rendered-from-cwd-project-definition).
 *
 * TWO sources, tried in order (card kit-version-unknown-inside-hooks):
 *
 *   1. `../package.json` (this module's own directory's parent). Present in a real install
 *      (npx cache, or a checkout) and in a running `npx petbox-wire`/`wire.ts` invocation — HERE
 *      always sits next to package.json there. Absent for a HOOK: pull-memory.ts /
 *      droid-pull-memory.ts / opencode-plugin.ts all run from the STABLE mirror
 *      (~/.petbox/wire/), and copyKitToStable (wire.ts) copies only HERE (this src/ dir) into
 *      it, never the sibling package.json — so `../package.json` from a hook's
 *      import.meta.dirname resolves to ~/.petbox/package.json, which does not exist.
 *   2. `../kit-version.json` — a delivery STAMP wire.ts's copyKitToStable writes next to (not
 *      inside) the mirror, specifically so a hook can find it by the exact same `..` resolution
 *      that just missed package.json: from ~/.petbox/wire/, `..` is ~/.petbox either way. Not
 *      inside the mirror because pruneStaleMirrorEntries deletes anything under STABLE that HERE
 *      does not also ship — a stamp living there would be wiped and rewritten every run.
 *
 * The stamp's `version` is paired with its `kitHash` (`${version}+${kitHash}`, semver
 * build-metadata syntax) whenever both are present, NOT bare version alone: this package's
 * checked-in package.json version is permanently "0.0.0" (CI only stamps the real semver on
 * publish, via GitVersion, without committing it — build.cs's TsWirePack), so a checkout-sourced
 * kit's bare version never changes between real code changes and cannot tell one generation of
 * shipped prose from another. The hash (already computed for `update`'s own before/after log)
 * changes on every content change regardless of version, which is what makes the label
 * MEANINGFUL rather than merely non-empty — the actual defect this card reports (a blind
 * provenance line is worse than a merely uninformative one, since nothing then contradicts it).
 *
 * Best-effort at every step: a missing/corrupt package.json falls through to the stamp; a
 * missing/corrupt stamp falls through to "unknown" — same soft degradation as before this card,
 * never a throw. A hook must never fail or noticeably slow down over a label.
 */
export const KIT_VERSION: string = loadKitVersion();

function loadKitVersion(): string {
  try {
    const raw = readFileSync(join(import.meta.dirname, "..", "package.json"), "utf8");
    const parsed = JSON.parse(raw) as { version?: unknown };
    if (typeof parsed.version === "string" && parsed.version.trim()) return parsed.version.trim();
  } catch {
    // no package.json next to HERE — expected in hook context (STABLE mirror); fall through.
  }
  try {
    const raw = readFileSync(join(import.meta.dirname, "..", "kit-version.json"), "utf8");
    const parsed = JSON.parse(raw) as { version?: unknown; kitHash?: unknown };
    const version = typeof parsed.version === "string" ? parsed.version.trim() : "";
    const kitHash = typeof parsed.kitHash === "string" ? parsed.kitHash.trim() : "";
    if (version && kitHash) return `${version}+${kitHash}`;
    if (version) return version;
  } catch {
    // no stamp either — e.g. this mirror predates the fix, or `update`/full wire never ran
    // since. Degrade the same way loadKitVersion always has: "unknown", never a throw.
  }
  return "unknown";
}

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
