// Fetch a portable agent definition from PetBox for petbox-wire apply.
//
// Contract (server: AgentDefsApi):
//   GET {baseUrl}/api/{projectKey}/agent-defs/{key}
//   header X-Api-Key; scope agents:read
//   200 → { key, version, definition: { name, roles: [...] }, created?, updated? }
//
// Polarity (definition-offline-lkg / wiring-memory-canon):
//   Server is authoritative; disk is LKG replica under ~/.petbox/cache/<project>.agent-def.json.
//   roles.json is the opposite polarity (disk authoritative) — not touched here.
//
// Resolution order on apply:
//   1. live server fetch (unless --offline)
//   2. LKG cache from last successful fetch (with explicit staleness mark)
//   3. DEFAULT_AGENT_DEFINITION only when no cache exists (fresh machine)
//
// Best-effort: network / 404 / auth / bad JSON / timeout → null from fetch (never throws).
//
// Plain TS for native node type-stripping: zero deps.

import { mkdirSync, readFileSync, writeFileSync, existsSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import type { AgentDefinition, AgentRole, RoleEscalation, RoleSpawn } from "./agent-definition.ts";
import { DEFAULT_AGENT_DEFINITION, validateAgentDefinition } from "./agent-definition.ts";
import { wireLog } from "./wire-log.ts";

export const DEFAULT_DEFINITION_KEY = "default";
export const AGENT_DEF_FETCH_TIMEOUT_MS = 8000;

/** Shown when apply uses LKG cache instead of a live server fetch (definition-offline-lkg). */
export const AGENT_DEF_STALE_MARKER =
  "⚠ Agent definition is from the local LKG cache (PetBox unreachable) — may be stale.";

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

export type AgentDefCacheRecord = {
  readonly key: string;
  readonly version: number;
  readonly fetchedAt: string;
  readonly definition: AgentDefinition;
};

/** Where a resolved definition came from (for logs / tests). */
export type AgentDefSource = "server" | "lkg" | "default";

export type ResolvedAgentDefinition = {
  readonly definition: AgentDefinition;
  readonly source: AgentDefSource;
  /** True when source === "lkg" (explicit staleness). */
  readonly stale: boolean;
  readonly key?: string;
  readonly version?: number;
  /** Human-facing line for apply logs when stale. */
  readonly staleMarker?: string;
  /**
   * Only meaningful when source === "default": the live fetch reached the server and it
   * replied 404 (this project simply has no own definition — normal for a fresh project),
   * as opposed to a genuine offline/unreachable/error condition. Lets callers avoid saying
   * "no server" when the server was in fact reachable (agent-def-404-not-offline).
   */
  readonly notFoundOnServer?: boolean;
  /**
   * The live fetch reached the server and it replied 401/403 — this key is missing the
   * agents:read scope — as opposed to a genuine network/timeout/5xx failure that never reached
   * the server at all. Set alongside source "default" (no cache to fall back to) or "lkg" (a
   * cache existed, but the caller should know the server was reachable-and-refusing, not
   * offline). wire-silent-failures-invisible, evidence 2026-07-26: without this a 403 rendered
   * as "offline default definition (no server, no LKG cache)" — a permissions problem read as a
   * network one, which sent the owner chasing the wrong fix.
   */
  readonly forbidden?: boolean;
  /**
   * The live fetch reached the server and got a non-2xx status that is neither a 404
   * (notFoundOnServer) nor a 401/403 (forbidden) — e.g. a 500, or PetBox's own self-recovering
   * 503 deploy_in_progress. The server ANSWERED; this must never be described as "unreachable"
   * (bug: doctor-reports-answering-server-unreachable — same class as
   * probe-collapses-http-errors-into-network's WorkspaceProbeResult "http-error", reused here
   * rather than reinvented). Carries `status` so callers can name it, plus best-effort
   * `retryAfterSeconds` when the error body carries one (PetBox's 503 shape).
   */
  readonly httpError?: { readonly status: number; readonly retryAfterSeconds?: number };
  /**
   * The live fetch got a 2xx response but the body did not parse as a valid definition
   * (bad JSON or wrong shape) — the server ANSWERED, this is a data problem, not connectivity.
   */
  readonly parseError?: boolean;
};

export function agentDefCacheDir(homeDir: string = homedir()): string {
  return join(homeDir, ".petbox", "cache");
}

/** Path: ~/.petbox/cache/<project>.agent-def.json (project may contain $ etc.). */
export function agentDefCachePath(projectKey: string, homeDir: string = homedir()): string {
  // Same sanitization style as canon: use project key as filename stem.
  const stem = String(projectKey).trim() || "unknown";
  return join(agentDefCacheDir(homeDir), `${stem}.agent-def.json`);
}

/**
 * Pure JSON → definition mapping. Returns null on any invalid/incomplete shape.
 * Tolerates extra fields; rejects role.model (portable roster only).
 */
export function parseAgentDefinitionResponse(json: unknown): FetchedAgentDefinition | null {
  if (!json || typeof json !== "object") return null;
  const root = json as Record<string, unknown>;

  const key = typeof root["key"] === "string" && root["key"].trim() ? root["key"].trim() : null;
  const version = parseVersion(root["version"]);
  if (key === null || version === null) return null;

  const defRaw = root["definition"];
  if (!defRaw || typeof defRaw !== "object") return null;
  const def = defRaw as Record<string, unknown>;

  const name = typeof def["name"] === "string" && def["name"].trim() ? def["name"].trim() : null;
  if (name === null) return null;
  if (!Array.isArray(def["roles"]) || def["roles"].length === 0) return null;

  const roles: AgentRole[] = [];
  for (const item of def["roles"]) {
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

/** Richer outcome behind fetchAgentDefinition: keeps the HTTP status (when the request
 * actually reached the server) so callers can tell a 404 (server reachable, this project
 * just has no own definition — normal) apart from a genuine network/timeout/error failure
 * (agent-def-404-not-offline). `status` is null when no response was ever obtained. */
async function fetchAgentDefinitionRaw(
  opts: FetchAgentDefinitionOptions,
): Promise<{
  definition: FetchedAgentDefinition | null;
  status: number | null;
  /** True iff a 2xx response was actually received (even if the body then failed to parse) —
   * lets the caller separate a genuine HTTP error status from a 2xx-but-unparseable body. */
  ok: boolean;
  /** Best-effort peek at the error body for PetBox's `{"retryAfterSeconds": N}` 503 shape. */
  retryAfterSeconds?: number;
}> {
  try {
    const base = String(opts.baseUrl ?? "").replace(/\/+$/, "");
    const project = String(opts.projectKey ?? "").trim();
    const apiKey = String(opts.apiKey ?? "").trim();
    const defKey = (opts.definitionKey?.trim() || DEFAULT_DEFINITION_KEY).trim();
    if (!base || !project || !apiKey || !defKey) return { definition: null, status: null, ok: false };

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
        // Connection: close — no lingering keep-alive socket after a short-lived hook
        // process's single request (see canon.ts's fetchCanon for the full rationale).
        headers: { "X-Api-Key": apiKey, Connection: "close" },
        signal: ctrl.signal,
      });
      if (!resp.ok) {
        // The server answered with an error status — never "network"/"unreachable" (same
        // taxonomy as skill-files.ts's probeWorkspace "http-error"). Best-effort peek at the
        // body for a retryAfterSeconds field (PetBox's 503 deploy_in_progress shape); an
        // unparseable or absent body still carries a meaningful status code.
        let retryAfterSeconds: number | undefined;
        try {
          const errBody = (await resp.json()) as { retryAfterSeconds?: unknown } | null;
          if (typeof errBody?.retryAfterSeconds === "number") retryAfterSeconds = errBody.retryAfterSeconds;
        } catch {
          // best-effort only
        }
        return {
          definition: null,
          status: resp.status,
          ok: false,
          ...(retryAfterSeconds !== undefined ? { retryAfterSeconds } : {}),
        };
      }
      const body = (await resp.json().catch(() => null)) as unknown;
      return { definition: parseAgentDefinitionResponse(body), status: resp.status, ok: true };
    } finally {
      clearTimeout(timer);
    }
  } catch {
    return { definition: null, status: null, ok: false };
  }
}

