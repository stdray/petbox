// Shared memory-canon fetch + offline cache — the ONE implementation both SessionStart hooks
// (pull-memory.ts for Claude Code, opencode-plugin.ts for opencode) use, so the injected canon
// block is byte-identical across agents (spec: agent-wiring, wiring-canon-inject).
//
// The server exposes the curated memory index (canon) at
//   GET {baseUrl}/api/memory/{project}/canon   (header X-Api-Key)
//   → 200 { "project": {body,updatedAt,version}|null, "workspace": {...}|null }
// We turn that into a markdown block appended to the session context. On any failure we fall
// back to a local cache (~/.petbox/cache/{project}.canon.md) written on the last good fetch,
// marked stale. This is best-effort and TOTAL: every path returns string | null, never throws.
//
// NOTE: production may not have this endpoint yet — a 404/error degrades gracefully (no canon
// block, the memory protocol is still injected by the caller).
//
// Plain TS for native node type-stripping: no enum/namespace/parameter-properties, type-only
// imports, zero deps.

import { mkdir, readFile, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import { join } from "node:path";
import type { ResolvedProject } from "./registry.ts";

const FETCH_TIMEOUT_MS = 8000;

const STALE_MARKER = "⚠ Canon below is from the local cache (PetBox unreachable) — may be stale.";

type CanonPart = { body?: unknown; updatedAt?: unknown; version?: unknown };
type CanonResponse = { project?: CanonPart | null; workspace?: CanonPart | null };

function cacheDir(): string {
  return join(homedir(), ".petbox", "cache");
}

function cachePath(project: string): string {
  return join(cacheDir(), `${project}.canon.md`);
}

// Pull a usable markdown body out of a canon part, or null when the part is missing/empty.
function partBody(part: CanonPart | null | undefined): string | null {
  if (!part || typeof part.body !== "string") return null;
  const body = part.body.trim();
  return body.length > 0 ? body : null;
}

// Assemble the canon block from the two parts. Returns null when both are empty.
function buildBlock(project: string, resp: CanonResponse | null): string | null {
  if (!resp) return null;
  const projectBody = partBody(resp.project);
  const workspaceBody = partBody(resp.workspace);
  if (projectBody === null && workspaceBody === null) return null;

  let out = `## PetBox memory canon

The curated memory index (canon) for this project — pointers to durable facts; pull full bodies via memory_get/memory_search.`;
  if (projectBody !== null) {
    out += `\n\n### Project (${project})\n\n${projectBody}`;
  }
  if (workspaceBody !== null) {
    out += `\n\n### Workspace\n\n${workspaceBody}`;
  }
  return out;
}

// Returns { ok: true, resp } on a successful HTTP fetch (resp may still carry empty canon),
// or { ok: false } on any failure (404 endpoint-absent / 401 / 5xx / network / timeout / bad
// JSON) — the caller uses ok to decide whether to fall back to the stale offline cache.
async function fetchCanon(
  resolved: ResolvedProject,
  timeoutMs: number = FETCH_TIMEOUT_MS,
): Promise<{ ok: true; resp: CanonResponse | null } | { ok: false }> {
  const ctrl = new AbortController();
  // timeoutMs <= 0 (budget already exhausted by a prior sequential fetch, e.g. pull-memory.ts's
  // shared session-start budget) aborts on the next tick — same effect as skipping the network
  // call, degrading straight to the offline cache below rather than blocking.
  const timer = setTimeout(() => ctrl.abort(), Math.max(0, timeoutMs));
  try {
    const url = `${resolved.baseUrl}/api/memory/${resolved.project}/canon`;
    const resp = await fetch(url, {
      method: "GET",
      // Connection: close so this socket doesn't linger keep-alive after the response —
      // a SessionStart/Stop hook process exits right after this fetch, and a kept-alive
      // socket is a libuv handle that either stalls natural process exit for seconds or
      // races a forced process.exit() against the handle's own close teardown (the crash
      // this header exists to prevent; see pull-memory.ts's exit comment).
      headers: { "X-Api-Key": resolved.apiKey, Connection: "close" },
      signal: ctrl.signal,
    });
    if (!resp.ok) return { ok: false }; // 404 (endpoint absent) / 401 / 5xx → degrade to cache
    const j = (await resp.json().catch(() => null)) as CanonResponse | null;
    return { ok: true, resp: j };
  } catch {
    return { ok: false }; // network/timeout → degrade to cache
  } finally {
    clearTimeout(timer);
  }
}

// Server-side canon budget (MemoryService.cs's CanonBodyBudget) — cited here only for display
// (e.g. status's "N of 10k chars"), never enforced client-side.
export const CANON_BODY_BUDGET_CHARS = 10000;

/**
 * One canon leg's state, told from `version` — NEVER from comparing body text against the
 * server's EmptyCanonMarker string (MemoryApi.cs): that string is server prose, not a contract,
 * and could be reworded without notice. `version === 0` is the server's actual signal for "the
 * store/entry does not exist yet, this is the curation-nudge placeholder" (MemoryApi.cs's
 * ReadCanonAsync); any version > 0 is a real curated entry, and `chars` is its length as a
 * concrete "how close to the 10k budget" fact for a human to act on. A null part (leg never asked
 * — no workspace — or withheld by sandbox containment) is "absent", distinct from "empty".
 */
export type CanonLegState =
  | { readonly kind: "absent" }
  | { readonly kind: "empty" }
  | { readonly kind: "content"; readonly chars: number };

function classifyCanonPart(part: CanonPart | null | undefined): CanonLegState {
  if (!part || typeof part.body !== "string") return { kind: "absent" };
  const version = typeof part.version === "number" ? part.version : null;
  if (version === 0) return { kind: "empty" };
  return { kind: "content", chars: part.body.length };
}

export type CanonLegsResult =
  | { readonly ok: true; readonly project: CanonLegState; readonly workspace: CanonLegState }
  | { readonly ok: false };

/**
 * Per-leg canon state for `status` (absent | empty | content, see CanonLegState) — the SAME fetch
 * fetchCanonBlock uses (fetchCanon below), just returned unshaped instead of pre-joined into a
 * markdown block, and with no LKG fallback: `status` reports what the SERVER says right now (a
 * degraded/unreachable server is its own fact to show, not something to paper over with a stale
 * cache — unlike the injected SessionStart block, which prefers showing something over nothing).
 * `{ ok: false }` on any fetch failure (network/timeout/non-2xx/bad JSON) — never throws.
 */
export async function fetchCanonLegs(
  resolved: ResolvedProject,
  opts?: { timeoutMs?: number },
): Promise<CanonLegsResult> {
  const result = await fetchCanon(resolved, opts?.timeoutMs);
  if (!result.ok) return { ok: false };
  return {
    ok: true,
    project: classifyCanonPart(result.resp?.project),
    workspace: classifyCanonPart(result.resp?.workspace),
  };
}

async function writeCache(project: string, block: string): Promise<void> {
  try {
    await mkdir(cacheDir(), { recursive: true });
    await writeFile(cachePath(project), block, "utf8");
  } catch {
    // best-effort: a failed cache write must not affect the returned block
  }
}

async function readCache(project: string): Promise<string | null> {
  try {
    const body = await readFile(cachePath(project), "utf8");
    return body.trim().length > 0 ? body : null;
  } catch {
    return null; // no cache file yet
  }
}

// Build the canon block for a resolved project. On a successful fetch the fresh block is
// cached and returned; on failure a cached block (if any) is returned PREFIXED with a stale
// marker. Returns null when there is nothing to show (fetch failed AND no cache, or both
// canon parts are empty). Never throws.
export async function fetchCanonBlock(
  resolved: ResolvedProject,
  opts?: { timeoutMs?: number },
): Promise<string | null> {
  try {
    const result = await fetchCanon(resolved, opts?.timeoutMs);
    if (result.ok) {
      // Successful fetch — the server is authoritative. A real block is cached and returned;
      // an empty canon returns null (do NOT show a stale cache when the server says "nothing").
      const block = buildBlock(resolved.project, result.resp);
      if (block !== null) await writeCache(resolved.project, block);
      return block;
    }
    // Fetch failed (endpoint absent / unreachable) → fall back to the offline cache if present.
    const cached = await readCache(resolved.project);
    if (cached !== null) return `${STALE_MARKER}\n\n${cached}`;
    return null;
  } catch {
    return null; // total: any unexpected error → no canon block
  }
}
