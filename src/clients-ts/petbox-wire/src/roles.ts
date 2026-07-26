// Local role→model binding store (~/.petbox/roles.json).
//
// Spec (role-model-binding-local, binding-not-server-authoritative):
//   - Active profile + per-agent role→model bindings live on the machine (owner axis = $HOME).
//   - Server may observe a stamp as session metadata later, but is NEVER the source of truth.
//   - All load/save/resolve paths are offline (no fetch).
//
// Plain TS for native node type-stripping: zero deps. Home is injectable so tests never touch
// the real ~/.petbox.

import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";
import { classifyModel } from "./harness-models.ts";
import { wireLog } from "./wire-log.ts";

export type RoleBinding = {
  readonly model: string;
};

export type AgentRoles = {
  readonly roles: Readonly<Record<string, RoleBinding>>;
};

export type Profile = {
  readonly agents: Readonly<Record<string, AgentRoles>>;
};

export type RolesFile = {
  readonly activeProfile: string;
  readonly profiles: Readonly<Record<string, Profile>>;
};

/** Best-effort observation stamp for a session push (client-side only until the server accepts it). */
export type ObservedBinding = {
  readonly profile: string;
  readonly agent: string;
  readonly roles: Readonly<Record<string, string>>;
};

const EMPTY: RolesFile = { activeProfile: "default", profiles: {} };

/**
 * Canonical agent ids used by session push / harness matrix (`droid-push-session` stamps
 * agent:"droid", HARNESS_IDS uses droid).
 * Aliases accepted in roles.json for the same bucket — lookup is alias-aware so a file
 * written as `factory-droid` still resolves when push asks for `droid`.
 */
export const CANONICAL_AGENT_IDS = ["claude-code", "opencode", "droid"] as const;

const AGENT_ALIASES: Readonly<Record<string, string>> = {
  "factory-droid": "droid",
  factory: "droid",
  cc: "claude-code",
  claude: "claude-code",
};

/** Map any alias / known id to the canonical agent id; unknown strings pass through. */
export function canonicalAgentId(agent: string): string {
  const a = agent.trim();
  if (!a) return a;
  return AGENT_ALIASES[a] ?? a;
}

/** Keys to try when reading agents{} from roles.json for a requested agent id. */
function agentLookupKeys(agent: string): readonly string[] {
  const canon = canonicalAgentId(agent);
  const keys = new Set<string>([agent, canon]);
  for (const [alias, c] of Object.entries(AGENT_ALIASES)) {
    if (c === canon) keys.add(alias);
  }
  return [...keys];
}

export function rolesPath(homeDir: string = homedir()): string {
  return join(homeDir, ".petbox", "roles.json");
}