/**
 * GET the named agent definition. Returns the mapped definition on 200 + valid body;
 * null on any failure (network, timeout, non-OK status, bad JSON, invalid shape).
 * Never throws. Does NOT write LKG — caller uses writeAgentDefCache on success.
 */
export async function fetchAgentDefinition(
  opts: FetchAgentDefinitionOptions,
): Promise<FetchedAgentDefinition | null> {
  const { definition } = await fetchAgentDefinitionRaw(opts);
  return definition;
}

/** Persist LKG after a successful fetch. Never throws. */
export function writeAgentDefCache(
  projectKey: string,
  fetched: FetchedAgentDefinition,
  homeDir: string = homedir(),
  now: () => string = () => new Date().toISOString(),
): void {
  try {
    const dir = agentDefCacheDir(homeDir);
    mkdirSync(dir, { recursive: true });
    const record: AgentDefCacheRecord = {
      key: fetched.key,
      version: fetched.version,
      fetchedAt: now(),
      definition: fetched.definition,
    };
    writeFileSync(agentDefCachePath(projectKey, homeDir), JSON.stringify(record, null, 2) + "\n", "utf8");
  } catch {
    // best-effort
  }
}

/**
 * Read LKG cache. Returns null if missing/corrupt. Never throws.
 *
 * Missing (existsSync false) is Class A — the overwhelmingly common case (fresh machine, never
 * fetched successfully yet) — and stays silent. Present-but-broken (bad JSON, wrong shape,
 * fails policy validation) is Class Б (bug: wire-silent-failures-invisible, card item 2: "битый
 * LKG-кеш неотличим от «кеша нет»") — the caller (resolveAgentDefinitionWithLkg) still falls
 * through to DEFAULT exactly as before, but a wireLog line now distinguishes "there never was a
 * cache" from "there was one and it's broken", which doctor can surface.
 */
