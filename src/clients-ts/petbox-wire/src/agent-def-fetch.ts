// Fetch a portable agent definition from PetBox for petbox-wire apply.
//
// Contract (server: AgentDefsApi):
//   GET {baseUrl}/api/{projectKey}/agent-defs/{key}
//   header X-Api-Key; scope agents:read
//   200 → { key, version, definition: { name, roles: [...] }, created?, updated? }
//
// Best-effort: network / 404 / auth / bad shape / timeout → null (never throws).
// Offline compile uses DEFAULT_AGENT_DEFINITION when this returns null.
//
// Plain TS for native node type-stripping: zero deps.

import type { AgentDefinition, AgentRole, RoleEscalation, RoleSpawn } from "./agent-definition.ts";

export const DEFAULT_DEFINITION_KEY = "default";
export const AGENT_DEF_FETCH_TIMEOUT_MS = 8000;

/** Successful server fetch: key + version envelope around a portable definition. */
export type FetchedAgentDefinition = {
  readonly key: string;
  readonly version: number;
  readonly definition: AgentDefinition;
};

export type FetchAgentDefinitionOptions = {
  readonly baseUrl: string;
  readonly projectKey: string;
  readonly apiKey: string;
  /** Document key; defaults to `default`. */
  readonly definitionKey?: string;
  readonly timeoutMs?: number;
  /** Injected for tests; defaults to global fetch. */
  readonly fetchImpl?: typeof fetch;
};

/**
 * Pure JSON → definition mapping. Returns null on any invalid/incomplete shape.
 * Tolerates extra fields; rejects role.model (portable roster only).
 */
export function parseAgentDefinitionResponse(json: unknown): FetchedAgentDefinition | null {
  if (!json || typeof json !== "object") return null;
  const root = json as Record<string, unknown>;

  const key = typeof root.key === "string" && root.key.trim() ? root.key.trim() : null;
  const version = parseVersion(root.version);
  if (key === null || version === null) return null;

  const defRaw = root.definition;
  if (!defRaw || typeof defRaw !== "object") return null;
  const def = defRaw as Record<string, unknown>;

  const name = typeof def.name === "string" && def.name.trim() ? def.name.trim() : null;
  if (name === null) return null;
  if (!Array.isArray(def.roles) || def.roles.length === 0) return null;

  const roles: AgentRole[] = [];
  for (const item of def.roles) {
    const role = mapRole(item);
    if (role === null) return null;
    roles.push(role);
  }

  return {
    key,
    version,
    definition: { name, roles },
  };
}

/**
 * GET the named agent definition. Returns the mapped definition on 200 + valid body;
 * null on any failure (network, timeout, non-OK status, bad JSON, invalid shape).
 * Never throws.
 */
export async function fetchAgentDefinition(
  opts: FetchAgentDefinitionOptions,
): Promise<FetchedAgentDefinition | null> {
  try {
    const base = String(opts.baseUrl ?? "").replace(/\/+$/, "");
    const project = String(opts.projectKey ?? "").trim();
    const apiKey = String(opts.apiKey ?? "").trim();
    const defKey = (opts.definitionKey?.trim() || DEFAULT_DEFINITION_KEY).trim();
    if (!base || !project || !apiKey || !defKey) return null;

    const timeoutMs =
      typeof opts.timeoutMs === "number" && opts.timeoutMs > 0
        ? opts.timeoutMs
        : AGENT_DEF_FETCH_TIMEOUT_MS;
    const fetchFn = opts.fetchImpl ?? fetch;

    const ctrl = new AbortController();
    const timer = setTimeout(() => ctrl.abort(), timeoutMs);
    try {
      const url = `${base}/api/${encodeURIComponent(project)}/agent-defs/${encodeURIComponent(defKey)}`;
      const resp = await fetchFn(url, {
        method: "GET",
        headers: { "X-Api-Key": apiKey },
        signal: ctrl.signal,
      });
      if (!resp.ok) return null;
      const body = (await resp.json().catch(() => null)) as unknown;
      return parseAgentDefinitionResponse(body);
    } finally {
      clearTimeout(timer);
    }
  } catch {
    return null;
  }
}

function parseVersion(v: unknown): number | null {
  if (typeof v === "number" && Number.isFinite(v) && v >= 0) return Math.trunc(v);
  if (typeof v === "string" && v.trim()) {
    const n = Number(v);
    if (Number.isFinite(n) && n >= 0) return Math.trunc(n);
  }
  return null;
}

function mapRole(item: unknown): AgentRole | null {
  if (!item || typeof item !== "object") return null;
  const r = item as Record<string, unknown>;

  // Portable definitions must not carry model binding.
  if ("model" in r) return null;

  const slug = typeof r.slug === "string" && r.slug.trim() ? r.slug.trim() : null;
  const tier = typeof r.tier === "string" && r.tier.trim() ? r.tier.trim() : null;
  if (slug === null || tier === null) return null;

  if (!Array.isArray(r.requiredCapabilities)) return null;
  const requiredCapabilities: string[] = [];
  for (const c of r.requiredCapabilities) {
    if (typeof c !== "string") return null;
    requiredCapabilities.push(c);
  }

  const spawn = mapSpawn(r.spawn);
  if (spawn === "invalid") return null;
  const escalation = mapEscalation(r.escalation);
  if (escalation === "invalid") return null;

  const notes =
    typeof r.notes === "string" && r.notes.trim() ? r.notes.trim() : undefined;

  const role: AgentRole = {
    slug,
    tier,
    requiredCapabilities,
    ...(spawn ? { spawn } : {}),
    ...(escalation ? { escalation } : {}),
    ...(notes !== undefined ? { notes } : {}),
  };
  return role;
}

function mapSpawn(raw: unknown): RoleSpawn | null | "invalid" {
  if (raw === undefined || raw === null) return null;
  if (typeof raw !== "object") return "invalid";
  const s = raw as Record<string, unknown>;
  if (typeof s.allowed !== "boolean") return "invalid";
  let allowedRoles: string[] | undefined;
  if (s.allowedRoles !== undefined && s.allowedRoles !== null) {
    if (!Array.isArray(s.allowedRoles)) return "invalid";
    allowedRoles = [];
    for (const x of s.allowedRoles) {
      if (typeof x !== "string") return "invalid";
      allowedRoles.push(x);
    }
  }
  return allowedRoles !== undefined
    ? { allowed: s.allowed, allowedRoles }
    : { allowed: s.allowed };
}

function mapEscalation(raw: unknown): RoleEscalation | null | "invalid" {
  if (raw === undefined || raw === null) return null;
  if (typeof raw !== "object") return "invalid";
  const e = raw as Record<string, unknown>;
  if (typeof e.available !== "boolean") return "invalid";
  let targets: string[] | undefined;
  if (e.targets !== undefined && e.targets !== null) {
    if (!Array.isArray(e.targets)) return "invalid";
    targets = [];
    for (const x of e.targets) {
      if (typeof x !== "string") return "invalid";
      targets.push(x);
    }
  }
  return targets !== undefined
    ? { available: e.available, targets }
    : { available: e.available };
}
