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

export function rolesPath(homeDir: string = homedir()): string {
  return join(homeDir, ".petbox", "roles.json");
}

function isPlainObject(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function asModelBinding(v: unknown): RoleBinding | null {
  if (!isPlainObject(v)) return null;
  const model = v.model;
  if (typeof model !== "string" || !model.trim()) return null;
  return { model: model.trim() };
}

function asAgentRoles(v: unknown): AgentRoles | null {
  if (!isPlainObject(v)) return null;
  const rolesRaw = v.roles;
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
  const agentsRaw = v.agents;
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
    typeof raw.activeProfile === "string" && raw.activeProfile.trim()
      ? raw.activeProfile.trim()
      : "default";
  const profilesRaw = raw.profiles;
  if (!isPlainObject(profilesRaw)) return { activeProfile, profiles: {} };
  const profiles: Record<string, Profile> = {};
  for (const [name, p] of Object.entries(profilesRaw)) {
    if (!name.trim()) continue;
    profiles[name] = asProfile(p);
  }
  return { activeProfile, profiles };
}

/**
 * Load ~/.petbox/roles.json. Never throws: missing/unreadable/invalid → empty shell
 * ({ activeProfile: "default", profiles: {} }).
 */
export function loadRoles(homeDir: string = homedir()): RolesFile {
  const path = rolesPath(homeDir);
  try {
    if (!existsSync(path)) return { ...EMPTY, profiles: {} };
    const raw = JSON.parse(readFileSync(path, "utf8"));
    return normalizeRoles(raw);
  } catch {
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

/** Role→model map for one agent under the active profile (missing → {}). */
export function resolveAgentRoles(
  data: RolesFile,
  agent: string,
): Readonly<Record<string, string>> {
  const profile = data.profiles[data.activeProfile];
  if (!profile) return {};
  const ar = profile.agents[agent];
  if (!ar) return {};
  const out: Record<string, string> = {};
  for (const [role, b] of Object.entries(ar.roles)) out[role] = b.model;
  return out;
}

/**
 * Pure client helper: observed binding stamp for session metadata.
 * Returns null when the active profile has no roles for this agent (do not invent defaults).
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
    agent,
    roles,
  };
}

/** Bootstrap-safe export shape (no secrets — roles.json has none). */
export function exportRolesBootstrap(data: RolesFile): RolesFile {
  return normalizeRoles(data);
}

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
    const roleNames = Object.keys(ar.roles);
    if (roleNames.length === 0) {
      lines.push("    (no roles)");
      continue;
    }
    for (const role of roleNames) {
      lines.push(`    ${role}: ${ar.roles[role].model}`);
    }
  }
  return lines.join("\n");
}