function isPlainObject(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function asModelBinding(v: unknown): RoleBinding | null {
  if (!isPlainObject(v)) return null;
  const model = v["model"];
  if (typeof model !== "string" || !model.trim()) return null;
  return { model: model.trim() };
}

function asAgentRoles(v: unknown): AgentRoles | null {
  if (!isPlainObject(v)) return null;
  const rolesRaw = v["roles"];
  if (!isPlainObject(rolesRaw)) return { roles: {} };
  const roles: Record<string, RoleBinding> = {};
  for (const [role, binding] of Object.entries(rolesRaw)) {
    const b = asModelBinding(binding);
    if (b) roles[role] = b;
  }
  return { roles };
}

function asProfile(v: unknown): Profile {
  if (!isPlainObject(v)) return { agents: {} };
  const agentsRaw = v["agents"];
  if (!isPlainObject(agentsRaw)) return { agents: {} };
  const agents: Record<string, AgentRoles> = {};
  for (const [agent, ar] of Object.entries(agentsRaw)) {
    const parsed = asAgentRoles(ar);
    if (parsed) agents[agent] = parsed;
  }
  return { agents };
}

/** Light validation: coerce unknown JSON into a RolesFile; drop junk fields. */
export function normalizeRoles(raw: unknown): RolesFile {
  if (!isPlainObject(raw)) return { ...EMPTY };
  const activeProfile =
    typeof raw["activeProfile"] === "string" && raw["activeProfile"].trim()
      ? raw["activeProfile"].trim()
      : "default";
  const profilesRaw = raw["profiles"];
  if (!isPlainObject(profilesRaw)) return { activeProfile, profiles: {} };
  const profiles: Record<string, Profile> = {};
  for (const [name, p] of Object.entries(profilesRaw)) {
    if (!name.trim()) continue;
    profiles[name] = asProfile(p);
  }
  return { activeProfile, profiles };
}

export type LoadRolesOptions = {
  /**
   * `apply`'s polarity ONLY (bug: wire-silent-failures-invisible). A missing roles.json file is
   * always Class A (fresh machine, nothing bound yet) and never throws even in strict mode. But
   * a PRESENT file that fails to parse is exactly the 2026-07-12-incident shape: a corrupt
   * roles.json silently reading as "no bindings" made `apply` render every role with no
   * `model:` line, so every subagent inherited the session's model (the "worker rides on Opus"
   * incident). `apply` must hard-fail on that instead of compiling a falsely-empty roster;
   * everyone else (doctor, the `roles`/`model` CLI, session-push's best-effort read) stays on
   * the default non-strict behavior — silent, but with a Class-Б trace via wireLog.
   */
  readonly strict?: boolean;
};

/**
 * Load ~/.petbox/roles.json.
 * - Missing file → empty shell, always silent (Class A: nothing bound yet is the common case).
 * - Present but unparsable → non-strict: empty shell + a Class-Б line in ~/.petbox/wire.log
 *   (doctor surfaces it); strict (`apply` only): throws, so the caller hard-fails instead of
 *   silently compiling roles as if unbound.
 */
export function loadRoles(homeDir: string = homedir(), opts?: LoadRolesOptions): RolesFile {
  const path = rolesPath(homeDir);
  if (!existsSync(path)) return { ...EMPTY, profiles: {} };
  try {
    const raw = JSON.parse(readFileSync(path, "utf8"));
    return normalizeRoles(raw);
  } catch (e) {
    const detail = `roles.json at ${path} exists but failed to parse — ${e instanceof Error ? e.message : String(e)}`;
    if (opts?.strict) {
      throw new Error(
        `corrupt roles.json (${path}): ${e instanceof Error ? e.message : String(e)} — refusing to ` +
          `treat this as "no bindings" (the 2026-07-12 incident shape: a bad file silently reads as ` +
          `empty, apply then renders every role with no model: line, and every subagent inherits the ` +
          `session's model). Fix or remove the file and re-run apply.`,
      );
    }
    wireLog("roles", detail, homeDir);
    return { ...EMPTY, profiles: {} };
  }
}

/** Persist roles.json (creates ~/.petbox if needed). */
export function saveRoles(data: RolesFile, homeDir: string = homedir()): void {
  const path = rolesPath(homeDir);
  mkdirSync(dirname(path), { recursive: true });
  const normalized = normalizeRoles(data);
  writeFileSync(path, JSON.stringify(normalized, null, 2) + "\n", "utf8");
}

/** True when there is no active profile shell and no agent role bindings at all. */
export function isEmptyRoles(data: RolesFile): boolean {
  const names = Object.keys(data.profiles);
  if (names.length === 0) return true;
  for (const p of Object.values(data.profiles)) {
    for (const a of Object.values(p.agents)) {
      if (Object.keys(a.roles).length > 0) return false;
    }
  }
  // Profiles may exist as empty shells (after `profile use`) — still "empty" for display
  // of bindings, but we still surface the active profile name.
  return Object.values(data.profiles).every((p) => Object.keys(p.agents).length === 0);
}

/**
 * Set activeProfile; create an empty profile shell if the name is new.
 * Returns the updated file (caller should saveRoles).
 */
export function useProfile(data: RolesFile, name: string): RolesFile {
  const n = name.trim();
  if (!n) throw new Error("profile name must be non-empty");
  const profiles: Record<string, Profile> = { ...data.profiles };
  if (!profiles[n]) profiles[n] = { agents: {} };
  return { activeProfile: n, profiles };
}

/** Role→model map for one agent under the active profile (missing → {}). Alias-aware. */
export function resolveAgentRoles(
  data: RolesFile,
  agent: string,
): Readonly<Record<string, string>> {
  const profile = data.profiles[data.activeProfile];
  if (!profile) return {};
  // Prefer exact key, then aliases / canonical (first non-empty wins).
  for (const key of agentLookupKeys(agent)) {
    const ar = profile.agents[key];
    if (!ar) continue;
    const out: Record<string, string> = {};
    for (const [role, b] of Object.entries(ar.roles)) out[role] = b.model;
    if (Object.keys(out).length > 0) return out;
  }
  return {};
}

/**
 * Pure client helper: observed binding stamp for session metadata.
 * Returns null when the active profile has no roles for this agent (do not invent defaults).
 * `agent` on the stamp is always the **canonical** id (e.g. droid, not factory-droid).
 */
export function resolveObservedBinding(
  agent: string,
  homeDir: string = homedir(),
): ObservedBinding | null {
  const data = loadRoles(homeDir);
  const roles = resolveAgentRoles(data, agent);
  if (Object.keys(roles).length === 0) return null;
  return {
    profile: data.activeProfile,
    agent: canonicalAgentId(agent),
    roles,
  };
}

/** Bootstrap-safe export shape (no secrets — roles.json has none). */
export function exportRolesBootstrap(data: RolesFile): RolesFile {
  return normalizeRoles(data);
}

export type SetRoleModelResult =
  | { readonly ok: true; readonly data: RolesFile; readonly warning?: string }
  | { readonly ok: false; readonly reason: string };

/**
 * Set one role's model binding for `agent` (alias-aware) under a profile (default: the
 * active one; created as an empty shell if new). This is `model set`'s core (spec
 * binding-set-by-tool) — the only writer of a role→model binding besides a text editor.
 *
 * Validated against the SAME three-tier policy `apply`/`doctor` already gate with
 * (harness-models.ts's classifyModel) — this function does not re-derive that policy, only
 * calls it. "foreign" (a recognizably different harness's id shape, e.g. droid's `custom:*`
 * landing in a claude-code binding — the 2026-07-12 incident shape) is refused unless
 * `allowUnknownModel` is set. "known" and "unknown" (shape-valid, just not on the small
 * known-alias list) both write; "unknown" (and a forced "foreign") come back with a
 * non-blocking `warning` for the caller to print (mirrors modelShapeWarning in truthfulness.ts).
 */
export function setRoleModel(
  data: RolesFile,
  opts: {
    readonly agent: string;
    readonly role: string;
    readonly model: string;
    readonly profile?: string;
    readonly allowUnknownModel?: boolean;
  },
): SetRoleModelResult {
  const canon = canonicalAgentId(opts.agent);
  const role = opts.role.trim();
  const model = opts.model.trim();
  if (!role) return { ok: false, reason: "role is required" };
  if (!model) {
    return {
      ok: false,
      reason: "model is required (use `model unset <role>` to clear a binding)",
    };
  }

  const cls = classifyModel(canon, model);
  if (cls === "foreign" && !opts.allowUnknownModel) {
    return {
      ok: false,
      reason:
        `model '${model}' looks like another harness's id shape, not one harness '${canon}' would ` +
        `own — refusing to write it (the 2026-07-12 incident shape: a foreign id landing in the ` +
        `wrong harness's binding). Pass --allow-unknown-model to force it through if you are certain.`,
    };
  }

  const profileName = opts.profile?.trim() || data.activeProfile;
  const profiles: Record<string, Profile> = { ...data.profiles };
  const existingProfile = profiles[profileName] ?? { agents: {} };
  const existingAgent = existingProfile.agents[canon] ?? { roles: {} };
  profiles[profileName] = {
    agents: {
      ...existingProfile.agents,
      [canon]: { roles: { ...existingAgent.roles, [role]: { model } } },
    },
  };
  const next: RolesFile = { activeProfile: data.activeProfile, profiles };

  if (cls === "foreign") {
    return {
      ok: true,
      data: next,
      warning:
        `model '${model}' does not match the id shape for harness '${canon}' — written anyway ` +
        `because --allow-unknown-model was passed. If '${canon}' cannot resolve it, that fails LOUD ` +
        `at runtime, not silently.`,
    };
  }
  if (cls === "unknown") {
    return {
      ok: true,
      data: next,
      warning:
        `model '${model}' is not on the known-alias list for harness '${canon}' but matches its id ` +
        `shape — written unverified. If '${canon}' cannot resolve it, that fails LOUD at runtime, ` +
        `not silently.`,
    };
  }
  return { ok: true, data: next };
}

export type UnsetRoleModelResult = {
  readonly data: RolesFile;
  readonly removed: boolean;
};

/**
 * Remove one role's model binding for `agent` (alias-aware) under a profile (default: the
 * active one). No-op-safe: an absent profile/agent/role returns `removed: false` and the
 * SAME `data` reference back, never throws. This is `model unset`'s core — a fair-empty
 * binding a role can hold on purpose (e.g. `reserve`, when the tester's machine lacks
 * access to the tier the role would otherwise be bound to).
 */
export function unsetRoleModel(
  data: RolesFile,
  opts: { readonly agent: string; readonly role: string; readonly profile?: string },
): UnsetRoleModelResult {
  const canon = canonicalAgentId(opts.agent);
  const role = opts.role.trim();
  const profileName = opts.profile?.trim() || data.activeProfile;
  const existingProfile = data.profiles[profileName];
  if (!existingProfile) return { data, removed: false };
  const existingAgent = existingProfile.agents[canon];
  if (!existingAgent || !(role in existingAgent.roles)) {
    return { data, removed: false };
  }
  const restRoles: Record<string, RoleBinding> = {};
  for (const [r, b] of Object.entries(existingAgent.roles)) {
    if (r !== role) restRoles[r] = b;
  }
  const profiles: Record<string, Profile> = {
    ...data.profiles,
    [profileName]: {
      agents: { ...existingProfile.agents, [canon]: { roles: restRoles } },
    },
  };
  return { data: { activeProfile: data.activeProfile, profiles }, removed: true };
}

// Default claude-code role→model seed for a BRAND-NEW machine (fresh-wire-roster-unusable):
// aliases only (never a concrete id — see harness-models.ts / the claude-api skill's live
// finding that the Task tool's `model` param is a closed enum of exactly these four tiers).
//
// `reserve` is bound too (reversing an earlier "leave it deliberately absent" call —
// reserve-unbound-inherits-session-model, owner decision 2026-07-26). It is bound to an ALIAS
// (`fable`), never a concrete model id: an id is exactly the foreign-shape mistake the
// 2026-07-12 incident was (a droid id landing in a claude-code binding) — an alias is
// harness-portable, and if the tier is genuinely unavailable on this machine it fails LOUD at
// spawn time (a closed Task-tool enum rejects it up front), never silently resolving to
// something else. It is bound to the STRONGEST tier on purpose, not a cautious default: the
// umbrella this card sits under (newcomer-equivalent-experience) means a newcomer's experience
// should match the owner's, and on the owner's own machine reserve already rides the strongest
// tier. reserve is also the one role where a weaker tier would defeat its own purpose — it is
// the second pair of eyes called in only once everything else has already been tried and failed,
// so it is never the role to economize on.
//
// Lives here (not wire.ts) so it has exactly ONE reader-safe source of truth: wire.ts's
// seedDefaultRoleBindingsIfMissing (the writer, on a fresh machine) AND status.ts's per-role
// model-source enumeration (a reader, on any machine) both need the identical map — wire.ts runs
// main() at module top level and must never be imported by a side module (see wire.ts's own file
// header), so a constant only wire.ts could see would force status.ts into either importing
// wire.ts (breaking that rule) or re-declaring a second copy that silently drifts from this one.
export const DEFAULT_ROLE_MODEL_SEED: Readonly<Record<string, string>> = {
  orchestrator: "opus",
  worker: "sonnet",
  utility: "haiku",
  explore: "haiku",
  reserve: "fable",
};

/** Human-readable dump of the active profile's agent/role/model tree. */
export function formatResolvedBinding(data: RolesFile): string {
  const lines: string[] = [];
  lines.push(`activeProfile: ${data.activeProfile}`);
  const profile = data.profiles[data.activeProfile];
  if (!profile || Object.keys(profile.agents).length === 0) {
    lines.push("(no agent role bindings for this profile)");
    return lines.join("\n");
  }
  for (const [agent, ar] of Object.entries(profile.agents)) {
    lines.push(`  ${agent}:`);
    const roleEntries = Object.entries(ar.roles);
    if (roleEntries.length === 0) {
      lines.push("    (no roles)");
      continue;
    }
    for (const [role, binding] of roleEntries) {
      lines.push(`    ${role}: ${binding.model}`);
    }
  }
  return lines.join("\n");
}