export function readAgentDefCache(
  projectKey: string,
  homeDir: string = homedir(),
): AgentDefCacheRecord | null {
  const path = agentDefCachePath(projectKey, homeDir);
  if (!existsSync(path)) return null;
  const corrupt = (reason: string): null => {
    wireLog("agent-def", `LKG cache at ${path} ${reason} — treating as absent`, homeDir);
    return null;
  };
  try {
    const raw = JSON.parse(readFileSync(path, "utf8")) as unknown;
    if (!raw || typeof raw !== "object") return corrupt("is not a JSON object");
    const r = raw as Record<string, unknown>;
    const key = typeof r["key"] === "string" && r["key"].trim() ? r["key"].trim() : null;
    const version = parseVersion(r["version"]);
    const fetchedAt = typeof r["fetchedAt"] === "string" ? r["fetchedAt"] : "";
    if (key === null || version === null) return corrupt("is missing key/version");
    const def = r["definition"];
    if (!def || typeof def !== "object") return corrupt("is missing its definition field");
    // Re-parse via envelope so shape validation matches server responses.
    const mapped = parseAgentDefinitionResponse({
      key,
      version,
      definition: def,
    });
    if (!mapped) return corrupt("failed definition shape validation");
    try {
      validateAgentDefinition(mapped.definition);
    } catch (e) {
      return corrupt(`failed policy validation — ${e instanceof Error ? e.message : String(e)}`);
    }
    return {
      key: mapped.key,
      version: mapped.version,
      fetchedAt,
      definition: mapped.definition,
    };
  } catch (e) {
    return corrupt(`is not valid JSON — ${e instanceof Error ? e.message : String(e)}`);
  }
}

export type ResolveAgentDefinitionOptions = {
  readonly offline: boolean;
  readonly definitionKey: string;
  /** When set, used for cache path + fetch. When unset offline/no-registry → default. */
  readonly projectKey?: string;
  readonly baseUrl?: string;
  readonly apiKey?: string;
  readonly homeDir?: string;
  readonly fetchImpl?: typeof fetch;
  readonly timeoutMs?: number;
};

/**
 * Resolve definition for apply: server → LKG → DEFAULT.
 * --offline skips network, still prefers LKG over DEFAULT.
 */
