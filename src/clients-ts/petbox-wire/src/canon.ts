// Shared memory-canon fetch + offline cache — the ONE implementation both SessionStart hooks
// (pull-memory.ts for Claude Code, opencode-plugin.ts for opencode) use, so the injected canon
// block is byte-identical across agents (spec: agent-wiring, wiring-canon-inject).
//
// The server exposes the curated memory index (canon) at
//   GET {baseUrl}/api/memory/{project}/canon   (header X-Api-Key)
//   → 200 { "project": {body,updatedAt,version}|null, "workspace": {...}|null }
// A LEG that was queried and has nothing curated yet is NOT null — MemoryApi.CanonAsync (card
// canon-invisible-and-unfed) answers it with a fixed nudge body and Version 0 instead, so the
// empty state is visible rather than silent. null stays reserved for a leg this caller cannot
// see at all (no workspace, or hidden by sandbox containment). See legStatus()/EMPTY_CANON_VERSION
// below for how this kit tells that nudge apart from real curated text.
// We turn that into a markdown block appended to the session context. On any failure we fall
// back to a local cache (~/.petbox/cache/{project}.canon.md) written on the last good fetch,
// marked stale. This is best-effort and TOTAL: every path returns string | null, never throws.
//
// NOTE: production may not have this endpoint yet, or may still be on the pre-fix build (empty
// leg → null) — either way a 404/error/null degrades gracefully (no canon block, the memory
// protocol is still injected by the caller).
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

// The server's discriminator for "this leg was queried but nothing is curated yet" (card
// canon-invisible-and-unfed): MemoryApi.CanonAsync answers an empty/absent store with a
// CanonPart carrying a fixed nudge string AND Version 0. Version 0 is never assigned to a real
// entry (TemporalStore's version cursor starts at 1 on first insert), so it is a safe, wording-
// independent signal — this kit does not need to string-match the marker text to recognize it.
const EMPTY_CANON_VERSION = 0;

function cacheDir(): string {
  return join(homedir(), ".petbox", "cache");
}

function cachePath(project: string): string {
  return join(cacheDir(), `${project}.canon.md`);
}

type LegStatus =
  | { kind: "absent" } // leg was null, or body missing/blank — pre-fix server, or a leg never asked
  | { kind: "empty"; marker: string } // leg was queried and is curated-empty (Version 0 nudge)
  | { kind: "content"; body: string }; // leg carries real curated text

// Classify one canon leg. A part with a non-blank body and Version 0 is the server's
// empty-canon nudge, not real project/workspace knowledge — it must never be presented (or
// cached) as though it were curated content.
function legStatus(part: CanonPart | null | undefined): LegStatus {
  if (!part || typeof part.body !== "string") return { kind: "absent" };
  const body = part.body.trim();
  if (body.length === 0) return { kind: "absent" };
  if (part.version === EMPTY_CANON_VERSION) return { kind: "empty", marker: body };
  return { kind: "content", body };
}

// Assemble the canon block from the two parts. Returns null when both legs are absent
// (pre-fix server, or neither leg has ever been queried/curated).
//
// `hasContent` tells the caller whether the block carries REAL curated text — the empty-canon
// nudge alone does NOT count, so fetchCanonBlock below knows not to let it displace a
// previously cached real canon (see that function's comment on cache stickiness).
function buildBlock(project: string, resp: CanonResponse | null): { text: string; hasContent: boolean } | null {
  if (!resp) return null;
  const projectLeg = legStatus(resp.project);
  const workspaceLeg = legStatus(resp.workspace);
  if (projectLeg.kind === "absent" && workspaceLeg.kind === "absent") return null;

  const hasContent = projectLeg.kind === "content" || workspaceLeg.kind === "content";

  // Both legs share the SAME fixed marker text when both are empty — collecting into a Set
  // dedups that case down to the one line the card's acceptance criterion asks for, instead of
  // printing the identical nudge twice (which would read as two facts, not one instruction).
  const markers = new Set<string>();
  if (projectLeg.kind === "empty") markers.add(projectLeg.marker);
  if (workspaceLeg.kind === "empty") markers.add(workspaceLeg.marker);

  let out = `## PetBox memory canon`;
  if (hasContent) {
    out += `\n\nThe curated memory index (canon) for this project — pointers to durable facts; pull full bodies via memory_get/memory_search.`;
  }
  // Real content is attributed to its leg with a heading — it IS a fact about the
  // project/workspace. The empty-canon nudge is deliberately NOT put under a "### Project
  // (name)" heading: that framing would make an instruction ("curate with memory_upsert") read
  // as though it were a fact ABOUT the named project, which is exactly what the card's
  // acceptance criterion rules out.
  if (projectLeg.kind === "content") {
    out += `\n\n### Project (${project})\n\n${projectLeg.body}`;
  }
  if (workspaceLeg.kind === "content") {
    out += `\n\n### Workspace\n\n${workspaceLeg.body}`;
  }
  for (const marker of markers) {
    out += `\n\n${marker}`;
  }
  return { text: out, hasContent };
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
// returned, and cached IFF it carries real curated content; on failure a cached block (if any)
// is returned PREFIXED with a stale marker. Returns null when there is nothing to show (fetch
// failed AND no cache, or both canon legs are absent). Never throws.
export async function fetchCanonBlock(
  resolved: ResolvedProject,
  opts?: { timeoutMs?: number },
): Promise<string | null> {
  try {
    const result = await fetchCanon(resolved, opts?.timeoutMs);
    if (result.ok) {
      // Successful fetch — the server is authoritative, so the CURRENT state (marker or real
      // content) is always what gets shown this session. Caching, though, is gated on
      // `hasContent`: the empty-canon nudge is a live instruction about the server's state
      // right now, not durable project knowledge, so it must never overwrite (or stand in for)
      // a previously cached REAL canon. Without this gate, curating the canon and then hitting
      // one network blip would resurrect the stale "canon is empty — curate" nudge over the
      // real content that now exists (canon-invisible-and-unfed: a stickier, actively
      // misleading staleness than ordinary stale-but-still-true cached content).
      const block = buildBlock(resolved.project, result.resp);
      if (block !== null && block.hasContent) await writeCache(resolved.project, block.text);
      return block !== null ? block.text : null;
    }
    // Fetch failed (endpoint absent / unreachable) → fall back to the offline cache if present.
    // The cache, by construction above, only ever holds real content — never a stale marker.
    const cached = await readCache(resolved.project);
    if (cached !== null) return `${STALE_MARKER}\n\n${cached}`;
    return null;
  } catch {
    return null; // total: any unexpected error → no canon block
  }
}