export async function resolveAgentDefinitionWithLkg(
  opts: ResolveAgentDefinitionOptions,
): Promise<ResolvedAgentDefinition> {
  const homeDir = opts.homeDir ?? homedir();
  const projectKey = opts.projectKey?.trim() ?? "";
  const defKey = opts.definitionKey.trim() || DEFAULT_DEFINITION_KEY;

  // Tracks whether a live fetch actually reached the server and got a 404 (this project
  // simply has no own definition — normal), vs never reaching it at all (offline/error).
  let notFoundOnServer = false;
  // Tracks the server-reachable-but-refused case (401/403 — missing agents:read scope), as
  // opposed to a genuine network/timeout/5xx failure (wire-silent-failures-invisible, item 2 /
  // evidence 2026-07-26): both used to fall into the same "offline" bucket below.
  let forbidden = false;
  // Tracks any OTHER non-2xx status the server actually answered with (500, 503, ...) — as
  // opposed to a genuine network/timeout failure that never reached the server at all (bug:
  // doctor-reports-answering-server-unreachable, same class as
  // probe-collapses-http-errors-into-network). A 2xx-but-unparseable body is tracked separately
  // (parseError) since it is a data problem, not an HTTP error.
  let httpError: { status: number; retryAfterSeconds?: number } | undefined;
  let parseError = false;

  if (!opts.offline && projectKey && opts.baseUrl && opts.apiKey) {
    const {
      definition: fetched,
      status,
      ok,
      retryAfterSeconds,
    } = await fetchAgentDefinitionRaw({
      baseUrl: opts.baseUrl,
      projectKey,
      apiKey: opts.apiKey,
      definitionKey: defKey,
      ...(opts.timeoutMs !== undefined ? { timeoutMs: opts.timeoutMs } : {}),
      ...(opts.fetchImpl !== undefined ? { fetchImpl: opts.fetchImpl } : {}),
    });
    if (fetched) {
      writeAgentDefCache(projectKey, fetched, homeDir);
      return {
        definition: fetched.definition,
        source: "server",
        stale: false,
        key: fetched.key,
        version: fetched.version,
      };
    }
    notFoundOnServer = status === 404;
    forbidden = status === 401 || status === 403;
    if (forbidden) {
      wireLog(
        "agent-def",
        `server refused agent-def fetch for project '${projectKey}' (status ${status}) — key ` +
          `likely missing the agents:read scope; falling back`,
        homeDir,
      );
    } else if (status !== null && !ok && !notFoundOnServer) {
      httpError = { status, ...(retryAfterSeconds !== undefined ? { retryAfterSeconds } : {}) };
      wireLog(
        "agent-def",
        `server answered agent-def fetch for project '${projectKey}' with HTTP ${status} ` +
          `(not offline/unreachable) — falling back`,
        homeDir,
      );
    } else if (status !== null && ok && !fetched) {
      parseError = true;
    }
  }

  // LKG before DEFAULT (definition-offline-lkg).
  if (projectKey) {
    const cached = readAgentDefCache(projectKey, homeDir);
    if (cached) {
      // Prefer matching key when possible; still use cache if only one project doc was stored.
      if (cached.key === defKey || defKey === DEFAULT_DEFINITION_KEY) {
        return {
          definition: cached.definition,
          source: "lkg",
          stale: true,
          key: cached.key,
          version: cached.version,
          staleMarker: AGENT_DEF_STALE_MARKER,
          ...(forbidden ? { forbidden: true } : {}),
          ...(httpError ? { httpError } : {}),
          ...(parseError ? { parseError: true } : {}),
        };
      }
    }
  }

  return {
    definition: DEFAULT_AGENT_DEFINITION,
    source: "default",
    stale: false,
    ...(notFoundOnServer ? { notFoundOnServer: true } : {}),
    ...(forbidden ? { forbidden: true } : {}),
    ...(httpError ? { httpError } : {}),
    ...(parseError ? { parseError: true } : {}),
  };
}

/**
 * One-line SessionStart banner marker for a definition that did NOT come straight from the
 * live server (bug: wire-silent-failures-invisible — "Пометить деградацию в баннере"). Reuses
 * the SAME ResolvedAgentDefinition resolveApplyDefinition/resolveAgentDefinitionForSession
 * already produce; this is a formatter, not a second resolver.
 *
 * Before this, `source: "lkg"` carried an explicit staleMarker but no injector (pull-memory.ts,
 * droid-pull-memory.ts, opencode-plugin.ts) ever put it in the banner text — only apply/doctor's
 * OWN logs mentioned staleness. And `source: "default"` always reported `stale: false`, so a
 * built-in-fallback definition (server AND LKG both unavailable/refused) looked byte-for-byte
 * like a healthy live fetch to whoever read the banner. Returns "" when the banner needs no
 * note (source === "server" — the healthy case).
 */
export function agentDefinitionBannerNote(resolved: ResolvedAgentDefinition): string {
  if (resolved.source === "server") return "";
  if (resolved.source === "lkg") {
    if (resolved.forbidden) {
      return "⚠ definition: using stale LKG cache — server refused the request (401/403, check API key scopes), not merely offline.";
    }
    if (resolved.httpError) {
      return `⚠ definition: using stale LKG cache — server answered with HTTP ${resolved.httpError.status}, not merely offline.`;
    }
    return resolved.staleMarker ?? AGENT_DEF_STALE_MARKER;
  }
  // source === "default"
  if (resolved.notFoundOnServer) {
    return "ℹ definition: no server-side definition for this project yet — using kit default baseline.";
  }
  if (resolved.forbidden) {
    return "⚠ definition: built-in fallback — server refused the request (401/403, check API key scopes), not merely offline.";
  }
  if (resolved.httpError) {
    return `⚠ definition: built-in fallback — server answered with HTTP ${resolved.httpError.status}, not merely offline.`;
  }
  return "⚠ definition: built-in fallback (server/LKG unavailable).";
}

/** Minimal shape SessionStart injectors already have from registry.ts's resolveProject. */
export type SessionProjectRef = {
  readonly project: string;
  readonly baseUrl: string;
  readonly apiKey: string;
};

/**
 * Resolve the agent definition for a SessionStart banner — the SAME server → LKG → DEFAULT
 * order `apply` uses (resolveAgentDefinitionWithLkg), pinned to the default definition key.
 * This closes the asymmetry where subagent role files already got server-authored notes via
 * `apply` but the main-loop banner (protocol.ts) was stuck on the hard-coded built-in default.
 *
 * Bounded by AGENT_DEF_FETCH_TIMEOUT_MS (same budget as the canon fetch already on this path,
 * see canon.ts) so a wedged network never turns into an unbounded session-start stall — the
 * abort falls through to the LKG cache, then the built-in default, never a crash or blank
 * banner. Callers SHOULD run this concurrently with fetchCanonBlock (e.g. via Promise.all)
 * rather than awaiting it first, so the two 8s budgets don't stack serially.
 */
export async function resolveAgentDefinitionForSession(
  resolved: SessionProjectRef,
  opts?: { homeDir?: string; fetchImpl?: typeof fetch; timeoutMs?: number },
): Promise<ResolvedAgentDefinition> {
  return resolveAgentDefinitionWithLkg({
    offline: false,
    definitionKey: DEFAULT_DEFINITION_KEY,
    projectKey: resolved.project,
    baseUrl: resolved.baseUrl,
    apiKey: resolved.apiKey,
    ...(opts?.homeDir !== undefined ? { homeDir: opts.homeDir } : {}),
    ...(opts?.fetchImpl !== undefined ? { fetchImpl: opts.fetchImpl } : {}),
    ...(opts?.timeoutMs !== undefined ? { timeoutMs: opts.timeoutMs } : {}),
  });
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

  const slug = typeof r["slug"] === "string" && r["slug"].trim() ? r["slug"].trim() : null;
  const tier = typeof r["tier"] === "string" && r["tier"].trim() ? r["tier"].trim() : null;
  if (slug === null || tier === null) return null;

  if (!Array.isArray(r["requiredCapabilities"])) return null;
  const requiredCapabilities: string[] = [];
  for (const c of r["requiredCapabilities"]) {
    if (typeof c !== "string") return null;
    requiredCapabilities.push(c);
  }

  const spawn = mapSpawn(r["spawn"]);
  if (spawn === "invalid") return null;
  const escalation = mapEscalation(r["escalation"]);
  if (escalation === "invalid") return null;

  const notes =
    typeof r["notes"] === "string" && r["notes"].trim() ? r["notes"].trim() : undefined;

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
  if (typeof s["allowed"] !== "boolean") return "invalid";
  let allowedRoles: string[] | undefined;
  if (s["allowedRoles"] !== undefined && s["allowedRoles"] !== null) {
    if (!Array.isArray(s["allowedRoles"])) return "invalid";
    allowedRoles = [];
    for (const x of s["allowedRoles"]) {
      if (typeof x !== "string") return "invalid";
      allowedRoles.push(x);
    }
  }
  return allowedRoles !== undefined
    ? { allowed: s["allowed"], allowedRoles }
    : { allowed: s["allowed"] };
}

function mapEscalation(raw: unknown): RoleEscalation | null | "invalid" {
  if (raw === undefined || raw === null) return null;
  if (typeof raw !== "object") return "invalid";
  const e = raw as Record<string, unknown>;
  if (typeof e["available"] !== "boolean") return "invalid";
  let targets: string[] | undefined;
  if (e["targets"] !== undefined && e["targets"] !== null) {
    if (!Array.isArray(e["targets"])) return "invalid";
    targets = [];
    for (const x of e["targets"]) {
      if (typeof x !== "string") return "invalid";
      targets.push(x);
    }
  }
  return targets !== undefined
    ? { available: e["available"], targets }
    : { available: e["available"] };
}
